using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
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
    // DateTimeOffset(멀티워드 struct)은 원자적 읽기/쓰기 보장이 없어, 429 쓰기와 매 캐시 미스의 락-프리 읽기가
    // 겹치면 torn read(쓰레기 타임스탬프)가 날 수 있다 → UTC ticks(long)로 저장하고 Volatile로 접근(QA4-10).
    private long _blockedUntilTicks = DateTimeOffset.MinValue.UtcTicks;

    // 429 발생 시 관리자 모니터링용으로 실제 Retry-After를 DB(spotify_status)에 기록.
    private readonly string? _dbConnString = BuildDbConnString(config);

    // L1 전역 토큰 버킷 레이트 리미터(NON-41) — 429가 오기 전에 우리가 먼저 안전 속도로 제한.
    // 정확한 Spotify 한도는 비공개(롤링 30초 창)라 RPS는 config로 튜닝(SPOTIFY:RATE_LIMIT_RPS, 기본 10).
    private readonly TokenBucketRateLimiter _limiter = BuildLimiter(config);

    // L2 single-flight(NON-41) — 같은 URL 동시 캐시 미스를 Spotify 호출 1번으로 합침(스탬피드 방지).
    private readonly ConcurrentDictionary<string, Lazy<Task<(HttpStatusCode Status, string Body)>>> _inflight = new();

    public bool Configured => !string.IsNullOrEmpty(_clientId) && !string.IsNullOrEmpty(_clientSecret);

    private static TokenBucketRateLimiter BuildLimiter(IConfiguration config)
    {
        var rps = int.TryParse(config.GetSection("SPOTIFY")["RATE_LIMIT_RPS"], out var r) && r > 0 ? r : 10;
        return new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = rps * 2,       // 버스트 허용치
            TokensPerPeriod = rps,      // 초당 보충
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            QueueLimit = 500,           // 대기 큐(초과 시 fast-fail)
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        });
    }

    /// 검색. 원시 JSON 문자열을 반환 — 호출부가 자기 스코프에서 파싱(JsonDocument 수명 문제 회피).
    /// 트랙은 external_ids.isrc 포함. 앨범 upc 는 search 응답엔 없어 GET /albums/{id} 필요(NON-5).
    public async Task<string> SearchAsync(string query, string types, int limit, int offset, CancellationToken ct)
    {
        // market=KR: explicit 플래그가 market 없이는 항상 false(실측) + KR 카탈로그 정합. id/isrc 유지.
        var url = $"https://api.spotify.com/v1/search?q={Uri.EscapeDataString(query)}" +
                  $"&type={Uri.EscapeDataString(types)}&limit={limit}&offset={offset}&market=KR";
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
    // 서킷 오픈 중(429 penalty)이면 호출 없이 즉시 실패. 동시 미스는 single-flight로 1콜에 합침(NON-41 L2).
    private async Task<(HttpStatusCode Status, string Body)> RequestAsync(string url, CancellationToken ct)
    {
        var ttl = url.Contains("/search", StringComparison.Ordinal) ? TimeSpan.FromHours(1) : TimeSpan.FromHours(12);
        var cached = await cache.GetAsync(url, ttl, ct);
        if (cached is not null) return (HttpStatusCode.OK, cached);

        var blockedUntilTicks = Volatile.Read(ref _blockedUntilTicks);
        if (DateTimeOffset.UtcNow.UtcTicks < blockedUntilTicks)
            throw new HttpRequestException($"Spotify rate-limited (circuit open until {new DateTimeOffset(blockedUntilTicks, TimeSpan.Zero):O})");

        // single-flight: 같은 url 동시 미스는 공유 fetch 1개를 await. 공유 fetch는 caller ct와 분리
        // (한 caller가 취소해도 나머지 대기자에겐 결과가 살아있게). 각 caller는 자기 ct로 '대기'만 취소.
        var lazy = _inflight.GetOrAdd(url, u => new Lazy<Task<(HttpStatusCode, string)>>(() =>
        {
            var t = FetchAsync(u);
            _ = t.ContinueWith(completed => _inflight.TryRemove(u, out _), TaskScheduler.Default);
            return t;
        }));
        return await lazy.Value.WaitAsync(ct);
    }

    // 실제 Spotify 조회(single-flight 내부). L1 레이트리미터 통과 → 토큰 → HTTP → 429면 서킷 오픈 → 200만 캐시.
    private async Task<(HttpStatusCode Status, string Body)> FetchAsync(string url)
    {
        using var lease = await _limiter.AcquireAsync(1, CancellationToken.None);
        if (!lease.IsAcquired)
            throw new HttpRequestException("Spotify local rate limiter queue full");

        // 리미터 대기 중 서킷이 열렸을 수 있어 재확인(불필요한 호출 방지).
        var blockedUntilTicks = Volatile.Read(ref _blockedUntilTicks);
        if (DateTimeOffset.UtcNow.UtcTicks < blockedUntilTicks)
            throw new HttpRequestException($"Spotify rate-limited (circuit open until {new DateTimeOffset(blockedUntilTicks, TimeSpan.Zero):O})");

        var token = await GetTokenAsync(CancellationToken.None);
        using var http = factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await http.SendAsync(req, CancellationToken.None);
        var body = await res.Content.ReadAsStringAsync(CancellationToken.None);

        if ((int)res.StatusCode == 429)
        {
            var realRetry = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
            var circuitRetry = realRetry > MaxBackoff ? MaxBackoff : realRetry; // 회로는 짧게(캡)
            Volatile.Write(ref _blockedUntilTicks, DateTimeOffset.UtcNow.Add(circuitRetry).UtcTicks);
            await WriteRateLimitStatusAsync(DateTimeOffset.UtcNow.Add(realRetry), (int)realRetry.TotalSeconds, CancellationToken.None);
            throw new HttpRequestException(
                $"Spotify 429 — backing off {circuitRetry.TotalSeconds:F0}s (Retry-After {realRetry.TotalSeconds:F0})");
        }

        if (res.StatusCode == HttpStatusCode.OK)
            await cache.SetAsync(url, body, CancellationToken.None); // 200만 캐시(404/에러는 캐시 안 함)

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
