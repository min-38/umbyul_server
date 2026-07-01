using System.Security.Claims;
using Api.Common;
using Npgsql;

namespace Api.Social;

/// 알림 목록/읽음 (NON-26). 로그인 필요.
public static class NotificationEndpoints
{
    public sealed record NotificationItem(
        string Id, string Type, string ActorUsername, string? ActorAvatarUrl,
        DateTimeOffset CreatedAt, bool Read, string? Link);

    public sealed record NotificationList(IReadOnlyList<NotificationItem> Items, int UnreadCount);

    public static void MapNotificationEndpoints(this WebApplication app, string? dbConnString)
    {
        var me = app.MapGroup("/me").RequireAuthorization();

        me.MapGet("/notifications", async (ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Sub(user) is not { } uid) return ApiResults.Unauthorized("UNAUTHORIZED");
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();

                var items = new List<NotificationItem>();
                await using (var cmd = new NpgsqlCommand(
                    """
                    select n.id, n.type, n.target_id, n.read_at, n.created_at,
                           u.username, u.avatar_url, r.target_type, r.target_spotify_id
                    from public.notifications n
                    join public.users u on u.id = n.actor_id
                    left join public.ratings r on n.type = 'review_like' and r.id::text = n.target_id
                    where n.recipient_id = @me
                    order by n.created_at desc
                    limit 30
                    """, conn))
                {
                    cmd.Parameters.AddWithValue("me", uid);
                    await using var rd = await cmd.ExecuteReaderAsync();
                    while (await rd.ReadAsync())
                    {
                        var type = rd.GetString(1);
                        var actor = rd.GetString(5);
                        string? link = type switch
                        {
                            "follow" => $"/u/{actor}",
                            "review_like" when !rd.IsDBNull(7) && !rd.IsDBNull(8) => $"/{rd.GetString(7)}/{rd.GetString(8)}",
                            _ => null,
                        };
                        items.Add(new NotificationItem(
                            rd.GetGuid(0).ToString(), type, actor,
                            rd.IsDBNull(6) ? null : rd.GetString(6),
                            rd.GetFieldValue<DateTimeOffset>(4),
                            !rd.IsDBNull(3), link));
                    }
                }

                int unread;
                await using (var c2 = new NpgsqlCommand(
                    "select count(*) from public.notifications where recipient_id = @me and read_at is null", conn))
                {
                    c2.Parameters.AddWithValue("me", uid);
                    unread = (int)(long)(await c2.ExecuteScalarAsync())!;
                }

                return ApiResults.Ok("OK", new NotificationList(items, unread));
            }
            catch (NpgsqlException)
            {
                return ApiResults.Ok("OK", new NotificationList([], 0));
            }
        });

        me.MapPost("/notifications/read", async (ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Sub(user) is not { } uid) return ApiResults.Unauthorized("UNAUTHORIZED");
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    "update public.notifications set read_at = now() where recipient_id = @me and read_at is null", conn);
                cmd.Parameters.AddWithValue("me", uid);
                await cmd.ExecuteNonQueryAsync();
                return ApiResults.Ok("OK");
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // 모두 지우기
        me.MapDelete("/notifications", async (ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Sub(user) is not { } uid) return ApiResults.Unauthorized("UNAUTHORIZED");
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    "delete from public.notifications where recipient_id = @me", conn);
                cmd.Parameters.AddWithValue("me", uid);
                await cmd.ExecuteNonQueryAsync();
                return ApiResults.Ok("OK");
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });
    }

    private static Guid? Sub(ClaimsPrincipal user) =>
        user.FindFirstValue("sub") is { Length: > 0 } id && Guid.TryParse(id, out var g) ? g : null;
}
