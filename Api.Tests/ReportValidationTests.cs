using Api.Social;

namespace Api.Tests;

public class ReportValidationTests
{
    [Theory]
    [InlineData("rating")]
    [InlineData("user")]
    public void IsTargetType_accepts_rating_user(string t) =>
        Assert.True(ReportValidation.IsTargetType(t));

    [Theory]
    [InlineData("track")]
    [InlineData("")]
    [InlineData(null)]
    public void IsTargetType_rejects_others(string? t) =>
        Assert.False(ReportValidation.IsTargetType(t));

    [Theory]
    [InlineData("not_music")]
    [InlineData("inappropriate_profile")]
    [InlineData("abuse")]
    [InlineData("other")]
    public void IsReason_accepts_known(string r) =>
        Assert.True(ReportValidation.IsReason(r));

    [Theory]
    [InlineData("spam")]
    [InlineData("")]
    [InlineData(null)]
    public void IsReason_rejects_unknown(string? r) =>
        Assert.False(ReportValidation.IsReason(r));

    [Fact]
    public void IsDetail_null_and_within_limit_ok_over_rejected()
    {
        Assert.True(ReportValidation.IsDetail(null));
        Assert.True(ReportValidation.IsDetail("부적절합니다"));
        Assert.True(ReportValidation.IsDetail(new string('a', ReportValidation.MaxDetailLength)));
        Assert.False(ReportValidation.IsDetail(new string('a', ReportValidation.MaxDetailLength + 1)));
    }
}
