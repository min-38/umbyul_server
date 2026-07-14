using Api.Common;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace Api.Sitemap;

/// 동적 상세 sitemap용 ID 나열(공개). web sitemap.xml이 track/album/artist 상세까지 색인하도록,
/// 살아있는 평가가 달린 모든 target의 (type, spotifyId)를 반환한다. 별도 targets 테이블은 없고 ratings에서 유도:
///   track/album은 target_type + target_spotify_id, artist는 target_artists JSONB 언네스트(Chart와 동일 패턴).
public static class SitemapEndpoints
{
    // 전체 카탈로그 스캔이라 크롤러가 자주 때려도 부담 없게 캐시(비개인화·저빈도 변경).
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    public static void MapSitemapEndpoints(this WebApplication app, string? dbConnString)
    {
        app.MapGet("/sitemap/targets", async (IMemoryCache cache, CancellationToken ct) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            const string cacheKey = "sitemap:targets";
            if (cache.TryGetValue(cacheKey, out IResult? cached) && cached is not null) return cached;

            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand(
                    """
                    select target_type as type, target_spotify_id as id
                    from public.ratings
                    where target_spotify_id is not null and deleted_at is null
                    group by target_type, target_spotify_id
                    union all
                    select 'artist', aid
                    from (
                      select coalesce(elem->>'Id', elem->>'id') as aid
                      from public.ratings r
                      cross join lateral jsonb_array_elements(r.target_artists) elem
                      where r.deleted_at is null and r.target_artists is not null
                    ) a
                    where aid is not null
                    group by aid
                    """, conn);

                var list = new List<SitemapTarget>();
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                    list.Add(new SitemapTarget(rd.GetString(0), rd.GetString(1)));
                var result = ApiResults.Ok("OK", list);
                cache.Set(cacheKey, result, CacheTtl);
                return result;
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });
    }
}

public sealed record SitemapTarget(string Type, string Id);
