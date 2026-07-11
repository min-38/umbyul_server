using Api.Announcements;
using Api.Feed;
using Api.Sets;

namespace Api.Tests;

// QA7-1: 보안 가드 5종(순수 로직) 회귀 잠금.
public class SecurityGuardTests
{
    // 1) 뷰어 IP 해석 — raw XFF는 스푸핑 가능하므로 무시, RemoteIpAddress만 신뢰(QA4-3).
    [Fact]
    public void ResolveClientIp_ignores_xff() =>
        Assert.Equal("10.0.0.1", AnnouncementEndpoints.ResolveClientIp("1.2.3.4", "10.0.0.1"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ResolveClientIp_null_remote_is_unknown(string? remote) =>
        Assert.Equal("unknown", AnnouncementEndpoints.ResolveClientIp("1.2.3.4", remote));

    [Fact]
    public void HashViewer_is_16_hex_deterministic()
    {
        var a = AnnouncementEndpoints.HashViewer("10.0.0.1");
        Assert.Equal(16, a.Length);
        Assert.Matches("^[0-9A-F]{16}$", a);
        Assert.Equal(a, AnnouncementEndpoints.HashViewer("10.0.0.1")); // 결정적
    }

    [Fact]
    public void HashViewer_differs_per_ip() =>
        Assert.NotEqual(AnnouncementEndpoints.HashViewer("10.0.0.1"), AnnouncementEndpoints.HashViewer("10.0.0.2"));

    // 2) 미디어 프록시 키 — announcements/ 접두 + 경로 이탈 차단(Ordinal 대소문자).
    [Theory]
    [InlineData("announcements/abc.png", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("avatars/x", false)]
    [InlineData("announcements/../secret", false)]
    [InlineData("announcements/a/../../x", false)]
    [InlineData("Announcements/x", false)] // Ordinal 대소문자 구분
    public void IsValidAnnouncementKey_cases(string? key, bool expected) =>
        Assert.Equal(expected, AnnouncementEndpoints.IsValidAnnouncementKey(key));

    // 3) 이미지 타입 화이트리스트 — SVG 거부가 핵심 보안 어서션.
    [Theory]
    [InlineData("image/jpeg", "jpg")]
    [InlineData("image/png", "png")]
    [InlineData("image/webp", "webp")]
    [InlineData("image/gif", "gif")]
    public void ImageTypes_allows_known(string ct, string ext) =>
        Assert.Equal(ext, AnnouncementEndpoints.ImageTypes[ct]);

    [Theory]
    [InlineData("image/jpg")]
    [InlineData("image/svg+xml")]
    [InlineData("")]
    public void ImageTypes_rejects_others(string ct) =>
        Assert.False(AnnouncementEndpoints.ImageTypes.ContainsKey(ct));

    // 4) 피드 정렬 화이트리스트 — SQL 보간 안전. 모든 출력이 ", id"로 끝나 페이지 안정성.
    [Theory]
    [InlineData("newest", "created_at desc, id")]
    [InlineData("likes", "likes desc, created_at desc, id")]
    [InlineData("ratio", "wilson desc, likes desc, id")]
    [InlineData("rising", "recent desc, created_at desc, id")]
    public void OrderByFor_known(string sort, string expected) =>
        Assert.Equal(expected, FeedEndpoints.OrderByFor(sort));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hot")]
    [InlineData("; drop table--")]
    public void OrderByFor_unknown_is_hot(string? sort) =>
        Assert.Equal("hot desc, id", FeedEndpoints.OrderByFor(sort));

    [Theory]
    [InlineData("newest")]
    [InlineData("likes")]
    [InlineData("; drop table--")]
    public void OrderByFor_always_ends_with_id(string? sort) =>
        Assert.EndsWith(", id", FeedEndpoints.OrderByFor(sort));

    // 5) 듣기 링크 — web safeHttpUrl 패리티. null/빈(선택 필드) 통과, http(s)만 허용.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://open.spotify.com/x")]
    [InlineData("http://example.com")]
    [InlineData("  https://x.com  ")] // 공백 트림 후 유효
    public void ValidListenUrl_accepts(string? url) =>
        Assert.True(SetEndpoints.ValidListenUrl(url));

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,x")]
    [InlineData("vbscript:x")]
    [InlineData("//evil.com")]
    [InlineData("/relative")]
    [InlineData("ftp://host/x")]
    public void ValidListenUrl_rejects(string url) =>
        Assert.False(SetEndpoints.ValidListenUrl(url));
}
