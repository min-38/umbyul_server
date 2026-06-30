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

    [Fact]
    public void IsReview_null_and_within_limit_ok_over_limit_rejected()
    {
        Assert.True(RatingValidation.IsReview(null));
        Assert.True(RatingValidation.IsReview("좋은 곡"));
        Assert.True(RatingValidation.IsReview(new string('a', RatingValidation.MaxReviewLength)));
        Assert.False(RatingValidation.IsReview(new string('a', RatingValidation.MaxReviewLength + 1)));
    }
}
