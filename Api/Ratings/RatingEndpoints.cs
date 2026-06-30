using System.Security.Claims;
using Api.Common;
using Npgsql;

namespace Api.Ratings;

/// 평점·리뷰 등록/수정/삭제 (NON-7). 로그인 필요. DB는 postgres 유저 → RLS 우회.
/// 1인 1평점/대상이라 등록은 upsert(conflict 시 갱신). 키 = ISRC/UPC(상세 응답의 targetId).
public static class RatingEndpoints
{
    public sealed record RatingRequest(string? TargetType, string? TargetId, decimal Score, string? Review);

    public static void MapRatingEndpoints(this WebApplication app, string? dbConnString)
    {
        var me = app.MapGroup("/me").RequireAuthorization();

        // 등록·수정 (upsert)
        me.MapPost("/ratings", async (RatingRequest req, ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (user.FindFirstValue("sub") is not { Length: > 0 } id) return ApiResults.Unauthorized("UNAUTHORIZED");
            if (!RatingValidation.IsTargetType(req.TargetType)) return ApiResults.BadRequest("INVALID_TARGET_TYPE");
            if (string.IsNullOrWhiteSpace(req.TargetId)) return ApiResults.BadRequest("INVALID_TARGET");
            if (!RatingValidation.IsScore(req.Score)) return ApiResults.BadRequest("INVALID_SCORE");
            if (!RatingValidation.IsReview(req.Review)) return ApiResults.BadRequest("REVIEW_TOO_LONG");

            var review = string.IsNullOrWhiteSpace(req.Review) ? null : req.Review!.Trim();
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    """
                    insert into public.ratings (user_id, target_type, target_id, score, review)
                    values (@uid, @tt, @tid, @score, @review)
                    on conflict (user_id, target_type, target_id)
                    do update set score = excluded.score, review = excluded.review
                    """, conn);
                cmd.Parameters.AddWithValue("uid", Guid.Parse(id));
                cmd.Parameters.AddWithValue("tt", req.TargetType!);
                cmd.Parameters.AddWithValue("tid", req.TargetId!.Trim());
                cmd.Parameters.AddWithValue("score", req.Score);
                cmd.Parameters.AddWithValue("review", (object?)review ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync();
                return ApiResults.Ok("OK");
            }
            catch (PostgresException ex) when (ex.SqlState == "23503") // FK 위반: public.users row 없음(온보딩 전)
            {
                return ApiResults.BadRequest("PROFILE_REQUIRED");
            }
            catch (NpgsqlException)
            {
                return ApiResults.ServiceUnavailable("DB_UNAVAILABLE");
            }
        });

        // 내 평점 삭제 (대상 기준)
        me.MapDelete("/ratings", async (string? targetType, string? targetId, ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (user.FindFirstValue("sub") is not { Length: > 0 } id) return ApiResults.Unauthorized("UNAUTHORIZED");
            if (!RatingValidation.IsTargetType(targetType)) return ApiResults.BadRequest("INVALID_TARGET_TYPE");
            if (string.IsNullOrWhiteSpace(targetId)) return ApiResults.BadRequest("INVALID_TARGET");

            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    "delete from public.ratings where user_id = @uid and target_type = @tt and target_id = @tid", conn);
                cmd.Parameters.AddWithValue("uid", Guid.Parse(id));
                cmd.Parameters.AddWithValue("tt", targetType!);
                cmd.Parameters.AddWithValue("tid", targetId!.Trim());
                var n = await cmd.ExecuteNonQueryAsync();
                return n > 0 ? ApiResults.Ok("OK") : ApiResults.NotFound("RATING_NOT_FOUND");
            }
            catch (NpgsqlException)
            {
                return ApiResults.ServiceUnavailable("DB_UNAVAILABLE");
            }
        });
    }
}
