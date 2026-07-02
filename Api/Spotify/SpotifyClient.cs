using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace Api.Spotify;

/// Spotify Web API 클라이언트. Client Credentials flow(앱 토큰)로 카탈로그 검색.
/// 토큰은 ~1시간 유효 → 캐시하고 만료 시 갱신. 싱글톤 등록이라 토큰 캐시가 공유된다.
/// 제약(기획안 §5): 음원 스트리밍·AI 학습·Audio Features/Analysis·이미지 파일 저장 금지.
public sealed class SpotifyClient(IHttpClientFactory factory, IConfiguration config, ISpotifyResponseCache cache)
{
    private readonly string? _clientId = config.GetSection("SPOTIFY")["CLIENT_ID"];
    private readonly string? _clientSecret = config.GetSection("SPOTIFY")["CLIENT_SECRET"];
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;

    // 서킷 브레이커: 429를 받으면 이 시각까지 Spotify를 아예 호출하지 않는다(fast-fail).
    // 버스트가 penalty를 계속 리셋/연장하는 악순환을 끊고, Retry-After 동안 실제로 decay되게 함.
    // Retry-After가 수 시간(확장 penalty)일 수 있어 앱 전체를 그만큼 막지 않도록 최대치로 캡하고,
    // 캡 이후엔 소수의 프로브만 나가 penalty를 자연 만료시킨다.
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;

    // 429 발생 시 관리자 모니터링용으로 실제 Retry-After를 DB(spotify_status)에 기록.
    private readonly string? _dbConnString = BuildDbConnString(config);

    public bool Configured => !string.IsNullOrEmpty(_clientId) && !string.IsNullOrEmpty(_clientSecret);

    /// 검색. 원시 JSON 문자열을 반환 — 호출부가 자기 스코프에서 파싱(JsonDocument 수명 문제 회피).
    /// 트랙은 external_ids.isrc 포함. 앨범 upc 는 search 응답엔 없어 GET /albums/{id} 필요(NON-5).
    public async Task<string> SearchAsync(string query, string types, int limit, int offset, CancellationToken ct)
    {
        var url = $"https://api.spotify.com/v1/search?q={Uri.EscapeDataString(query)}" +
                  $"&type={Uri.EscapeDataString(types)}&limit={limit}&offset={offset}";
        var (status, body) = await RequestAsync(url, ct);
        if (status != HttpStatusCode.OK)
            throw new HttpRequestException($"Spotify {(int)status} for {url} :: {body}");
        return body;
    }

    /// 임의 GET 엔드포인트(tracks/{id}, albums/{id}, artists/{id} 등). 원시 JSON 반환.
    /// 404 → null(존재하지 않는 리소스). 그 외 비정상 → HttpRequestException.
    public async Task<string?> GetAsync(string path, CancellationToken ct)
    {
        var url = $"https://api.spotify.com/v1/{path}";
        var (status, body) = await RequestAsync(url, ct);
        if (status == HttpStatusCode.NotFound) return null;
        if (status != HttpStatusCode.OK)
            throw new HttpRequestException($"Spotify {(int)status} for {url} :: {body}");
        return body;
    }

    // 공통 GET. 캐시 히트면 Spotify 미호출(서킷/429 무관하게 서빙), 미스면 조회 후 200만 캐시.
    // 서킷 오픈 중(429 penalty)이면 호출 없이 즉시 실패, 429면 Retry-After(캡)만큼 서킷을 연다.
    private async Task<(HttpStatusCode Status, string Body)> RequestAsync(string url, CancellationToken ct)
    {
        var ttl = url.Contains("/search", StringComparison.Ordinal) ? TimeSpan.FromHours(1) : TimeSpan.FromHours(12);
        var cached = await cache.GetAsync(url, ttl, ct);
        if (cached is not null) return (HttpStatusCode.OK, cached);

        if (DateTimeOffset.UtcNow < _blockedUntil)
            throw new HttpRequestException($"Spotify rate-limited (circuit open until {_blockedUntil:O})");

        var token = await GetTokenAsync(ct);
        using var http = factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        if ((int)res.StatusCode == 429)
        {
            var realRetry = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
            var circuitRetry = realRetry > MaxBackoff ? MaxBackoff : realRetry; // 회로는 짧게(캡)
            _blockedUntil = DateTimeOffset.UtcNow.Add(circuitRetry);
            await WriteRateLimitStatusAsync(DateTimeOffset.UtcNow.Add(realRetry), (int)realRetry.TotalSeconds, ct);
            throw new HttpRequestException(
                $"Spotify 429 — backing off {circuitRetry.TotalSeconds:F0}s (Retry-After {realRetry.TotalSeconds:F0})");
        }

        if (res.StatusCode == HttpStatusCode.OK)
            await cache.SetAsync(url, body, ct); // 200만 캐시(404/에러는 캐시 안 함)

        return (res.StatusCode, body);
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _expiresAt) return _token;
        await _lock.WaitAsync(ct);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _expiresAt) return _token;
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_clientId}:{_clientSecret}"));
            using var http = factory.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token");
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            req.Content = new FormUrlEncodedContent(
                new Dictionary<string, string> { ["grant_type"] = "client_credentials" });
            using var res = await http.SendAsync(req, ct);
            res.EnsureSuccessStatusCode();
            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;
            _token = root.GetProperty("access_token").GetString()!;
            var expiresIn = root.GetProperty("expires_in").GetInt32();
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60); // 만료 60초 전 갱신
            return _token;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static string? BuildDbConnString(IConfiguration config)
    {
        var db = config.GetSection("DATABASE");
        if (string.IsNullOrEmpty(db["HOST"]) || string.IsNullOrEmpty(db["PASSWORD"])) return null;
        return new NpgsqlConnectionStringBuilder
        {
            Host = db["HOST"],
            Port = int.TryParse(db["PORT"], out var p) ? p : 5432,
            Database = string.IsNullOrEmpty(db["DATABASE"]) ? "postgres" : db["DATABASE"],
            Username = string.IsNullOrEmpty(db["USER"]) ? "postgres" : db["USER"],
            Password = db["PASSWORD"],
            SslMode = SslMode.Require,
        }.ConnectionString;
    }

    // 관리자 모니터링용 429 상태 기록(best-effort). 실패해도 무시.
    private async Task WriteRateLimitStatusAsync(DateTimeOffset blockedUntil, int retryAfterSeconds, CancellationToken ct)
    {
        if (_dbConnString is null) return;
        try
        {
            await using var conn = new NpgsqlConnection(_dbConnString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                """
                insert into public.spotify_status (id, blocked_until, retry_after_seconds, updated_at)
                values (1, @b, @r, now())
                on conflict (id) do update set blocked_until = excluded.blocked_until,
                    retry_after_seconds = excluded.retry_after_seconds, updated_at = now()
                """, conn);
            cmd.Parameters.AddWithValue("b", blockedUntil);
            cmd.Parameters.AddWithValue("r", retryAfterSeconds);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (NpgsqlException) { /* best-effort */ }
    }
}
