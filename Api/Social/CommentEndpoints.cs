using System.Security.Claims;
using Api.Common;
using Npgsql;

namespace Api.Social;

/// 리뷰 댓글 (NON-36). 열람은 공개, 작성/삭제는 로그인. 본인 댓글만 삭제.
public static class CommentEndpoints
{
    public const int MaxBodyLength = 1000;

    public sealed record CommentRequest(string? RatingId, string? Body);
    public sealed record CommentItem(
        string Id, string UserId, string Username, string? AvatarUrl, string Body, DateTimeOffset CreatedAt);

    public static void MapCommentEndpoints(this WebApplication app, string? dbConnString)
    {
        // 공개: 리뷰의 댓글 목록(오래된 순). 상세가 비로그인 열람이라 인증 불필요.
        app.MapGet("/detail/comments/{ratingId}", async (string ratingId) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (!Guid.TryParse(ratingId, out var rid)) return ApiResults.BadRequest("INVALID_TARGET");

            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    """
                    select c.id, c.user_id, u.username, u.avatar_url, c.body, c.created_at
                    from public.review_comments c
                    join public.users u on u.id = c.user_id
                    where c.rating_id = @rid
                    order by c.created_at asc
                    """, conn);
                cmd.Parameters.AddWithValue("rid", rid);

                var items = new List<CommentItem>();
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    items.Add(new CommentItem(
                        r.GetGuid(0).ToString(), r.GetGuid(1).ToString(), r.GetString(2),
                        r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5)));
                return ApiResults.Ok("OK", new { items });
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        var me = app.MapGroup("/me").RequireAuthorization();

        // 댓글 작성 → 생성된 댓글(작성자 정보 포함) 반환
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
                await using var cmd = new NpgsqlCommand(
                    """
                    with ins as (
                        insert into public.review_comments (rating_id, user_id, body)
                        values (@rid, @uid, @body)
                        returning id, user_id, body, created_at
                    )
                    select ins.id, ins.user_id, u.username, u.avatar_url, ins.body, ins.created_at
                    from ins join public.users u on u.id = ins.user_id
                    """, conn);
                cmd.Parameters.AddWithValue("rid", rid);
                cmd.Parameters.AddWithValue("uid", uid);
                cmd.Parameters.AddWithValue("body", body);
                await using var r = await cmd.ExecuteReaderAsync();
                await r.ReadAsync();
                var item = new CommentItem(
                    r.GetGuid(0).ToString(), r.GetGuid(1).ToString(), r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5));
                return ApiResults.Created("CREATED", item);
            }
            catch (PostgresException ex) when (ex.SqlState == "23503") { return ApiResults.BadRequest("INVALID_TARGET"); }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // 댓글 삭제 — 본인 것만
        me.MapDelete("/comments/{id}", async (string id, ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Sub(user) is not { } uid) return ApiResults.Unauthorized("UNAUTHORIZED");
            if (!Guid.TryParse(id, out var cid)) return ApiResults.BadRequest("INVALID_TARGET");

            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    "delete from public.review_comments where id = @cid and user_id = @uid", conn);
                cmd.Parameters.AddWithValue("cid", cid);
                cmd.Parameters.AddWithValue("uid", uid);
                if (await cmd.ExecuteNonQueryAsync() == 0) return ApiResults.NotFound("COMMENT_NOT_FOUND");
                return ApiResults.Ok("OK");
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });
    }

    private static Guid? Sub(ClaimsPrincipal user) =>
        user.FindFirstValue("sub") is { Length: > 0 } id && Guid.TryParse(id, out var g) ? g : null;
}
