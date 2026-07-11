using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace Api.Profile;

/// 여러 유저의 리뷰어 레벨을 한 번에 계산(NON-163) — 닉네임 옆 레벨 뱃지를 여러 화면에 붙이기 위함.
/// XP 집계는 ReviewerLevel과 동일(리뷰·받은 좋아요·픽 당일 리뷰).
/// 레벨은 느리게 변하므로 유저별 10분 TTL 캐시로 상관 서브쿼리 3개의 곱셈적 재계산을 방지(QA6-11).
public static class LevelService
{
    // 레벨 전용 프로세스 캐시(전역 IMemoryCache와 분리 — 호출처 시그니처 무변경). 엔트리당 10분 TTL로 자연 만료.
    private static readonly IMemoryCache Cache = new MemoryCache(new MemoryCacheOptions());
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    /// userId → level. 실패(마이그레이션 전 등) 시 해당 유저는 결과에서 빠져 호출부가 기본 Lv 1로 처리.
    /// daily_picks 미존재면 픽 보너스만 빼고 재시도(리뷰/좋아요는 코어 테이블).
    public static async Task<Dictionary<Guid, int>> LoadLevelsAsync(
        NpgsqlConnection conn, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var result = new Dictionary<Guid, int>();
        if (ids.Count == 0) return result;

        // 캐시 히트는 바로 채우고, 미스만 쿼리 대상으로.
        var misses = new List<Guid>();
        foreach (var id in ids.Distinct())
        {
            if (Cache.TryGetValue(id, out int lvl)) result[id] = lvl;
            else misses.Add(id);
        }
        if (misses.Count == 0) return result;
        var arr = misses.ToArray();

        // 상관 서브쿼리 3개(리뷰 수 / 받은 좋아요 / 픽 당일 리뷰)를 유저별 1행으로.
        const string withPicks = """
            select u.id,
              (select count(*) from public.ratings r
                 where r.user_id = u.id and r.review is not null and r.review <> '' and r.deleted_at is null),
              (select count(*) from public.review_reactions x
                 join public.ratings r on r.id = x.rating_id and r.deleted_at is null
                 where x.value = 'like' and r.user_id = u.id),
              (select count(*) from public.ratings r
                 join public.daily_picks d
                   on d.target_type = r.target_type and d.target_spotify_id = r.target_spotify_id
                  and d.pick_date = (r.created_at)::date
                 where r.user_id = u.id and r.deleted_at is null)
            from unnest(@ids) as u(id)
            """;
        const string noPicks = """
            select u.id,
              (select count(*) from public.ratings r
                 where r.user_id = u.id and r.review is not null and r.review <> '' and r.deleted_at is null),
              (select count(*) from public.review_reactions x
                 join public.ratings r on r.id = x.rating_id and r.deleted_at is null
                 where x.value = 'like' and r.user_id = u.id),
              0
            from unnest(@ids) as u(id)
            """;

        foreach (var sql in new[] { withPicks, noPicks })
        {
            try
            {
                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("ids", arr);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                {
                    var id = rd.GetGuid(0);
                    var level = ReviewerLevel.LevelFor(ReviewerLevel.Xp(
                        (int)rd.GetInt64(1), (int)rd.GetInt64(2), (int)rd.GetInt64(3)));
                    result[id] = level;
                    Cache.Set(id, level, Ttl);
                }
                return result;
            }
            catch (NpgsqlException) { /* 픽 없이 재시도, 그래도 실패면 캐시 히트만 반환 → 호출부 기본 Lv 1 */ }
        }
        return result;
    }
}
