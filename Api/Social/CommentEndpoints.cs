using System.Security.Claims;
using System.Text.RegularExpressions;
using Api.Common;
using Api.Profile;
using Npgsql;
using NpgsqlTypes;

namespace Api.Social;

/// 리뷰 댓글 + 대댓글 (NON-36/40). 열람 공개, 작성/삭제/좋아요는 로그인.
/// 2레벨 flatten(답글의 답글도 최상위 댓글에 평평하게, @멘션으로 대상 표시). 댓글 좋아요만(싫어요 없음).
/// 각 댓글에 작성자가 그 대상(곡/앨범)에 매긴 별점 동봉(없으면 null=평가 없음). 신고는 /me/reports(target_type=comment).
public static class CommentEndpoints
{
    public const int MaxBodyLength = 1000;

    // @핸들 파싱 — username 규칙(^[A-Za-z0-9]+(-[A-Za-z0-9]+)*$)과 동일.
    private static readonly Regex MentionPattern = new(@"@([A-Za-z0-9]+(?:-[A-Za-z0-9]+)*)", RegexOptions.Compiled);

    public sealed record CommentRequest(string? RatingId, string? ParentId, string? Body);
    public sealed record CommentItem(
        string Id, string? ParentId, string UserId, string Username, string? AvatarUrl,
        string? Body, DateTimeOffset CreatedAt, int LikeCount, bool LikedByMe, double? Score, bool Deleted, bool Edited,
        int Level = 1); // 작성자 리뷰어 레벨(NON-163)

    public static void MapCommentEndpoints(this WebApplication app, string? dbConnString)
    {
        // 공개(옵셔널 인증): 리뷰의 댓글+대댓글 목록(오래된 순). 로그인 시 내 좋아요 포함.
        app.MapGet("/detail/comments/{ratingId}", async (string ratingId, ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (!Guid.TryParse(ratingId, out var rid)) return ApiResults.BadRequest("INVALID_TARGET");
            var me = Sub(user);

            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    """
                    with tgt as (select target_type, target_id from public.ratings where id = @rid)
                    select c.id, c.parent_id, c.user_id, u.username, u.avatar_url,
                           case when c.deleted_at is null then c.body else null end as body,
                           c.created_at,
                           (select count(*) from public.comment_likes cl where cl.comment_id = c.id)::int as like_count,
                           (@me is not null and exists (select 1 from public.comment_likes cl where cl.comment_id = c.id and cl.user_id = @me)) as liked,
                           cr.score::float8 as score,
                           (c.deleted_at is not null) as deleted,
                           (c.edited_at is not null) as edited
                    from public.review_comments c
                    join public.users u on u.id = c.user_id
                    cross join tgt
                    left join public.ratings cr
                        on cr.user_id = c.user_id and cr.target_type = tgt.target_type
                       and cr.target_id = tgt.target_id and cr.deleted_at is null
                    where c.rating_id = @rid
                      and (c.deleted_at is null
                           or exists (select 1 from public.review_comments ch where ch.parent_id = c.id and ch.deleted_at is null))
                    order by c.created_at asc
                    limit 500
                    """, conn);
                cmd.Parameters.AddWithValue("rid", rid);
                cmd.Parameters.Add(new NpgsqlParameter("me", NpgsqlDbType.Uuid) { Value = (object?)me ?? DBNull.Value });

                var items = new List<CommentItem>();
                await using (var r = await cmd.ExecuteReaderAsync())
                    while (await r.ReadAsync()) items.Add(Read(r));

                // 댓글/대댓글 작성자 레벨 뱃지(NON-163) — 배치 1회. 실패 시 기본 Lv 1.
                var levels = await LevelService.LoadLevelsAsync(conn, items.Select(x => Guid.Parse(x.UserId)).ToList(), default);
                if (levels.Count > 0)
                    for (int i = 0; i < items.Count; i++)
                        if (levels.TryGetValue(Guid.Parse(items[i].UserId), out var lv)) items[i] = items[i] with { Level = lv };

                return ApiResults.Ok("OK", new { items });
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        var me = app.MapGroup("/me").RequireAuthorization();

        // 댓글/대댓글 작성. parentId 있으면 대댓글 — 2레벨 flatten(부모가 대댓글이면 그 최상위 조상으로 승격).
        me.MapPost("/comments", async (CommentRequest req, ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Sub(user) is not { } uid) return ApiResults.Unauthorized("UNAUTHORIZED");
            if (!Guid.TryParse(req.RatingId, out var rid)) return ApiResults.BadRequest("INVALID_TARGET");
            var body = req.Body?.Trim();
            if (string.IsNullOrEmpty(body) || body.Length > MaxBodyLength) return ApiResults.BadRequest("INVALID_BODY");

            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                if (await Moderation.CheckAsync(conn, uid, default) is { } block) return Moderation.ToResult(block);

                Guid? parentId = null;
                if (!string.IsNullOrWhiteSpace(req.ParentId))
                {
                    if (!Guid.TryParse(req.ParentId, out var pid)) return ApiResults.BadRequest("INVALID_TARGET");
                    await using var pcmd = new NpgsqlCommand(
                        "select coalesce(parent_id, id) from public.review_comments where id = @pid and rating_id = @rid and deleted_at is null", conn);
                    pcmd.Parameters.AddWithValue("pid", pid);
                    pcmd.Parameters.AddWithValue("rid", rid);
                    if (await pcmd.ExecuteScalarAsync() is not Guid ancestor) return ApiResults.BadRequest("INVALID_TARGET");
                    parentId = ancestor;
                }

                await using var cmd = new NpgsqlCommand(
                    """
                    with tgt as (select target_type, target_id, target_spotify_id from public.ratings where id = @rid),
                    ins as (
                        insert into public.review_comments (rating_id, user_id, body, parent_id)
                        values (@rid, @uid, @body, @parent)
                        returning id, parent_id, user_id, body, created_at
                    )
                    select ins.id, ins.parent_id, ins.user_id, u.username, u.avatar_url, ins.body, ins.created_at, cr.score::float8,
                           tgt.target_type, tgt.target_spotify_id
                    from ins
                    join public.users u on u.id = ins.user_id
                    cross join tgt
                    left join public.ratings cr
                        on cr.user_id = ins.user_id and cr.target_type = tgt.target_type
                       and cr.target_id = tgt.target_id and cr.deleted_at is null
                    """, conn);
                cmd.Parameters.AddWithValue("rid", rid);
                cmd.Parameters.AddWithValue("uid", uid);
                cmd.Parameters.AddWithValue("body", body);
                cmd.Parameters.Add(new NpgsqlParameter("parent", NpgsqlDbType.Uuid) { Value = (object?)parentId ?? DBNull.Value });

                CommentItem item;
                string commentId;
                string? targetType;
                string? targetSpotifyId;
                await using (var r = await cmd.ExecuteReaderAsync())
                {
                    await r.ReadAsync();
                    commentId = r.GetGuid(0).ToString();
                    targetType = r.IsDBNull(8) ? null : r.GetString(8);
                    targetSpotifyId = r.IsDBNull(9) ? null : r.GetString(9);
                    item = new CommentItem(
                        commentId, r.IsDBNull(1) ? null : r.GetGuid(1).ToString(), r.GetGuid(2).ToString(),
                        r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.GetFieldValue<DateTimeOffset>(6),
                        0, false, r.IsDBNull(7) ? null : r.GetDouble(7), false, false);
                }

                // 작성자 레벨 뱃지(NON-163).
                var lvls = await LevelService.LoadLevelsAsync(conn, new[] { Guid.Parse(item.UserId) }, default);
                if (lvls.TryGetValue(Guid.Parse(item.UserId), out var mylv)) item = item with { Level = mylv };

                // @멘션 알림 — 본문의 실존 유저에게 적재(대상 정보 있을 때만). 알림 클릭 시 해당 댓글로 딥링크(BUG-3).
                if (targetType is not null && targetSpotifyId is not null)
                    await NotifyMentionsAsync(conn, body, uid, rid, commentId, targetType, targetSpotifyId);

                return ApiResults.Created("CREATED", item);
            }
            catch (PostgresException ex) when (ex.SqlState == "23503") { return ApiResults.BadRequest("INVALID_TARGET"); }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // 댓글 좋아요 토글(좋아요만). 반환 { liked, likeCount }.
        me.MapPost("/comments/{id}/like", async (string id, ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Sub(user) is not { } uid) return ApiResults.Unauthorized("UNAUTHORIZED");
            if (!Guid.TryParse(id, out var cid)) return ApiResults.BadRequest("INVALID_TARGET");

            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                if (await Moderation.CheckAsync(conn, uid, default) is { } block) return Moderation.ToResult(block);

                bool liked;
                await using (var del = new NpgsqlCommand(
                    "delete from public.comment_likes where comment_id = @cid and user_id = @uid", conn))
                {
                    del.Parameters.AddWithValue("cid", cid);
                    del.Parameters.AddWithValue("uid", uid);
                    liked = await del.ExecuteNonQueryAsync() == 0; // 지운 게 없으면 새로 좋아요
                }
                if (liked)
                {
                    await using var ins = new NpgsqlCommand(
                        "insert into public.comment_likes (comment_id, user_id) values (@cid, @uid) on conflict do nothing", conn);
                    ins.Parameters.AddWithValue("cid", cid);
                    ins.Parameters.AddWithValue("uid", uid);
                    await ins.ExecuteNonQueryAsync();
                }
                await using var cnt = new NpgsqlCommand("select count(*)::int from public.comment_likes where comment_id = @cid", conn);
                cnt.Parameters.AddWithValue("cid", cid);
                var likeCount = (int)(await cnt.ExecuteScalarAsync())!;
                return ApiResults.Ok("OK", new { liked, likeCount });
            }
            catch (PostgresException ex) when (ex.SqlState == "23503") { return ApiResults.BadRequest("INVALID_TARGET"); }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // 댓글 삭제 — 본인 것만. 소프트삭제(대댓글 스레드 보존 → 목록에선 답글 있을 때만 "삭제됨"으로 노출).
        me.MapDelete("/comments/{id}", async (string id, ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Sub(user) is not { } uid) return ApiResults.Unauthorized("UNAUTHORIZED");
            if (!Guid.TryParse(id, out var cid)) return ApiResults.BadRequest("INVALID_TARGET");

            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                // 본인 삭제는 deleted_by 미기록(null) — deleted_by는 관리자(모더레이션) 삭제 전용(set_comments 컨벤션, QA6-2).
                await using var cmd = new NpgsqlCommand(
                    "update public.review_comments set deleted_at = now() where id = @cid and user_id = @uid and deleted_at is null", conn);
                cmd.Parameters.AddWithValue("cid", cid);
                cmd.Parameters.AddWithValue("uid", uid);
                if (await cmd.ExecuteNonQueryAsync() == 0) return ApiResults.NotFound("COMMENT_NOT_FOUND");
                return ApiResults.Ok("OK");
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // 댓글 수정 — 본인 것만(삭제되지 않은 것). edited_at 기록 → 목록에서 '(수정됨)' 표시(BUG-11).
        me.MapPut("/comments/{id}", async (string id, CommentRequest req, ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Sub(user) is not { } uid) return ApiResults.Unauthorized("UNAUTHORIZED");
            if (!Guid.TryParse(id, out var cid)) return ApiResults.BadRequest("INVALID_TARGET");
            var body = req.Body?.Trim();
            if (string.IsNullOrEmpty(body) || body.Length > MaxBodyLength) return ApiResults.BadRequest("INVALID_BODY");

            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                if (await Moderation.CheckAsync(conn, uid, default) is { } block) return Moderation.ToResult(block);
                await using var cmd = new NpgsqlCommand(
                    "update public.review_comments set body = @body, edited_at = now() where id = @cid and user_id = @uid and deleted_at is null", conn);
                cmd.Parameters.AddWithValue("body", body);
                cmd.Parameters.AddWithValue("cid", cid);
                cmd.Parameters.AddWithValue("uid", uid);
                if (await cmd.ExecuteNonQueryAsync() == 0) return ApiResults.NotFound("COMMENT_NOT_FOUND");
                return ApiResults.Ok("OK");
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });
    }

    // 본문에서 @핸들을 뽑아 실존 유저로 해석 → 각자에게 멘션 알림. self·설정off·대상뮤트는 헬퍼가 걸러냄.
    private static async Task NotifyMentionsAsync(
        NpgsqlConnection conn, string body, Guid actorId, Guid ratingId, string commentId, string targetType, string targetSpotifyId)
    {
        var names = MentionPattern.Matches(body)
            .Select(m => m.Groups[1].Value.ToLowerInvariant())
            .Distinct()
            .Take(10)
            .ToArray();
        if (names.Length == 0) return;

        try
        {
            var recipients = new List<Guid>();
            await using (var q = new NpgsqlCommand(
                "select id from public.users where lower(username) = any(@names)", conn))
            {
                q.Parameters.Add(new NpgsqlParameter("names", names));
                await using var rd = await q.ExecuteReaderAsync();
                while (await rd.ReadAsync()) recipients.Add(rd.GetGuid(0));
            }
            foreach (var rid in recipients)
                await Notifications.CreateMentionAsync(conn, rid, actorId, ratingId.ToString(), commentId, targetType, targetSpotifyId);
        }
        catch (NpgsqlException) { /* 멘션 알림 실패 무시 */ }
    }

    private static CommentItem Read(NpgsqlDataReader r) => new(
        r.GetGuid(0).ToString(),
        r.IsDBNull(1) ? null : r.GetGuid(1).ToString(),
        r.GetGuid(2).ToString(),
        r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5),
        r.GetFieldValue<DateTimeOffset>(6),
        r.GetInt32(7),
        r.GetBoolean(8),
        r.IsDBNull(9) ? null : r.GetDouble(9),
        r.GetBoolean(10),
        r.GetBoolean(11));

    private static Guid? Sub(ClaimsPrincipal user) =>
        user.FindFirstValue("sub") is { Length: > 0 } id && Guid.TryParse(id, out var g) ? g : null;
}
