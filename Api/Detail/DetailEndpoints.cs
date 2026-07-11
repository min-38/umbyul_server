using System.Security.Claims;
using System.Text.Json;
using Api.Common;
using Api.Profile;
using Api.Spotify;
using Npgsql;

namespace Api.Detail;

/// 앨범/곡 상세 (NON-6). 공개(비로그인 열람). Spotify 라이브 조회 + 우리 평점/리뷰.
/// 평점 키 = ISRC(track)/UPC(album), 없으면 spotify_id 폴백.
/// 장르는 앱 토큰으로 안 내려와 제외(실측). 레이블은 앨범 copyrights로 대체.
public static class DetailEndpoints
{
    // 앱 토큰은 market 없이는 explicit 를 항상 false(=unknown)로 준다(실측). KR 시장 붙이면 정확.
    // id/ISRC 는 relinking 없이 유지됨(실측) — 평점 키 안 흔들림. (BUG-14)
    private const string Market = "KR";

    public static void MapDetailEndpoints(this WebApplication app, string? dbConnString)
    {
        app.MapGet("/detail/track/{id}", async (string id, SpotifyClient spotify, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (!spotify.Configured) return ApiResults.ServiceUnavailable("SPOTIFY_NOT_CONFIGURED");

            string? json;
            try { json = await spotify.GetAsync($"tracks/{id}?market={Market}", ct); }
            catch (HttpRequestException) { return ApiResults.ServiceUnavailable("SPOTIFY_UNAVAILABLE"); }
            if (json is null) return ApiResults.NotFound("TRACK_NOT_FOUND");

            DetailParse.ParsedTrack t;
            using (var doc = JsonDocument.Parse(json)) t = DetailParse.Track(doc.RootElement);

            // 레이블/저작권은 앨범 객체(copyrights)에 있어 별도 조회. 실패해도 본문은 살린다.
            var copyright = await FetchCopyrightAsync(spotify, t.AlbumId, ct);
            var targetId = t.Isrc ?? t.SpotifyId;
            var (summary, reviews) = await LoadReviewsAsync(dbConnString, "track", targetId, Me(user), ct);
            var youtube = await TargetLinks.YoutubeAsync(dbConnString, "track", targetId, t.SpotifyId, ct);

            return ApiResults.Ok("OK", new TrackDetail(
                t.SpotifyId, t.Name, t.SpotifyUrl, t.Artists, t.Album, t.Isrc, targetId, t.DurationMs,
                t.ReleaseDate, copyright, t.Explicit, summary, reviews, youtube));
        });

        app.MapGet("/detail/album/{id}", async (string id, SpotifyClient spotify, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (!spotify.Configured) return ApiResults.ServiceUnavailable("SPOTIFY_NOT_CONFIGURED");

            string? json;
            try { json = await spotify.GetAsync($"albums/{id}?market={Market}", ct); }
            catch (HttpRequestException) { return ApiResults.ServiceUnavailable("SPOTIFY_UNAVAILABLE"); }
            if (json is null) return ApiResults.NotFound("ALBUM_NOT_FOUND");

            DetailParse.ParsedAlbum a;
            using (var doc = JsonDocument.Parse(json)) a = DetailParse.Album(doc.RootElement);

            var targetId = a.Upc ?? a.SpotifyId;
            var (summary, reviews) = await LoadReviewsAsync(dbConnString, "album", targetId, Me(user), ct);

            // 수록곡별 평점(트랙리스트에 별점 표시). target_spotify_id로 곡 평가 집계.
            var trackRatings = await LoadTrackRatingsAsync(dbConnString, a.Tracks.Select(t => t.Id).ToList(), ct);
            var tracks = a.Tracks
                .Select(t => trackRatings.TryGetValue(t.Id, out var r) ? t with { Rating = r } : t)
                .ToList();

            var youtube = await TargetLinks.YoutubeAsync(dbConnString, "album", targetId, a.SpotifyId, ct);

            return ApiResults.Ok("OK", new AlbumDetail(
                a.SpotifyId, a.Name, a.SpotifyUrl, a.Artists, a.ImageUrl, a.Upc, targetId, a.ReleaseDate, a.Copyright,
                a.TotalTracks, tracks, summary, reviews, youtube));
        });
    }

    private static async Task<string?> FetchCopyrightAsync(SpotifyClient spotify, string? albumId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(albumId)) return null;
        try
        {
            var json = await spotify.GetAsync($"albums/{albumId}", ct);
            if (json is null) return null;
            using var doc = JsonDocument.Parse(json);
            return DetailParse.Copyright(doc.RootElement);
        }
        catch (HttpRequestException) { return null; }
    }

    // 토큰이 있으면 내 user id, 없으면 null. 상세는 공개라 [Authorize] 아님 → 옵셔널.
    private static Guid? Me(ClaimsPrincipal user) =>
        user.FindFirstValue("sub") is { Length: > 0 } id && Guid.TryParse(id, out var g) ? g : null;

    // 수록곡 spotify_id → 평균·개수. 구 평점(target_spotify_id null)은 매칭 안 됨(허용).
    private static async Task<Dictionary<string, RatingSummary>> LoadTrackRatingsAsync(
        string? dbConnString, IReadOnlyList<string> trackIds, CancellationToken ct)
    {
        var map = new Dictionary<string, RatingSummary>();
        if (dbConnString is null || trackIds.Count == 0) return map;
        try
        {
            await using var conn = new NpgsqlConnection(dbConnString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                """
                -- 앵커(target_id=ISRC) 기준 집계 — 요청 spotify_id의 앵커로 해석 후 멀티에디션 병합(QA6-3).
                with req as (
                  select distinct target_spotify_id, target_id
                  from public.ratings
                  where target_type = 'track' and target_spotify_id = any(@ids) and deleted_at is null
                ),
                agg as (
                  select r.target_id, round(avg(r.score), 2)::float8 avg, count(*) cnt
                  from public.ratings r
                  where r.target_type = 'track' and r.deleted_at is null
                    and r.target_id in (select target_id from req)
                  group by r.target_id
                )
                select req.target_spotify_id, agg.avg, agg.cnt
                from req join agg on agg.target_id = req.target_id
                """, conn);
            cmd.Parameters.AddWithValue("ids", trackIds.ToArray());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                map[r.GetString(0)] = new RatingSummary(r.GetDouble(1), (int)r.GetInt64(2));
        }
        catch (NpgsqlException) { /* 트랙 평점은 없어도 트랙리스트는 살린다 */ }
        return map;
    }

    private const int ReviewPageSize = 50; // 상세 리뷰 목록 페이지 크기(무제한 반환·per-row 서브플랜 방지, QA6-9)

    // 우리 DB의 평점/리뷰 + 평균 + 리뷰별 좋아요/싫어요 집계, me 의 반응(NON-23).
    private static async Task<(RatingSummary, IReadOnlyList<ReviewItem>)> LoadReviewsAsync(
        string? dbConnString, string targetType, string targetId, Guid? me, CancellationToken ct)
    {
        var empty = new RatingSummary(null, 0);
        if (dbConnString is null) return (empty, []);
        try
        {
            await using var conn = new NpgsqlConnection(dbConnString);
            await conn.OpenAsync(ct);

            // 요약(평균·개수)은 리뷰 목록과 분리해 전체 평가 기준으로 집계(ratings_target_idx 커버) — 목록 바운드와 무관하게 정확(QA6-9).
            RatingSummary summary = empty;
            await using (var sc = new NpgsqlCommand(
                "select count(*), round(avg(score), 2)::float8 from public.ratings where target_type = @tt and target_id = @tid and deleted_at is null", conn))
            {
                sc.Parameters.AddWithValue("tt", targetType);
                sc.Parameters.AddWithValue("tid", targetId);
                await using var sr = await sc.ExecuteReaderAsync(ct);
                if (await sr.ReadAsync(ct))
                {
                    var cnt = (int)sr.GetInt64(0);
                    if (cnt > 0) summary = new RatingSummary(sr.IsDBNull(1) ? null : sr.GetDouble(1), cnt);
                }
            }

            await using var cmd = new NpgsqlCommand(
                """
                select r.id, r.user_id, u.username, u.avatar_url, r.score, r.review, r.created_at,
                       count(re.id) filter (where re.value = 'like')    as likes,
                       count(re.id) filter (where re.value = 'dislike') as dislikes,
                       max(case when re.user_id = @me then re.value end) as my_reaction,
                       (select count(*) from public.review_comments c where c.rating_id = r.id and c.deleted_at is null) as comments
                from public.ratings r
                join public.users u on u.id = r.user_id
                left join public.review_reactions re on re.rating_id = r.id
                where r.target_type = @tt and r.target_id = @tid and r.deleted_at is null
                group by r.id, u.username, u.avatar_url
                order by r.created_at desc
                limit @lim
                """, conn);
            cmd.Parameters.AddWithValue("tt", targetType);
            cmd.Parameters.AddWithValue("tid", targetId);
            cmd.Parameters.AddWithValue("me", (object?)me ?? DBNull.Value);
            cmd.Parameters.AddWithValue("lim", ReviewPageSize);

            var reviews = new List<ReviewItem>();
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                    reviews.Add(new ReviewItem(
                        r.GetGuid(0).ToString(),
                        r.GetGuid(1).ToString(),
                        r.GetString(2),
                        r.IsDBNull(3) ? null : r.GetString(3),
                        r.GetDecimal(4),
                        r.IsDBNull(5) ? null : r.GetString(5),
                        r.GetFieldValue<DateTimeOffset>(6),
                        (int)r.GetInt64(7),
                        (int)r.GetInt64(8),
                        r.IsDBNull(9) ? null : r.GetString(9),
                        (int)r.GetInt64(10)));
            }

            // 리뷰 작성자 레벨 뱃지(NON-163) — 배치 1회. 실패 시 기본 Lv 1.
            var levels = await LevelService.LoadLevelsAsync(conn, reviews.Select(x => Guid.Parse(x.UserId)).ToList(), ct);
            if (levels.Count > 0)
                for (int i = 0; i < reviews.Count; i++)
                    if (levels.TryGetValue(Guid.Parse(reviews[i].UserId), out var lv)) reviews[i] = reviews[i] with { Level = lv };

            return (summary, reviews);
        }
        catch (NpgsqlException) { return (empty, []); }
    }
}
