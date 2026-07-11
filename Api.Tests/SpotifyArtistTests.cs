using System.Text.Json;
using Api.Detail;
using Api.Spotify;
using ArtistRef = Api.Common.ArtistRef;

namespace Api.Tests;

// QA7-2: NON-41 로직(429 백오프·RPS 파싱) + 실제로 밟은 target_artists 케이싱 버그를 회귀 잠금.
public class SpotifyArtistTests
{
    // CapBackoff — null→30s 기본, MaxBackoff(5min) 초과는 캡. 서킷은 캡값, DB는 원래 Retry-After(분리).
    [Fact]
    public void CapBackoff_null_is_30s() =>
        Assert.Equal(TimeSpan.FromSeconds(30), SpotifyClient.CapBackoff(null));

    [Fact]
    public void CapBackoff_under_cap_unchanged() =>
        Assert.Equal(TimeSpan.FromSeconds(10), SpotifyClient.CapBackoff(TimeSpan.FromSeconds(10)));

    [Fact]
    public void CapBackoff_over_cap_is_5min() =>
        Assert.Equal(TimeSpan.FromMinutes(5), SpotifyClient.CapBackoff(TimeSpan.FromHours(2)));

    [Fact]
    public void CapBackoff_exactly_5min_unchanged() =>
        Assert.Equal(TimeSpan.FromMinutes(5), SpotifyClient.CapBackoff(TimeSpan.FromMinutes(5)));

    // ResolveRps — 유효 양수만, 아니면 기본 10.
    [Theory]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-1")]
    public void ResolveRps_invalid_defaults_10(string? raw) =>
        Assert.Equal(10, SpotifyClient.ResolveRps(raw));

    [Fact]
    public void ResolveRps_valid() =>
        Assert.Equal(25, SpotifyClient.ResolveRps("25"));

    // target_artists 케이싱 — System.Text.Json 기본은 대소문자 구분(특성화: 소문자 키는 매칭 실패).
    [Fact]
    public void Parse_lowercase_id_yields_null_field()
    {
        var r = ArtistRef.Parse("[{\"id\":\"x\",\"name\":\"y\"}]");
        Assert.NotNull(r);
        Assert.Single(r!);
        Assert.Null(r![0].Id);   // "id" != "Id"
        Assert.Null(r[0].Name);
    }

    [Fact]
    public void Parse_PascalCase_ok()
    {
        var r = ArtistRef.Parse("[{\"Id\":\"x\",\"Name\":\"y\"}]");
        Assert.NotNull(r);
        Assert.Equal("x", r![0].Id);
        Assert.Equal("y", r[0].Name);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[")]
    [InlineData(null)]
    [InlineData("")]
    public void Parse_invalid_returns_null(string? json) =>
        Assert.Null(ArtistRef.Parse(json));

    // 불변식: 쓰기 직렬화가 PascalCase "Id"/"Name" 키를 만들어 ArtistJson containment probe 모양과 일치(NON-41 버그 방지).
    [Fact]
    public void ArtistRef_serialize_uses_PascalCase_keys()
    {
        var json = JsonSerializer.Serialize(new List<ArtistRef> { new("abc", "The Band") });
        Assert.Contains("\"Id\"", json);
        Assert.Contains("\"Name\"", json);
    }

    [Fact]
    public void ArtistJson_probe_shape_is_Id() =>
        Assert.Equal("[{\"Id\": \"x\"}]", ArtistEndpoints.ArtistJson("x"));

    // 따옴표 포함 id도 유효 JSON(이스케이프) — probe가 깨지지 않음.
    [Fact]
    public void ArtistJson_escapes_quotes()
    {
        var json = ArtistEndpoints.ArtistJson("a\"b");
        var parsed = ArtistRef.Parse(json);
        Assert.NotNull(parsed);
        Assert.Equal("a\"b", parsed![0].Id);
    }
}
