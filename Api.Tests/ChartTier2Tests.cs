using Api.Chart;
using Api.Common;
using Api.Detail;
using Api.Profile;

namespace Api.Tests;

// QA7-4: 차트 k-anonymity·닫힌집합 매핑·앨범 dedupe·기존 스위트 엣지 보강.
public class ChartTier2Tests
{
    // MinVFor — 인구통계 필터(성별·나이) 활성 시 5(재식별 방지), 아니면 1.
    [Theory]
    [InlineData(null, null, 1)]
    [InlineData("male", null, 5)]
    [InlineData("female", null, 5)]
    [InlineData(null, "20", 5)]
    [InlineData("x", null, 1)]     // 무효 성별
    [InlineData(null, "99", 1)]    // 무효 나이
    public void MinVFor_cases(string? gender, string? age, int expected) =>
        Assert.Equal(expected, ChartEndpoints.MinVFor(gender, age));

    // IntervalFor — 닫힌 화이트리스트, 기본 year.
    [Theory]
    [InlineData("day", "1 day")]
    [InlineData("week", "7 days")]
    [InlineData("month", "30 days")]
    [InlineData(null, "365 days")]
    [InlineData("year", "365 days")]
    [InlineData("; drop--", "365 days")]
    public void IntervalFor_maps(string? period, string expectedInterval) =>
        Assert.Contains($"interval '{expectedInterval}'", ChartEndpoints.IntervalFor(period));

    // AgeClauseFor — 유효 나이만 EXISTS 절, 그 외 "".
    [Fact]
    public void AgeClauseFor_valid_has_range() =>
        Assert.Contains("between 20 and 29", ChartEndpoints.AgeClauseFor("20"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("99")]
    public void AgeClauseFor_invalid_empty(string? age) =>
        Assert.Equal("", ChartEndpoints.AgeClauseFor(age));

    // DedupeAlbums — 대소문자 무시 이름 dedupe(첫 항목 유지) + 발매일 문자열 desc.
    [Fact]
    public void DedupeAlbums_case_insensitive_keeps_first()
    {
        var albums = new List<ArtistAlbum>
        {
            new("id1", "Hello", null, "2020", "album", null),
            new("id2", "HELLO", null, "2023", "album", null), // 같은 이름(대소문자 무시) → 제거
        };
        var r = ArtistEndpoints.DedupeAlbums(albums);
        Assert.Single(r);
        Assert.Equal("id1", r[0].SpotifyId); // 첫 항목 유지
    }

    [Fact]
    public void DedupeAlbums_sorts_release_date_string_desc()
    {
        // 혼합 정밀도 특성화: "2023-07-07" > "2023" (문자열 비교).
        var albums = new List<ArtistAlbum>
        {
            new("a", "A", null, "2023", "album", null),
            new("b", "B", null, "2023-07-07", "album", null),
        };
        var r = ArtistEndpoints.DedupeAlbums(albums);
        Assert.Equal("b", r[0].SpotifyId);
        Assert.Equal("a", r[1].SpotifyId);
    }

    // 엣지 보강: 레벨 상한 캡.
    [Fact]
    public void LevelFor_caps_at_200() =>
        Assert.Equal(200, ReviewerLevel.LevelFor(int.MaxValue));

    // 엣지 보강: 영구정지는 미래 until이 있어도 Until=null(영구 우선).
    [Fact]
    public void Evaluate_banned_wins_over_future_until()
    {
        var now = DateTimeOffset.UnixEpoch;
        var block = Moderation.Evaluate(banned: true, until: now.AddDays(30), now: now);
        Assert.NotNull(block);
        Assert.True(block!.Banned);
        Assert.Null(block.Until);
    }
}
