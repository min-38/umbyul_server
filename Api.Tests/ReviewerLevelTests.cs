using Api.Profile;

namespace Api.Tests;

// 리뷰어 레벨/XP 곡선(NON-153). 공식·경계·일관성 커버.
public class ReviewerLevelTests
{
    [Fact]
    public void Xp_uses_review_like_dailypick_weights()
    {
        Assert.Equal(0, ReviewerLevel.Xp(0, 0, 0));
        Assert.Equal(10, ReviewerLevel.Xp(1, 0, 0));   // 리뷰 10
        Assert.Equal(1, ReviewerLevel.Xp(0, 1, 0));    // 따봉 1
        Assert.Equal(15, ReviewerLevel.Xp(0, 0, 1));   // 픽 15
        Assert.Equal(55, ReviewerLevel.Xp(3, 10, 1));  // 30 + 10 + 15
    }

    [Fact]
    public void Xp_clamps_negative_inputs_to_zero()
    {
        Assert.Equal(0, ReviewerLevel.Xp(-5, -5, -5));
        Assert.Equal(10, ReviewerLevel.Xp(1, -3, -2));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 30)]
    [InlineData(3, 78)]
    [InlineData(4, 155)]
    [InlineData(5, 278)]
    public void Threshold_matches_expected_curve(int level, int expected) =>
        Assert.Equal(expected, ReviewerLevel.Threshold(level));

    [Fact]
    public void Threshold_is_strictly_increasing()
    {
        for (int l = 1; l < 30; l++)
            Assert.True(ReviewerLevel.Threshold(l + 1) > ReviewerLevel.Threshold(l), $"level {l}");
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(29, 1)]
    [InlineData(30, 2)]   // L2 경계
    [InlineData(77, 2)]
    [InlineData(78, 3)]   // L3 경계
    [InlineData(154, 3)]
    [InlineData(155, 4)]  // L4 경계
    public void LevelFor_lands_on_thresholds(int xp, int expectedLevel) =>
        Assert.Equal(expectedLevel, ReviewerLevel.LevelFor(xp));

    [Fact]
    public void LevelFor_never_below_one()
    {
        Assert.Equal(1, ReviewerLevel.LevelFor(-100));
        Assert.Equal(1, ReviewerLevel.LevelFor(0));
    }

    // LevelFor와 Threshold는 항상 일관: threshold(level) <= xp < threshold(level+1).
    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(77)]
    [InlineData(78)]
    [InlineData(474)]
    [InlineData(1000)]
    [InlineData(5000)]
    public void Compute_progress_is_consistent(int xp)
    {
        // XP를 리뷰 수로 환산(리뷰당 10)해 동일 xp를 만든다: 나머지는 따봉 1점씩.
        int reviews = xp / 10;
        int likes = xp % 10;
        var r = ReviewerLevel.Compute(reviews, likes, 0);
        Assert.Equal(xp, r.Xp);
        Assert.True(r.XpIntoLevel >= 0, "into >= 0");
        Assert.True(r.XpIntoLevel < r.XpForLevel, "into < span");
        Assert.Equal(r.Xp, ReviewerLevel.Threshold(r.Level) + r.XpIntoLevel);
        Assert.Equal(r.XpForLevel, ReviewerLevel.Threshold(r.Level + 1) - ReviewerLevel.Threshold(r.Level));
    }

    [Fact]
    public void Compute_resets_into_level_on_level_up()
    {
        // 정확히 임계값에 도달하면 XpIntoLevel은 0(진행바 리셋).
        var atL2 = ReviewerLevel.Compute(3, 0, 0); // 30 XP = L2 임계값
        Assert.Equal(2, atL2.Level);
        Assert.Equal(0, atL2.XpIntoLevel);
    }
}
