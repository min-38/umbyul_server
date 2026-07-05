using Api.Common;
using Npgsql;

namespace Api.Detail;

/// 곡/앨범 평점 "시세" (NON-124). created_at 순 일별 누적 평균 시계열. 공개·순수 DB.
/// anchor는 상세 평점과 동일(target_type + target_id = ISRC/UPC). 웹이 상세의 targetId를 그대로 넘김 → Spotify 재호출 0.
public static class RatingHistoryEndpoints
{
    public sealed record RatingPoint(DateOnly Date, double Avg, int Count);

    public static void MapRatingHistoryEndpoints(this WebApplication app, string? dbConnString)
    {
        app.MapGet("/detail/rating-history", async (string? type, string? id, CancellationToken ct) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (type is not ("track" or "album") || string.IsNullOrWhiteSpace(id))
                return ApiResults.BadRequest("INVALID_TARGET");
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand(
                    """
                    with daily as (
                        select date_trunc('day', created_at)::date as d,
                               count(*)::int as c, sum(score) as s
                        from public.ratings
                        where target_type = @tt and target_id = @id and deleted_at is null
                        group by 1
                    )
                    select d,
                           round(sum(s) over w / sum(c) over w, 2)::float8 as cum_avg,
                           (sum(c) over w)::int                            as cum_count
                    from daily
                    window w as (order by d)
                    order by d
                    """, conn);
                cmd.Parameters.AddWithValue("tt", type);
                cmd.Parameters.AddWithValue("id", id.Trim());

                var points = new List<RatingPoint>();
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    points.Add(new RatingPoint(r.GetFieldValue<DateOnly>(0), r.GetDouble(1), r.GetInt32(2)));
                return ApiResults.Ok("OK", points);
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });
    }
}
