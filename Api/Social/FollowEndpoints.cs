using System.Security.Claims;
using Api.Common;
using Npgsql;

namespace Api.Social;

/// 팔로우 시스템 (NON-25). 팔로우/언팔로우는 로그인. 목록은 공개(뷰어 isFollowing 포함).
public static class FollowEndpoints
{
    public sealed record FollowRequest(string? Username);
    public sealed record FollowUser(string Username, string? AvatarUrl, bool IsFollowing);

    public static void MapFollowEndpoints(this WebApplication app, string? dbConnString)
    {
        var me = app.MapGroup("/me").RequireAuthorization();

        // 팔로우
        me.MapPost("/follows", async (FollowRequest req, ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Me(user) is not { } meId) return ApiResults.Unauthorized("UNAUTHORIZED");
            if (string.IsNullOrWhiteSpace(req.Username)) return ApiResults.BadRequest("INVALID_TARGET");

            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                if (await Moderation.CheckAsync(conn, meId, default) is { } block) return Moderation.ToResult(block);
                if (await ResolveUserIdAsync(conn, req.Username!) is not { } targetId)
                    return ApiResults.NotFound("USER_NOT_FOUND");
                if (targetId == meId) return ApiResults.BadRequest("CANNOT_FOLLOW_SELF");
                if (await BlockEndpoints.IsBlockedAsync(conn, meId, targetId)) return ApiResults.BadRequest("BLOCKED");

                await using var cmd = new NpgsqlCommand(
                    "insert into public.follows (follower_id, following_id) values (@me, @t) on conflict do nothing", conn);
                cmd.Parameters.AddWithValue("me", meId);
                cmd.Parameters.AddWithValue("t", targetId);
                if (await cmd.ExecuteNonQueryAsync() > 0) // 신규 팔로우만 알림
                    await Notifications.CreateAsync(conn, targetId, meId, "follow", null);
                return ApiResults.Ok("OK");
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // 언팔로우
        me.MapDelete("/follows", async (string? username, ClaimsPrincipal user) =>
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
                    "delete from public.follows where follower_id = @me and following_id = @t", conn);
                cmd.Parameters.AddWithValue("me", meId);
                cmd.Parameters.AddWithValue("t", targetId);
                await cmd.ExecuteNonQueryAsync();
                return ApiResults.Ok("OK");
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // 팔로워 목록 / 팔로잉 목록 (공개)
        app.MapGet("/users/{username}/followers", (string username, ClaimsPrincipal user, CancellationToken ct) =>
            ListAsync(dbConnString, username, followers: true, Me(user), ct));
        app.MapGet("/users/{username}/following", (string username, ClaimsPrincipal user, CancellationToken ct) =>
            ListAsync(dbConnString, username, followers: false, Me(user), ct));
    }

    private static async Task<IResult> ListAsync(string? dbConnString, string username, bool followers, Guid? viewer, CancellationToken ct)
    {
        if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
        try
        {
            await using var conn = new NpgsqlConnection(dbConnString);
            await conn.OpenAsync(ct);
            if (await ResolveUserIdAsync(conn, username) is not { } uid)
                return ApiResults.NotFound("USER_NOT_FOUND");

            // followers: 나(uid)를 팔로우하는 사람 → f.follower_id 가 그 사람.
            // following: 내가 팔로우하는 사람 → f.following_id 가 그 사람.
            var sql = followers
                ? """
                  select u.username, u.avatar_url,
                         exists(select 1 from public.follows f2 where f2.follower_id = @viewer and f2.following_id = u.id)
                  from public.follows f join public.users u on u.id = f.follower_id
                  where f.following_id = @uid order by f.created_at desc
                  """
                : """
                  select u.username, u.avatar_url,
                         exists(select 1 from public.follows f2 where f2.follower_id = @viewer and f2.following_id = u.id)
                  from public.follows f join public.users u on u.id = f.following_id
                  where f.follower_id = @uid order by f.created_at desc
                  """;
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("uid", uid);
            cmd.Parameters.AddWithValue("viewer", (object?)viewer ?? DBNull.Value);

            var list = new List<FollowUser>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new FollowUser(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetBoolean(2)));
            return ApiResults.Ok("OK", list);
        }
        catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
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
