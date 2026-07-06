using System.Security.Claims;
using Api.Common;
using Npgsql;

namespace Api.Social;

/// @멘션 지원 (NON-131): username 자동완성 검색 + 특정 트랙/앨범 멘션 뮤트.
public static class MentionEndpoints
{
    public sealed record UserHit(string Id, string Username, string? AvatarUrl);
    public sealed record MuteRequest(string? TargetType, string? SpotifyId);

    public static void MapMentionEndpoints(this WebApplication app, string? dbConnString)
    {
        // 공개: username 접두 검색(자동완성용). 실존 유저만.
        app.MapGet("/users/search", async (string? q) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            var prefix = new string((q ?? "").Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
            if (prefix.Length == 0) return ApiResults.Ok("OK", new { items = Array.Empty<UserHit>() });

            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    """
                    select id, username, avatar_url
                    from public.users
                    where username ilike @p || '%'
                    order by username
                    limit 8
                    """, conn);
                cmd.Parameters.AddWithValue("p", prefix);
                var items = new List<UserHit>();
                await using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                    items.Add(new UserHit(rd.GetGuid(0).ToString(), rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetString(2)));
                return ApiResults.Ok("OK", new { items });
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        var me = app.MapGroup("/me").RequireAuthorization();

        // 이 트랙/앨범의 멘션 뮤트 여부.
        me.MapGet("/mention-mute", async (string? type, string? id, ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Sub(user) is not { } uid) return ApiResults.Unauthorized("UNAUTHORIZED");
            if (type is not ("track" or "album") || string.IsNullOrEmpty(id)) return ApiResults.BadRequest("INVALID_TARGET");
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    "select exists (select 1 from public.mention_mutes where user_id = @u and target_type = @t and target_spotify_id = @s)", conn);
                cmd.Parameters.AddWithValue("u", uid);
                cmd.Parameters.AddWithValue("t", type);
                cmd.Parameters.AddWithValue("s", id);
                var muted = (bool)(await cmd.ExecuteScalarAsync())!;
                return ApiResults.Ok("OK", new { muted });
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // 이 트랙/앨범 멘션 뮤트 토글. 반환 { muted }.
        me.MapPost("/mention-mute", async (MuteRequest req, ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Sub(user) is not { } uid) return ApiResults.Unauthorized("UNAUTHORIZED");
            if (req.TargetType is not ("track" or "album") || string.IsNullOrEmpty(req.SpotifyId))
                return ApiResults.BadRequest("INVALID_TARGET");
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                bool muted;
                await using (var del = new NpgsqlCommand(
                    "delete from public.mention_mutes where user_id = @u and target_type = @t and target_spotify_id = @s", conn))
                {
                    del.Parameters.AddWithValue("u", uid);
                    del.Parameters.AddWithValue("t", req.TargetType);
                    del.Parameters.AddWithValue("s", req.SpotifyId);
                    muted = await del.ExecuteNonQueryAsync() == 0; // 지운 게 없으면 새로 뮤트
                }
                if (muted)
                {
                    await using var ins = new NpgsqlCommand(
                        "insert into public.mention_mutes (user_id, target_type, target_spotify_id) values (@u, @t, @s) on conflict do nothing", conn);
                    ins.Parameters.AddWithValue("u", uid);
                    ins.Parameters.AddWithValue("t", req.TargetType);
                    ins.Parameters.AddWithValue("s", req.SpotifyId);
                    await ins.ExecuteNonQueryAsync();
                }
                return ApiResults.Ok("OK", new { muted });
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });
    }

    private static Guid? Sub(ClaimsPrincipal user) =>
        user.FindFirstValue("sub") is { Length: > 0 } id && Guid.TryParse(id, out var g) ? g : null;
}
