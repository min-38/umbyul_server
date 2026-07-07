using Api.Social;
using Npgsql;

namespace Api.Tests;

// 상호 차단 판정(BlockEndpoints.IsBlockedAsync)의 단락 가드 — DB 접근 전 즉시 false 반환.
// null 행위자(비로그인)나 자기 자신은 절대 "차단됨"이 아니어야 함(NON-115).
// 커넥션은 열지 않는다 — 가드가 통과되면 DB를 건드리지 않으므로.
public class BlockTests
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Null_actor_is_never_blocked()
    {
        await using var conn = new NpgsqlConnection();
        Assert.False(await BlockEndpoints.IsBlockedAsync(conn, null, B));
    }

    [Fact]
    public async Task Null_target_is_never_blocked()
    {
        await using var conn = new NpgsqlConnection();
        Assert.False(await BlockEndpoints.IsBlockedAsync(conn, A, null));
    }

    [Fact]
    public async Task Self_is_never_blocked()
    {
        await using var conn = new NpgsqlConnection();
        Assert.False(await BlockEndpoints.IsBlockedAsync(conn, A, A));
    }
}
