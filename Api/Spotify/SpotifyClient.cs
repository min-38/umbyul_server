using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Api.Spotify;

/// Spotify Web API 클라이언트. Client Credentials flow(앱 토큰)로 카탈로그 검색.
/// 토큰은 ~1시간 유효 → 캐시하고 만료 시 갱신. 싱글톤 등록이라 토큰 캐시가 공유된다.
/// 제약(기획안 §5): 음원 스트리밍·AI 학습·Audio Features/Analysis·이미지 파일 저장 금지.
public sealed class SpotifyClient(IHttpClientFactory factory, IConfiguration config)
{
    private readonly string? _clientId = config.GetSection("SPOTIFY")["CLIENT_ID"];
    private readonly string? _clientSecret = config.GetSection("SPOTIFY")["CLIENT_SECRET"];
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;

    public bool Configured => !string.IsNullOrEmpty(_clientId) && !string.IsNullOrEmpty(_clientSecret);

    /// 검색. 원시 JSON 문자열을 반환 — 호출부가 자기 스코프에서 파싱(JsonDocument 수명 문제 회피).
    /// 트랙은 external_ids.isrc 포함. 앨범 upc 는 search 응답엔 없어 GET /albums/{id} 필요(NON-5).
    public async Task<string> SearchAsync(string query, string types, int limit, int offset, CancellationToken ct)
    {
        var token = await GetTokenAsync(ct);
        var url = $"https://api.spotify.com/v1/search?q={Uri.EscapeDataString(query)}" +
                  $"&type={Uri.EscapeDataString(types)}&limit={limit}&offset={offset}";
        using var http = factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Spotify {(int)res.StatusCode} for {url} :: {body}");
        }
        return await res.Content.ReadAsStringAsync(ct);
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
}
