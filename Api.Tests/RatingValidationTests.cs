using Api.Ratings;

namespace Api.Tests;

public class RatingValidationTests
{
    [Theory]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(3.5)]
    [InlineData(5.0)]
    public void IsScore_accepts_half_steps_in_range(double score) =>
        Assert.True(RatingValidation.IsScore((decimal)score));

    [Theory]
    [InlineData(0.0)]   // 최소 미만
    [InlineData(0.4)]
    [InlineData(5.5)]   // 최대 초과
    [InlineData(3.3)]   // 0.5 단위 아님
    [InlineData(0.25)]
    public void IsScore_rejects_out_of_range_or_non_half(double score) =>
        Assert.False(RatingValidation.IsScore((decimal)score));

    [Theory]
    [InlineData("track")]
    [InlineData("album")]
    public void IsTargetType_accepts_track_album(string t) =>
        Assert.True(RatingValidation.IsTargetType(t));

    [Theory]
    [InlineData("artist")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Track")]
    public void IsTargetType_rejects_others(string? t) =>
        Assert.False(RatingValidation.IsTargetType(t));

    // NON-91: 리뷰 필수 · 최소 10자(trim 기준). 경계값 커버.
    [Fact]
    public void IsReview_rejects_missing_or_too_short()
    {
        Assert.False(RatingValidation.IsReview(null));                 // 없음
        Assert.False(RatingValidation.IsReview(""));                   // 빈 문자열
        Assert.False(RatingValidation.IsReview("     "));              // 공백만
        Assert.False(RatingValidation.IsReview("좋은 곡"));            // 4자 < 10
        Assert.False(RatingValidation.IsReview(new string('a', 9)));   // 9자 (경계)
    }

    [Fact]
    public void IsReview_accepts_min_length_and_up_to_max()
    {
        Assert.True(RatingValidation.IsReview(new string('a', RatingValidation.MinReviewLength)));  // 정확히 10
        Assert.True(RatingValidation.IsReview("  " + new string('a', 10) + "  "));                  // trim 후 10
        Assert.True(RatingValidation.IsReview(new string('a', RatingValidation.MaxReviewLength)));  // 5000
    }

    [Fact]
    public void IsReview_trims_before_measuring_and_rejects_over_max()
    {
        Assert.False(RatingValidation.IsReview("  " + new string('a', 9) + "  "));                       // trim 후 9자
        Assert.False(RatingValidation.IsReview(new string('a', RatingValidation.MaxReviewLength + 1)));  // 5001 초과
    }
}
