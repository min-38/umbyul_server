using System.Text.Json;
using Api.Common;
using Npgsql;

namespace Api.Chart;

/// Chart (NON-82). 공개. 우리 ratings 집계 랭킹(리더보드).
///   대상 type=all|album|track, 기간 period=day|week|month|year,
///   평가자 성별 gender=all|male|female, 나이대 age=all|10|20|30|40|50,
///   정렬 sort= most(최다 리뷰) | top(최고 평가·베이지안 가중).
/// 최고 평가는 "평가 1개 5.0" 문제를 베이지안 가중 평균으로 완화:
///   wr = v/(v+m)·R + m/(v+m)·C  (v=평가수, R=대상 평균, C=전체 평균, m=가중치)
public static class ChartEndpoints
{
    private const int M = 5;     // 베이지안 가중치(신뢰 붙기 시작하는 평가 수) — 튜닝값
    private const int MinV = 1;  // 차트 노출 최소 평가 수

    public static void MapChartEndpoints(this WebApplication app, string? dbConnString)
    {
        app.MapGet("/chart", async (string? type, string? sort, string? period, string? gender, string? age, CancellationToken ct) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");

            var t = type is "album" or "track" ? type : null; // all => 타입 무관
            var typeClause = t is null ? "" : "and r.target_type = @type";
            var orderBy = sort == "most" ? "v desc, wr desc" : "wr desc, v desc"; // 기본 top
            var interval = period switch
            {
                "day" => "and r.created_at > now() - interval '1 day'",
                "week" => "and r.created_at > now() - interval '7 days'",
                "month" => "and r.created_at > now() - interval '30 days'",
                _ => "and r.created_at > now() - interval '365 days'", // year(기본)
            };
            var g = gender is "male" or "female" ? gender : null;
            var genderClause = g is null ? "" : "and exists (select 1 from public.users u where u.id = r.user_id and u.gender = @gender)";
            var ageClause = age switch
            {
                "10" => AgeExists("between 10 and 19"),
                "20" => AgeExists("between 20 and 29"),
                "30" => AgeExists("between 30 and 39"),
                "40" => AgeExists("between 40 and 49"),
                "50" => AgeExists(">= 50"),
                _ => "",
            };
            var filters = $"{typeClause} {interval} {genderClause} {ageClause}";

            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand(
                    $"""
                    with base as (
                      select r.target_type, r.target_spotify_id,
                             count(*) v, avg(r.score) r,
                             max(r.target_name) name, max(r.target_artist) artist, max(r.target_image_url) image,
                             (array_agg(r.target_artists) filter (where r.target_artists is not null))[1] artists
                      from public.ratings r
                      where r.target_spotify_id is not null and r.deleted_at is null {filters}
                      group by r.target_type, r.target_spotify_id
                    ),
                    gc as (
                      select avg(r.score) c from public.ratings r
                      where r.target_spotify_id is not null and r.deleted_at is null {filters}
                    )
                    select b.target_type, b.target_spotify_id, b.v, round(b.r, 2)::float8,
                           b.name, b.artist, b.image, b.artists,
                           (b.v::numeric / (b.v + @m)) * b.r + (@m::numeric / (b.v + @m)) * gc.c as wr
                    from base b cross join gc
                    where b.v >= @minv
                    order by {orderBy}
                    limit 100
                    """, conn);
                cmd.Parameters.AddWithValue("m", M);
                cmd.Parameters.AddWithValue("minv", MinV);
                if (t is not null) cmd.Parameters.AddWithValue("type", t);
                if (g is not null) cmd.Parameters.AddWithValue("gender", g);

                var list = new List<ChartItem>();
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                    list.Add(new ChartItem(
                        rd.GetString(0), rd.GetString(1), (int)rd.GetInt64(2), rd.GetDouble(3),
                        rd.IsDBNull(4) ? null : rd.GetString(4), rd.IsDBNull(5) ? null : rd.GetString(5),
                        rd.IsDBNull(6) ? null : rd.GetString(6),
                        rd.IsDBNull(7) ? null : ParseArtists(rd.GetString(7))));
                return ApiResults.Ok("OK", new ChartData(list));
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        MapUserChart(app, dbConnString);
    }

    // 유저 차트(NON-86): 리뷰어 랭킹. sort= reviews(최다 리뷰) | likes(좋아요 받은) | followers(최다 팔로워).
    // 기간은 각 축의 이벤트 시각(ratings/review_reactions/follows.created_at) 기준.
    private static void MapUserChart(WebApplication app, string? dbConnString)
    {
        app.MapGet("/chart/users", async (string? sort, string? period, CancellationToken ct) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");

            string Interval(string col) => period switch
            {
                "day" => $"and {col} > now() - interval '1 day'",
                "week" => $"and {col} > now() - interval '7 days'",
                "month" => $"and {col} > now() - interval '30 days'",
                _ => $"and {col} > now() - interval '365 days'", // year(기본)
            };

            var sql = sort switch
            {
                "likes" => $"""
                    select u.id, u.username, u.avatar_url, count(*) n
                    from public.review_reactions x
                    join public.ratings r on r.id = x.rating_id and r.deleted_at is null
                    join public.users u on u.id = r.user_id
                    where x.value = 'like' {Interval("x.created_at")}
                    group by u.id, u.username, u.avatar_url
                    order by n desc
                    limit 100
                    """,
                "followers" => $"""
                    select u.id, u.username, u.avatar_url, count(*) n
                    from public.follows f
                    join public.users u on u.id = f.following_id
                    where true {Interval("f.created_at")}
                    group by u.id, u.username, u.avatar_url
                    order by n desc
                    limit 100
                    """,
                _ => $"""
                    select u.id, u.username, u.avatar_url, count(*) n
                    from public.ratings r
                    join public.users u on u.id = r.user_id
                    where r.review is not null and r.review <> '' and r.deleted_at is null {Interval("r.created_at")}
                    group by u.id, u.username, u.avatar_url
                    order by n desc
                    limit 100
                    """,
            };

            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand(sql, conn);

                var list = new List<UserRankItem>();
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                    list.Add(new UserRankItem(
                        rd.GetGuid(0).ToString(), rd.GetString(1),
                        rd.IsDBNull(2) ? null : rd.GetString(2), (int)rd.GetInt64(3)));
                return ApiResults.Ok("OK", new UserChartData(list));
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });
    }

    // 평가자 나이대 EXISTS(생년월일 기준 완료 나이). cond 는 검증된 리터럴만.
    private static string AgeExists(string cond) =>
        $"and exists (select 1 from public.users u where u.id = r.user_id and u.birth_date is not null and date_part('year', age(u.birth_date)) {cond})";

    private static IReadOnlyList<ArtistRef>? ParseArtists(string json)
    {
        try { return JsonSerializer.Deserialize<List<ArtistRef>>(json); }
        catch (JsonException) { return null; }
    }
}

public sealed record ChartItem(
    string TargetType, string SpotifyId, int Count, double Average,
    string? Name, string? Artist, string? ImageUrl, IReadOnlyList<ArtistRef>? Artists);
public sealed record ChartData(IReadOnlyList<ChartItem> Items);
public sealed record UserRankItem(string UserId, string Username, string? AvatarUrl, int Count);
public sealed record UserChartData(IReadOnlyList<UserRankItem> Items);
