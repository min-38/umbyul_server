using System.Security.Claims;
using Api.Common;
using Npgsql;

namespace Api.Social;

/// 유저 차단(상호) — NON-115. 차단/해제는 로그인. 차단 시 양방향 팔로우 자동 해제.
/// 상호 판정(IsBlockedAsync)은 어느 방향이든 행이 있으면 true → 피드·프로필·팔로우에서 서로 제외.
public static class BlockEndpoints
{
    public sealed record BlockRequest(string? Username);
    public sealed record BlockedUser(string Username, string? AvatarUrl, DateTimeOffset CreatedAt);

    public static void MapBlockEndpoints(this WebApplication app, string? dbConnString)
    {
        var me = app.MapGroup("/me").RequireAuthorization();

        // 내가 차단한 유저 목록(설정 · 차단 관리) — 최신순.
        me.MapGet("/blocks", async (ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Me(user) is not { } meId) return ApiResults.Unauthorized("UNAUTHORIZED");

            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand(
                    """
                    select u.username, u.avatar_url, b.created_at
                    from public.blocks b
                    join public.users u on u.id = b.blocked_id
                    where b.blocker_id = @me
                    order by b.created_at desc
                    """, conn);
                cmd.Parameters.AddWithValue("me", meId);

                var list = new List<BlockedUser>();
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    list.Add(new BlockedUser(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetFieldValue<DateTimeOffset>(2)));
                return ApiResults.Ok("OK", list);
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // 차단 — 서로의 팔로우를 함께 삭제(멱등).
        me.MapPost("/blocks", async (BlockRequest req, ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Me(user) is not { } meId) return ApiResults.Unauthorized("UNAUTHORIZED");
            if (string.IsNullOrWhiteSpace(req.Username)) return ApiResults.BadRequest("INVALID_TARGET");

            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                if (await ResolveUserIdAsync(conn, req.Username!) is not { } targetId)
                    return ApiResults.NotFound("USER_NOT_FOUND");
                if (targetId == meId) return ApiResults.BadRequest("CANNOT_BLOCK_SELF");

                await using var cmd = new NpgsqlCommand(
                    """
                    insert into public.blocks (blocker_id, blocked_id) values (@me, @t)
                    on conflict do nothing;
                    delete from public.follows
                    where (follower_id = @me and following_id = @t)
                       or (follower_id = @t and following_id = @me);
                    """, conn);
                cmd.Parameters.AddWithValue("me", meId);
                cmd.Parameters.AddWithValue("t", targetId);
                await cmd.ExecuteNonQueryAsync();
                return ApiResults.Ok("OK");
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // 차단 해제
        me.MapDelete("/blocks", async (string? username, ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Me(user) is not { } meId) return ApiResults.Unauthorized("UNAUTHORIZED");
            if (string.IsNullOrWhiteSpace(username)) return ApiResults.BadRequest("INVALID_TARGET");

            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                if (await ResolveUserIdAsync(conn, username!) is not { } targetId)
                    return ApiResults.NotFound("USER_NOT_FOUND");

                await using var cmd = new NpgsqlCommand(
                    "delete from public.blocks where blocker_id = @me and blocked_id = @t", conn);
                cmd.Parameters.AddWithValue("me", meId);
                cmd.Parameters.AddWithValue("t", targetId);
                await cmd.ExecuteNonQueryAsync();
                return ApiResults.Ok("OK");
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });
    }

    /// 상호 차단 여부(어느 방향이든). 한쪽이라도 null 이면 false(비로그인 등).
    public static async Task<bool> IsBlockedAsync(NpgsqlConnection conn, Guid? a, Guid? b)
    {
        if (a is not { } x || b is not { } y || x == y) return false;
        await using var cmd = new NpgsqlCommand(
            """
            select exists(
              select 1 from public.blocks
              where (blocker_id = @a and blocked_id = @b)
                 or (blocker_id = @b and blocked_id = @a))
            """, conn);
        cmd.Parameters.AddWithValue("a", x);
        cmd.Parameters.AddWithValue("b", y);
        return await cmd.ExecuteScalarAsync() is true;
    }

    private static async Task<Guid?> ResolveUserIdAsync(NpgsqlConnection conn, string username)
    {
        await using var cmd = new NpgsqlCommand("select id from public.users where lower(username) = lower(@u)", conn);
        cmd.Parameters.AddWithValue("u", username);
        return await cmd.ExecuteScalarAsync() is Guid g ? g : null;
    }

    private static Guid? Me(ClaimsPrincipal user) =>
        user.FindFirstValue("sub") is { Length: > 0 } id && Guid.TryParse(id, out var g) ? g : null;
}
