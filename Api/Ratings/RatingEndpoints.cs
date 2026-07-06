using System.Security.Claims;
using System.Text.Json;
using Api.Common;
using Npgsql;
using NpgsqlTypes;

namespace Api.Ratings;

/// 평점·리뷰 등록/수정/삭제 (NON-7). 로그인 필요. DB는 postgres 유저 → RLS 우회.
/// 1인 1평점/대상이라 등록은 upsert(conflict 시 갱신). 키 = ISRC/UPC(상세 응답의 targetId).
public static class RatingEndpoints
{
    public sealed record RatingRequest(
        string? TargetType, string? TargetId, string? SpotifyId, decimal Score, string? Review,
        string? Name, string? Artist, string? ImageUrl, IReadOnlyList<ArtistRef>? Artists, bool Explicit = false);

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

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
            var reviewLen = req.Review?.Trim().Length ?? 0;
            if (reviewLen < RatingValidation.MinReviewLength) return ApiResults.BadRequest("REVIEW_TOO_SHORT");
            if (reviewLen > RatingValidation.MaxReviewLength) return ApiResults.BadRequest("REVIEW_TOO_LONG");

            var review = string.IsNullOrWhiteSpace(req.Review) ? null : req.Review!.Trim();
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                if (await Moderation.CheckAsync(conn, Guid.Parse(id), default) is { } block) return Moderation.ToResult(block);
                await using var cmd = new NpgsqlCommand(
                    """
                    insert into public.ratings
                        (user_id, target_type, target_id, target_spotify_id, score, review,
                         target_name, target_artist, target_image_url, target_artists, target_explicit)
                    values (@uid, @tt, @tid, @sid, @score, @review, @name, @artist, @image, @artists, @explicit)
                    on conflict (user_id, target_type, target_id)
                    do update set score = excluded.score, review = excluded.review,
                                  target_spotify_id = excluded.target_spotify_id,
                                  target_name = excluded.target_name,
                                  target_artist = excluded.target_artist,
                                  target_image_url = excluded.target_image_url,
                                  target_artists = excluded.target_artists,
                                  target_explicit = excluded.target_explicit,
                                  deleted_at = null, deleted_by = null, deleted_reason = null
                    """, conn);
                cmd.Parameters.AddWithValue("uid", Guid.Parse(id));
                cmd.Parameters.AddWithValue("tt", req.TargetType!);
                cmd.Parameters.AddWithValue("tid", req.TargetId!.Trim());
                cmd.Parameters.AddWithValue("sid", (object?)(string.IsNullOrWhiteSpace(req.SpotifyId) ? null : req.SpotifyId!.Trim()) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("score", req.Score);
                cmd.Parameters.AddWithValue("review", (object?)review ?? DBNull.Value);
                cmd.Parameters.AddWithValue("name", (object?)Trim(req.Name) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("artist", (object?)Trim(req.Artist) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("image", (object?)Trim(req.ImageUrl) ?? DBNull.Value);
                var artistsJson = req.Artists is { Count: > 0 } ? JsonSerializer.Serialize(req.Artists) : null;
                cmd.Parameters.Add(new NpgsqlParameter("artists", NpgsqlDbType.Jsonb) { Value = (object?)artistsJson ?? DBNull.Value });
                cmd.Parameters.AddWithValue("explicit", req.Explicit);
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
