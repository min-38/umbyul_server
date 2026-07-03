using Api.Common;

namespace Api.Tests;

// 제재 판정 순수 함수(Moderation.Evaluate) — banned/suspended_until 경계(NON-48/109).
public class ModerationTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Ban_takes_priority_and_ignores_until()
    {
        var b = Moderation.Evaluate(banned: true, until: Now.AddDays(-100), now: Now);
        Assert.NotNull(b);
        Assert.True(b!.Banned);
        Assert.Null(b.Until);
    }

    [Fact]
    public void Future_suspension_blocks_with_until()
    {
        var until = Now.AddHours(1);
        var b = Moderation.Evaluate(banned: false, until: until, now: Now);
        Assert.NotNull(b);
        Assert.False(b!.Banned);
        Assert.Equal(until, b.Until);
    }

    [Fact]
    public void Suspension_exactly_now_is_expired_pass()
    {
        // 경계: until == now → 만료(u > now 거짓) → 통과.
        Assert.Null(Moderation.Evaluate(banned: false, until: Now, now: Now));
    }

    [Fact]
    public void Past_suspension_passes()
    {
        Assert.Null(Moderation.Evaluate(banned: false, until: Now.AddSeconds(-1), now: Now));
    }

    [Fact]
    public void No_sanction_passes()
    {
        Assert.Null(Moderation.Evaluate(banned: false, until: null, now: Now));
    }
}
