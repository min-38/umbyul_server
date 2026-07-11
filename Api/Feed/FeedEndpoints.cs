using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using Api.Common;
using Api.Profile;
using Npgsql;

namespace Api.Feed;

/// 홈 피드 (NON-88). 공개(옵셔널 인증). 리뷰 카드 목록 + Reddit식 정렬.
///   scope = all | following(로그인 필요), sort = hot | newest | likes | ratio | rising.
///   반응(review_reactions)으로 좋아요/싫어요 집계. 전부 우리 DB만 조회.
public static class FeedEndpoints
{
    public static void MapFeedEndpoints(this WebApplication app, string? dbConnString)
    {
        app.MapGet("/feed", async (string? sort, string? scope, string? genre, int? offset, int? limit, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            var me = Me(user);
            var off = Math.Max(offset ?? 0, 0);
            var lim = Math.Clamp(limit ?? 50, 1, 100);

            // 팔로잉인데 비로그인 → 빈 피드.
            if (scope == "following" && me is null) return ApiResults.Ok("OK", new FeedData([]));
            var scopeClause = scope == "following"
                ? "and r.user_id in (select following_id from public.follows where follower_id = @me)"
                : "";

            // 계산 정렬의 페이지 간 안정성 위해 tie-breaker(id) 포함.
            var orderBy = sort switch
            {
                "newest" => "created_at desc",
                "likes" => "likes desc, created_at desc",
                "ratio" => "wilson desc, likes desc",
                "rising" => "recent desc, created_at desc",
                _ => "hot desc", // 화제(기본)
            } + ", id";

            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync(ct);

                // 내 선호 장르만 보기(NON-88/150). 선호 장르로 태깅된 대상의 리뷰만. 선호 없음·테이블 부재 시 필터 미적용.
                int[] prefGenres = [];
                if (genre == "preferred" && me is { } gme)
                {
                    try
                    {
                        await using var pc = new NpgsqlCommand("select genre_id from public.user_genre_preferences where user_id = @me", conn);
                        pc.Parameters.AddWithValue("me", gme);
                        var pl = new List<int>();
                        await using var pr = await pc.ExecuteReaderAsync(ct);
                        while (await pr.ReadAsync(ct)) pl.Add(pr.GetInt32(0));
                        prefGenres = pl.ToArray();
                    }
                    catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedTable) { }
                }
                var genreClause = prefGenres.Length > 0
                    ? "and exists (select 1 from public.genre_tags gt where gt.target_type = r.target_type and gt.target_spotify_id = r.target_spotify_id and gt.genre_id = any(@prefgenres))"
                    : "";

                // 기본(hot)·rising 후보는 최근 30일로 바운드(ratings_created_active_idx 활용) — 전체 스캔 방지(QA6-7).
                var recencyClause = sort is null or "hot" or "rising"
                    ? "and r.created_at > now() - interval '30 days'"
                    : "";

                // 카운터 비정규화(QA6-7): likes/dislikes는 ratings 컬럼(::bigint 캐스트로 리더 계약 유지). recent(24h)만 바운드된 rx.
                // my_reaction/comment_count는 정렬·limit 이후 최종 페이지 행에만 계산(rx 전면 집계·정렬 전 per-row 서브플랜 제거).
                var newSql = $"""
                    with rx_recent as (
                      select rating_id, count(*) recent
                      from public.review_reactions
                      where created_at > now() - interval '24 hours'
                      group by rating_id
                    ),
                    ranked as (
                      select r.id, r.user_id, u.username, u.avatar_url, r.target_type, r.target_spotify_id,
                             r.score, r.review, r.created_at, r.target_name, r.target_artist, r.target_image_url, r.target_artists, r.target_explicit,
                             r.likes_count::bigint likes, r.dislikes_count::bigint dislikes, coalesce(rr.recent, 0) recent,
                             (log(greatest(abs(r.likes_count - r.dislikes_count), 1))
                               + sign(r.likes_count - r.dislikes_count) * extract(epoch from r.created_at) / 45000.0) hot,
                             (case when r.likes_count + r.dislikes_count = 0 then 0 else
                               ((r.likes_count::float / (r.likes_count + r.dislikes_count) + 1.9208 / (r.likes_count + r.dislikes_count))
                                 - 1.96 * sqrt(((r.likes_count::float / (r.likes_count + r.dislikes_count)) * (1 - r.likes_count::float / (r.likes_count + r.dislikes_count)) + 0.9604 / (r.likes_count + r.dislikes_count)) / (r.likes_count + r.dislikes_count)))
                               / (1 + 3.8416 / (r.likes_count + r.dislikes_count))
                             end) wilson
                      from public.ratings r
                      join public.users u on u.id = r.user_id
                      left join rx_recent rr on rr.rating_id = r.id
                      where r.review is not null and length(trim(r.review)) > 0 and r.target_spotify_id is not null and r.deleted_at is null {scopeClause} {genreClause} {recencyClause}
                        and not exists (select 1 from public.feed_dismissals d where d.user_id = @me and d.rating_id = r.id)
                        and not exists (select 1 from public.blocks b where (b.blocker_id = @me and b.blocked_id = r.user_id) or (b.blocker_id = r.user_id and b.blocked_id = @me))
                      order by {orderBy}
                      limit @lim offset @off
                    )
                    select ranked.id, ranked.user_id, ranked.username, ranked.avatar_url, ranked.target_type, ranked.target_spotify_id,
                           ranked.score, ranked.review, ranked.created_at, ranked.target_name, ranked.target_artist, ranked.target_image_url, ranked.target_artists,
                           ranked.likes, ranked.dislikes,
                           (select re.value from public.review_reactions re where re.rating_id = ranked.id and re.user_id = @me limit 1) my_reaction,
                           (select count(*) from public.review_comments c where c.rating_id = ranked.id and c.deleted_at is null) comment_count,
                           ranked.target_explicit
                    from ranked
                    order by {orderBy}
                    """;

                // 폴백: likes_count 컬럼 미적용(42703) 시 기존 rx 전면 집계 쿼리로 degrade(graceful).
                var oldSql = $"""
                    with rx as (
                      select rating_id,
                             count(*) filter (where value = 'like') likes,
                             count(*) filter (where value = 'dislike') dislikes,
                             count(*) filter (where created_at > now() - interval '24 hours') recent
                      from public.review_reactions
                      group by rating_id
                    ),
                    f as (
                      select r.id, r.user_id, u.username, u.avatar_url, r.target_type, r.target_spotify_id,
                             r.score, r.review, r.created_at, r.target_name, r.target_artist, r.target_image_url, r.target_artists, r.target_explicit,
                             coalesce(rx.likes, 0) likes, coalesce(rx.dislikes, 0) dislikes, coalesce(rx.recent, 0) recent,
                             (select re.value from public.review_reactions re where re.rating_id = r.id and re.user_id = @me limit 1) my_reaction,
                             (select count(*) from public.review_comments c where c.rating_id = r.id and c.deleted_at is null) comment_count
                      from public.ratings r
                      join public.users u on u.id = r.user_id
                      left join rx on rx.rating_id = r.id
                      where r.review is not null and length(trim(r.review)) > 0 and r.target_spotify_id is not null and r.deleted_at is null {scopeClause} {genreClause}
                        and not exists (select 1 from public.feed_dismissals d where d.user_id = @me and d.rating_id = r.id)
                        and not exists (select 1 from public.blocks b where (b.blocker_id = @me and b.blocked_id = r.user_id) or (b.blocker_id = r.user_id and b.blocked_id = @me))
                    )
                    select id, user_id, username, avatar_url, target_type, target_spotify_id,
                           score, review, created_at, target_name, target_artist, target_image_url, target_artists,
                           likes, dislikes, my_reaction, comment_count, target_explicit
                    from f
                    order by {orderBy}
                    limit @lim offset @off
                    """;

                async Task<List<FeedItem>> RunFeedAsync(string sql)
                {
                    await using var cmd = new NpgsqlCommand(sql, conn);
                    cmd.Parameters.AddWithValue("lim", lim);
                    cmd.Parameters.AddWithValue("off", off);
                    // @me 는 scopeClause · my_reaction · dismissal 필터에서 항상 참조 → 비로그인은 NULL 로 바인딩.
                    cmd.Parameters.Add(new NpgsqlParameter("me", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)me ?? DBNull.Value });
                    if (prefGenres.Length > 0) cmd.Parameters.AddWithValue("prefgenres", prefGenres);
                    var result = new List<FeedItem>();
                    await using var rd = await cmd.ExecuteReaderAsync(ct);
                    while (await rd.ReadAsync(ct))
                        result.Add(new FeedItem(
                            rd.GetGuid(0).ToString(), rd.GetGuid(1).ToString(), rd.GetString(2),
                            rd.IsDBNull(3) ? null : rd.GetString(3), rd.GetString(4), rd.GetString(5),
                            rd.GetDecimal(6), rd.GetString(7), rd.GetFieldValue<DateTimeOffset>(8),
                            rd.IsDBNull(9) ? null : rd.GetString(9), rd.IsDBNull(10) ? null : rd.GetString(10),
                            rd.IsDBNull(11) ? null : rd.GetString(11),
                            rd.IsDBNull(12) ? null : ArtistRef.Parse(rd.GetString(12)),
                            (int)rd.GetInt64(13), (int)rd.GetInt64(14),
                            rd.IsDBNull(15) ? null : rd.GetString(15),
                            (int)rd.GetInt64(16), rd.GetBoolean(17), Array.Empty<string>()));
                    return result;
                }

                List<FeedItem> list;
                try { list = await RunFeedAsync(newSql); }
                catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedColumn) { list = await RunFeedAsync(oldSql); }

                // 대상별 상위 장르 칩(NON-88/122). 크라우드 태깅 상위 2개(투표순). 리더 닫은 뒤 배치 조회.
                var genreMap = await LoadFeedGenresAsync(conn, list.Select(x => x.TargetSpotifyId).Distinct().ToArray(), ct);
                if (genreMap.Count > 0)
                    list = list.Select(x => genreMap.TryGetValue(x.TargetSpotifyId, out var gs) ? x with { Genres = gs } : x).ToList();

                // 작성자 레벨 뱃지(NON-163) — 배치 1회. 실패 시 기본 Lv 1.
                var levels = await LevelService.LoadLevelsAsync(conn, list.Select(x => Guid.Parse(x.UserId)).Distinct().ToList(), ct);
                if (levels.Count > 0)
                    list = list.Select(x => levels.TryGetValue(Guid.Parse(x.UserId), out var lv) ? x with { Level = lv } : x).ToList();

                return ApiResults.Ok("OK", new FeedData(list));
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // "관심 없음" — 이 리뷰를 내 피드에서 숨김(멱등). 인스타식, 취소 UI 없음.
        app.MapGroup("/me").RequireAuthorization()
           .MapPost("/feed/dismiss", async (DismissRequest req, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (user.FindFirstValue("sub") is not { Length: > 0 } sub || !Guid.TryParse(sub, out var uid))
                return ApiResults.Unauthorized("UNAUTHORIZED");
            if (!Guid.TryParse(req.RatingId, out var rid)) return ApiResults.BadRequest("INVALID_TARGET");

            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand(
                    """
                    insert into public.feed_dismissals (user_id, rating_id)
                    values (@uid, @rid)
                    on conflict (user_id, rating_id) do nothing
                    """, conn);
                cmd.Parameters.AddWithValue("uid", uid);
                cmd.Parameters.AddWithValue("rid", rid);
                await cmd.ExecuteNonQueryAsync(ct);
                return ApiResults.Ok("OK", new { });
            }
            catch (PostgresException ex) when (ex.SqlState == "23503") { return ApiResults.BadRequest("INVALID_TARGET"); }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });
    }

    public sealed record DismissRequest(string? RatingId);


    // 피드 대상별 상위 장르(투표순 2개). 크라우드 태깅(genre_tags). 테이블 없으면 빈 맵.
    private static async Task<Dictionary<string, IReadOnlyList<string>>> LoadFeedGenresAsync(NpgsqlConnection conn, string[] spotifyIds, CancellationToken ct)
    {
        var map = new Dictionary<string, IReadOnlyList<string>>();
        if (spotifyIds.Length == 0) return map;
        try
        {
            await using var cmd = new NpgsqlCommand(
                """
                select target_spotify_id, name from (
                  select gt.target_spotify_id, g.name,
                         row_number() over (partition by gt.target_spotify_id order by count(*) desc, g.sort_order) rn
                  from public.genre_tags gt
                  join public.genres g on g.id = gt.genre_id
                  where gt.target_spotify_id = any(@ids)
                  group by gt.target_spotify_id, g.id, g.name, g.sort_order
                ) x
                where x.rn <= 2
                order by x.target_spotify_id, x.rn
                """, conn);
            cmd.Parameters.AddWithValue("ids", spotifyIds);
            var tmp = new Dictionary<string, List<string>>();
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var id = rd.GetString(0);
                if (!tmp.TryGetValue(id, out var l)) { l = new List<string>(); tmp[id] = l; }
                l.Add(rd.GetString(1));
            }
            foreach (var kv in tmp) map[kv.Key] = kv.Value;
            return map;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedTable) { return map; }
    }

    private static Guid? Me(ClaimsPrincipal user) =>
        user.FindFirstValue("sub") is { Length: > 0 } id && Guid.TryParse(id, out var g) ? g : null;
}

public sealed record FeedItem(
    string Id, string UserId, string Username, string? AvatarUrl,
    string TargetType, string TargetSpotifyId, decimal Score, string Body, DateTimeOffset CreatedAt,
    string? Name, string? Artist, string? ImageUrl, IReadOnlyList<ArtistRef>? Artists,
    int Likes, int Dislikes, string? MyReaction, int CommentCount, bool Explicit,
    IReadOnlyList<string> Genres,
    int Level = 1); // 작성자 리뷰어 레벨(NON-163)
public sealed record FeedData(IReadOnlyList<FeedItem> Items);
