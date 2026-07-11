using System.Security.Claims;
using Api.Common;
using Npgsql;

namespace Api.Social;

/// 리뷰 좋아요/싫어요 (NON-23). 로그인 필요. 1인 1리뷰당 1반응.
/// 같은 값 재요청 = 취소(삭제), 다른 값 = 변경, 새로 = 추가. 갱신된 카운트+내 반응을 반환.
public static class ReactionEndpoints
{
    public sealed record ReactionRequest(string? RatingId, string? Value);
    public sealed record ReactionState(int LikeCount, int DislikeCount, string? MyReaction);

    public static void MapReactionEndpoints(this WebApplication app, string? dbConnString)
    {
        var me = app.MapGroup("/me").RequireAuthorization();

        me.MapPost("/reactions", async (ReactionRequest req, ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (user.FindFirstValue("sub") is not { Length: > 0 } sub || !Guid.TryParse(sub, out var uid))
                return ApiResults.Unauthorized("UNAUTHORIZED");
            if (req.Value is not ("like" or "dislike")) return ApiResults.BadRequest("INVALID_REACTION");
            if (!Guid.TryParse(req.RatingId, out var rid)) return ApiResults.BadRequest("INVALID_TARGET");

            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                if (await Moderation.CheckAsync(conn, uid, default) is { } block) return Moderation.ToResult(block);

                // 자기 리뷰에는 반응 금지 — 셀프 좋아요가 XP·유저 차트에 무료로 집계되는 것 차단(NON-259).
                await using (var own = new NpgsqlCommand("select user_id from public.ratings where id = @rid", conn))
                {
                    own.Parameters.AddWithValue("rid", rid);
                    if (await own.ExecuteScalarAsync() is Guid authorId && authorId == uid)
                        return ApiResults.Forbidden("SELF_REACTION");
                }

                // 같은 값이면 삭제(토글 오프). 0건이면 없었거나 다른 값 → upsert.
                await using (var del = new NpgsqlCommand(
                    "delete from public.review_reactions where rating_id=@rid and user_id=@uid and value=@val", conn))
                {
                    del.Parameters.AddWithValue("rid", rid);
                    del.Parameters.AddWithValue("uid", uid);
                    del.Parameters.AddWithValue("val", req.Value!);
                    if (await del.ExecuteNonQueryAsync() == 0)
                    {
                        await using (var up = new NpgsqlCommand(
                            """
                            insert into public.review_reactions (rating_id, user_id, value)
                            values (@rid, @uid, @val)
                            on conflict (rating_id, user_id) do update set value = excluded.value
                            """, conn))
                        {
                            up.Parameters.AddWithValue("rid", rid);
                            up.Parameters.AddWithValue("uid", uid);
                            up.Parameters.AddWithValue("val", req.Value!);
                            await up.ExecuteNonQueryAsync();
                        }
                        if (req.Value == "like") // 좋아요 전환 → 리뷰 작성자에게 알림(자기 좋아요는 헬퍼가 스킵)
                        {
                            await using var author = new NpgsqlCommand(
                                "select user_id from public.ratings where id = @rid", conn);
                            author.Parameters.AddWithValue("rid", rid);
                            if (await author.ExecuteScalarAsync() is Guid authorId)
                                await Notifications.CreateAsync(conn, authorId, uid, "review_like", rid.ToString());
                        }
                    }
                }

                await using var q = new NpgsqlCommand(
                    """
                    select count(*) filter (where value='like'),
                           count(*) filter (where value='dislike'),
                           max(case when user_id=@uid then value end)
                    from public.review_reactions where rating_id=@rid
                    """, conn);
                q.Parameters.AddWithValue("rid", rid);
                q.Parameters.AddWithValue("uid", uid);
                await using var r = await q.ExecuteReaderAsync();
                await r.ReadAsync();
                var state = new ReactionState(
                    (int)r.GetInt64(0), (int)r.GetInt64(1), r.IsDBNull(2) ? null : r.GetString(2));
                return ApiResults.Ok("OK", state);
            }
            catch (PostgresException ex) when (ex.SqlState == "23503") // FK: rating/user 없음
            {
                return ApiResults.BadRequest("INVALID_TARGET");
            }
            catch (NpgsqlException)
            {
                return ApiResults.ServiceUnavailable("DB_UNAVAILABLE");
            }
        });
    }
}
