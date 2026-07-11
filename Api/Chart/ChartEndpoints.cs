using System.Text.Json;
using Api.Common;
using Api.Profile;
using Microsoft.Extensions.Caching.Memory;
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
    // 성별·나이대 필터가 걸리면 소수 N 에서 개인의 성별/나이가 드러날 수 있어(재식별) 노출 임계를 올림 —
    // k-익명성(k=5). 예: "여성·20대" 필터에서 1명만 평가한 곡이 노출되면 그 1명이 특정됨 (LEG-10).
    private const int MinDemographicV = 5;
    private static readonly TimeSpan ChartCacheTtl = TimeSpan.FromMinutes(2); // 비개인화 차트 응답 캐시 TTL(QA6-10)

    public static void MapChartEndpoints(this WebApplication app, string? dbConnString)
    {
        app.MapGet("/chart", async (string? type, string? sort, string? period, string? gender, string? age, int? offset, int? limit, IMemoryCache cache, CancellationToken ct) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");

            // 차트는 비개인화(전 유저 동일 응답) — 파라미터 조합 키로 2분 캐시(QA6-10). 성공 응답만 캐시.
            var cacheKey = $"chart:{type}:{sort}:{period}:{gender}:{age}:{offset}:{limit}";
            if (cache.TryGetValue(cacheKey, out IResult? cachedResult) && cachedResult is not null) return cachedResult;

            var off = Math.Max(offset ?? 0, 0);
            var lim = Math.Clamp(limit ?? 50, 1, 200); // 페이지 크기(더 보기로 증가)
            var isArtist = type == "artist"; // 앨범/곡 평가를 아티스트별로 집계
            var t = type is "album" or "track" ? type : null; // all => 타입 무관
            var typeClause = t is null ? "" : "and r.target_type = @type";
            // 계산 정렬의 페이지 간 안정성 위해 tie-breaker(target_spotify_id) 포함.
            var orderBy = (sort == "most" ? "v desc, wr desc" : "wr desc, v desc") + ", b.target_spotify_id";
            var interval = IntervalFor(period);
            var g = gender is "male" or "female" ? gender : null;
            var genderClause = g is null ? "" : "and exists (select 1 from public.users u where u.id = r.user_id and u.gender = @gender)";
            var ageClause = AgeClauseFor(age);
            var filters = $"{typeClause} {interval} {genderClause} {ageClause}";
            // 아티스트: 타입 무관, target_artists 있는 것만. 나머지 필터는 동일.
            var artistFilters = $"{interval} {genderClause} {ageClause} and r.target_artists is not null";
            // 인구통계 필터(성별·나이) 활성 시 재식별 방지 위해 노출 임계 상향(LEG-10, k-익명성).
            var minv = MinVFor(gender, age);

            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync(ct);

                if (isArtist)
                {
                    var artistResult = await LoadArtistChartAsync(conn, artistFilters, sort, g, minv, off, lim, ct);
                    cache.Set(cacheKey, artistResult, ChartCacheTtl);
                    return artistResult;
                }

                await using var cmd = new NpgsqlCommand(
                    $"""
                    with base as (
                      -- 앵커(target_id=ISRC/UPC) 기준 집계 — 멀티에디션 병합. 대표 spotify_id는 최신 평가 것(QA6-3).
                      select r.target_type,
                             (array_agg(r.target_spotify_id order by r.created_at desc) filter (where r.target_spotify_id is not null))[1] target_spotify_id,
                             count(*) v, avg(r.score) r,
                             max(r.target_name) name, max(r.target_artist) artist, max(r.target_image_url) image,
                             (array_agg(r.target_artists order by r.created_at desc) filter (where r.target_artists is not null))[1] artists,
                             bool_or(r.target_explicit) explicit
                      from public.ratings r
                      where r.target_spotify_id is not null and r.deleted_at is null {filters}
                      group by r.target_type, r.target_id
                    ),
                    scored as (
                      -- 글로벌 평균(gc)을 base 1패스에서 window로 유도 → gc 재스캔 제거(QA6-10). minv 필터 전 전체 그룹 기준이라 값 동일.
                      select b.*, sum(b.v * b.r) over () / nullif(sum(b.v) over (), 0) gc
                      from base b
                    )
                    select b.target_type, b.target_spotify_id, b.v, round(b.r, 2)::float8,
                           b.name, b.artist, b.image, b.artists, b.explicit,
                           (b.v::numeric / (b.v + @m)) * b.r + (@m::numeric / (b.v + @m)) * b.gc as wr
                    from scored b
                    where b.v >= @minv
                    order by {orderBy}
                    limit @lim offset @off
                    """, conn);
                cmd.Parameters.AddWithValue("m", M);
                cmd.Parameters.AddWithValue("minv", minv);
                cmd.Parameters.AddWithValue("lim", lim);
                cmd.Parameters.AddWithValue("off", off);
                if (t is not null) cmd.Parameters.AddWithValue("type", t);
                if (g is not null) cmd.Parameters.AddWithValue("gender", g);

                var list = new List<ChartItem>();
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                    list.Add(new ChartItem(
                        rd.GetString(0), rd.GetString(1), (int)rd.GetInt64(2), rd.GetDouble(3),
                        rd.IsDBNull(4) ? null : rd.GetString(4), rd.IsDBNull(5) ? null : rd.GetString(5),
                        rd.IsDBNull(6) ? null : rd.GetString(6),
                        rd.IsDBNull(7) ? null : ArtistRef.Parse(rd.GetString(7)),
                        rd.GetBoolean(8)));
                var result = ApiResults.Ok("OK", new ChartData(list));
                cache.Set(cacheKey, result, ChartCacheTtl);
                return result;
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        MapUserChart(app, dbConnString);
    }

    // 유저 차트(NON-86): 리뷰어 랭킹. sort= reviews(최다 리뷰) | likes(좋아요 받은) | followers(최다 팔로워).
    // 기간은 각 축의 이벤트 시각(ratings/review_reactions/follows.created_at) 기준.
    private static void MapUserChart(WebApplication app, string? dbConnString)
    {
        app.MapGet("/chart/users", async (string? sort, string? period, int? offset, int? limit, CancellationToken ct) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");

            var off = Math.Max(offset ?? 0, 0);
            var lim = Math.Clamp(limit ?? 50, 1, 200);

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
                    order by n desc, u.id
                    limit @lim offset @off
                    """,
                "followers" => $"""
                    select u.id, u.username, u.avatar_url, count(*) n
                    from public.follows f
                    join public.users u on u.id = f.following_id
                    where true {Interval("f.created_at")}
                    group by u.id, u.username, u.avatar_url
                    order by n desc, u.id
                    limit @lim offset @off
                    """,
                _ => $"""
                    select u.id, u.username, u.avatar_url, count(*) n
                    from public.ratings r
                    join public.users u on u.id = r.user_id
                    where r.review is not null and r.review <> '' and r.deleted_at is null {Interval("r.created_at")}
                    group by u.id, u.username, u.avatar_url
                    order by n desc, u.id
                    limit @lim offset @off
                    """,
            };

            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("lim", lim);
                cmd.Parameters.AddWithValue("off", off);

                var list = new List<UserRankItem>();
                await using (var rd = await cmd.ExecuteReaderAsync(ct))
                    while (await rd.ReadAsync(ct))
                        list.Add(new UserRankItem(
                            rd.GetGuid(0).ToString(), rd.GetString(1),
                            rd.IsDBNull(2) ? null : rd.GetString(2), (int)rd.GetInt64(3)));

                // 랭킹 유저 레벨 뱃지(NON-163) — 배치 1회. 실패 시 기본 Lv 1.
                var levels = await LevelService.LoadLevelsAsync(conn, list.Select(x => Guid.Parse(x.UserId)).ToList(), ct);
                if (levels.Count > 0)
                    list = list.Select(x => levels.TryGetValue(Guid.Parse(x.UserId), out var lv) ? x with { Level = lv } : x).ToList();

                return ApiResults.Ok("OK", new UserChartData(list));
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });
    }

    // 평가자 나이대 EXISTS(생년월일 기준 완료 나이). cond 는 검증된 리터럴만.
    private static string AgeExists(string cond) =>
        $"and exists (select 1 from public.users u where u.id = r.user_id and u.birth_date is not null and date_part('year', age(u.birth_date)) {cond})";

    // 기간 → SQL interval 절(닫힌 화이트리스트, 인젝션 가드 — QA7-4). 기본 year.
    public static string IntervalFor(string? period) => period switch
    {
        "day" => "and r.created_at > now() - interval '1 day'",
        "week" => "and r.created_at > now() - interval '7 days'",
        "month" => "and r.created_at > now() - interval '30 days'",
        _ => "and r.created_at > now() - interval '365 days'", // year(기본)
    };

    // 연령대 → EXISTS 절(닫힌 화이트리스트). 미지정/무효 → "".
    public static string AgeClauseFor(string? age) => age switch
    {
        "10" => AgeExists("between 10 and 19"),
        "20" => AgeExists("between 20 and 29"),
        "30" => AgeExists("between 30 and 39"),
        "40" => AgeExists("between 40 and 49"),
        "50" => AgeExists(">= 50"),
        _ => "",
    };

    // 노출 최소 평가 수 — 인구통계 필터(성별·나이) 활성 시 재식별 방지로 상향(k-익명성, LEG-10/QA7-4).
    public static int MinVFor(string? gender, string? age) =>
        gender is "male" or "female" || age is "10" or "20" or "30" or "40" or "50" ? MinDemographicV : MinV;

    // 아티스트 차트(NON-87): 앨범/곡 평가를 target_artists 로 펼쳐 아티스트별 집계. 커버 없음.
    private static async Task<IResult> LoadArtistChartAsync(
        NpgsqlConnection conn, string filters, string? sort, string? gender, int minv, int off, int lim, CancellationToken ct)
    {
        var orderBy = (sort == "most" ? "v desc, wr desc" : "wr desc, v desc") + ", b.aid";
        await using var cmd = new NpgsqlCommand(
            $"""
            with base as (
              select coalesce(elem->>'Id', elem->>'id') aid,
                     max(coalesce(elem->>'Name', elem->>'name')) aname,
                     count(*) v, avg(r.score) r
              from public.ratings r
              cross join lateral jsonb_array_elements(r.target_artists) elem
              where r.target_spotify_id is not null and r.deleted_at is null {filters}
              group by coalesce(elem->>'Id', elem->>'id')
            ),
            scored as (
              -- 글로벌 평균(gc)을 base 1패스에서 window로 유도 → gc 재스캔 제거(QA6-10).
              select b.*, sum(b.v * b.r) over () / nullif(sum(b.v) over (), 0) gc
              from base b
            )
            select b.aid, b.aname, b.v, round(b.r, 2)::float8,
                   (b.v::numeric / (b.v + @m)) * b.r + (@m::numeric / (b.v + @m)) * b.gc as wr
            from scored b
            where b.aid is not null and b.v >= @minv
            order by {orderBy}
            limit @lim offset @off
            """, conn);
        cmd.Parameters.AddWithValue("m", M);
        cmd.Parameters.AddWithValue("minv", minv);
        cmd.Parameters.AddWithValue("lim", lim);
        cmd.Parameters.AddWithValue("off", off);
        if (gender is not null) cmd.Parameters.AddWithValue("gender", gender);

        var list = new List<ArtistRankItem>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            list.Add(new ArtistRankItem(
                rd.GetString(0), rd.IsDBNull(1) ? null : rd.GetString(1),
                (int)rd.GetInt64(2), rd.GetDouble(3)));
        return ApiResults.Ok("OK", new ArtistChartData(list));
    }

}

public sealed record ChartItem(
    string TargetType, string SpotifyId, int Count, double Average,
    string? Name, string? Artist, string? ImageUrl, IReadOnlyList<ArtistRef>? Artists, bool Explicit);
public sealed record ChartData(IReadOnlyList<ChartItem> Items);
public sealed record UserRankItem(string UserId, string Username, string? AvatarUrl, int Count, int Level = 1);
public sealed record UserChartData(IReadOnlyList<UserRankItem> Items);
public sealed record ArtistRankItem(string ArtistId, string? ArtistName, int Count, double Average);
public sealed record ArtistChartData(IReadOnlyList<ArtistRankItem> Items);
