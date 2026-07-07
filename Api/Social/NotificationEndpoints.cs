using System.Security.Claims;
using Api.Common;
using Npgsql;

namespace Api.Social;

/// 알림 목록/읽음 (NON-26). 로그인 필요.
public static class NotificationEndpoints
{
    public sealed record NotificationItem(
        string Id, string Type, string ActorUsername, string? ActorAvatarUrl,
        DateTimeOffset CreatedAt, bool Read, string? Link, string? Detail);

    public sealed record NotificationList(IReadOnlyList<NotificationItem> Items, int UnreadCount);

    public sealed record NotificationPrefs(bool Master, bool Follow, bool ReviewLike, bool Mention);

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
                           u.username, u.avatar_url, r.target_type, r.target_spotify_id, n.detail
                    from public.notifications n
                    left join public.users u on u.id = n.actor_id
                    left join public.ratings r
                        on r.id = (case when n.type in ('review_like', 'warning', 'mention') then n.target_id::uuid end)
                       and r.deleted_at is null
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
                        var actor = rd.IsDBNull(5) ? "" : rd.GetString(5); // warning 은 발신 유저 없음
                        var detail = rd.IsDBNull(9) ? null : rd.GetString(9); // mention 은 댓글 id, warning 은 사유
                        string? link = type switch
                        {
                            "follow" => $"/u/{actor}",
                            // 멘션: 해당 댓글로 딥링크(?c=댓글id → 리뷰 열고 그 댓글로 스크롤, BUG-3).
                            "mention" when !rd.IsDBNull(7) && !rd.IsDBNull(8) && detail is not null =>
                                $"/{rd.GetString(7)}/{rd.GetString(8)}?c={detail}#review-{rd.GetString(2)}",
                            // 리뷰로 딥링크: /{track|album}/{spotifyId}#review-{ratingId} (NON-60, 기존 앵커 관례)
                            "review_like" or "warning" or "mention" when !rd.IsDBNull(7) && !rd.IsDBNull(8) => $"/{rd.GetString(7)}/{rd.GetString(8)}#review-{rd.GetString(2)}",
                            _ => null,
                        };
                        items.Add(new NotificationItem(
                            rd.GetGuid(0).ToString(), type, actor,
                            rd.IsDBNull(6) ? null : rd.GetString(6),
                            rd.GetFieldValue<DateTimeOffset>(4),
                            !rd.IsDBNull(3), link,
                            type == "mention" ? null : detail)); // mention detail(댓글 id)은 UI 노출 안 함
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

        // 개별 삭제
        me.MapDelete("/notifications/{id}", async (string id, ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Sub(user) is not { } uid) return ApiResults.Unauthorized("UNAUTHORIZED");
            if (!Guid.TryParse(id, out var nid)) return ApiResults.BadRequest("INVALID_TARGET");
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    "delete from public.notifications where id = @id and recipient_id = @me", conn);
                cmd.Parameters.AddWithValue("id", nid);
                cmd.Parameters.AddWithValue("me", uid);
                await cmd.ExecuteNonQueryAsync();
                return ApiResults.Ok("OK");
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // 알림 설정 조회 (없으면 기본 on)
        me.MapGet("/notification-prefs", async (ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Sub(user) is not { } uid) return ApiResults.Unauthorized("UNAUTHORIZED");
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    "select master, follow, review_like, mention from public.notification_prefs where user_id = @me", conn);
                cmd.Parameters.AddWithValue("me", uid);
                await using var rd = await cmd.ExecuteReaderAsync();
                var prefs = await rd.ReadAsync()
                    ? new NotificationPrefs(rd.GetBoolean(0), rd.GetBoolean(1), rd.GetBoolean(2), rd.GetBoolean(3))
                    : new NotificationPrefs(true, true, true, true);
                return ApiResults.Ok("OK", prefs);
            }
            catch (NpgsqlException) { return ApiResults.Ok("OK", new NotificationPrefs(true, true, true, true)); }
        });

        // 알림 설정 저장(upsert)
        me.MapPut("/notification-prefs", async (NotificationPrefs req, ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Sub(user) is not { } uid) return ApiResults.Unauthorized("UNAUTHORIZED");
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    """
                    insert into public.notification_prefs (user_id, master, follow, review_like, mention, updated_at)
                    values (@me, @m, @f, @rl, @mn, now())
                    on conflict (user_id) do update
                        set master = @m, follow = @f, review_like = @rl, mention = @mn, updated_at = now()
                    """, conn);
                cmd.Parameters.AddWithValue("me", uid);
                cmd.Parameters.AddWithValue("m", req.Master);
                cmd.Parameters.AddWithValue("f", req.Follow);
                cmd.Parameters.AddWithValue("rl", req.ReviewLike);
                cmd.Parameters.AddWithValue("mn", req.Mention);
                await cmd.ExecuteNonQueryAsync();
                return ApiResults.Ok("OK");
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });
    }

    private static Guid? Sub(ClaimsPrincipal user) =>
        user.FindFirstValue("sub") is { Length: > 0 } id && Guid.TryParse(id, out var g) ? g : null;
}
