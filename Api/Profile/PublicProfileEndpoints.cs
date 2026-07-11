using System.Security.Claims;
using System.Text.Json;
using Api.Common;
using Api.Detail;
using Api.Spotify;
using Npgsql;

namespace Api.Profile;

/// 공개 유저 프로필 (NON-24). 비로그인 열람. 유저 정보 + 작성 리뷰 목록.
/// 리뷰 대상 이름/이미지는 target_spotify_id 를 Spotify 배치 조회로 라이브 해석(콘텐츠 비영구).
public sealed record ProfileReview(
    string Id, string TargetType, string? SpotifyId, decimal Score, string? Body,
    DateTimeOffset CreatedAt, int LikeCount, string? Name, string? Artist, string? ImageUrl, bool Deleted, bool Explicit,
    // 아티스트 링크(각 id) + 트랙이면 앨범 링크(NON-24 개선). Spotify 라이브 해석에서 채움.
    IReadOnlyList<Api.Detail.ArtistRef>? Artists = null, string? AlbumId = null, string? AlbumName = null);

public sealed record UserProfile(
    string Id, string Username, string? AvatarUrl, DateTimeOffset JoinedAt,
    int ReviewCount, int TotalLikes,
    int Xp, int Level, int XpIntoLevel, int XpForLevel,
    int FollowerCount, int FollowingCount, bool IsFollowing,
    IReadOnlyList<ProfileReview> Reviews, bool Blocked);

public static class PublicProfileEndpoints
{
    private record Row(string Id, string Tt, string? Sid, string? TargetId, decimal Score, string? Body, DateTimeOffset Created, int Likes, bool Deleted, bool Explicit,
        string? Name, string? Artist, string? Image, string? Artists);

    public static void MapPublicProfileEndpoints(this WebApplication app, string? dbConnString)
    {
        app.MapGet("/users/{username}", async (string username, int? offset, int? limit, SpotifyClient spotify, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            var off = Math.Max(offset ?? 0, 0);
            // limit 미지정 시 전체(현 web은 클라 사이드 정렬이라 전체 필요) — 핵심 부하였던 평가당 Spotify 콜은
            // denormalized 우선 렌더로 제거됨(QA6-8). limit 지정 클라이언트는 opt-in 페이지네이션 가능.
            var lim = limit is int l ? Math.Clamp(l, 1, 50) : int.MaxValue;

            await using var conn = new NpgsqlConnection(dbConnString);
            try { await conn.OpenAsync(ct); }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }

            // 유저
            Guid uid;
            string uname;
            string? avatar;
            DateTimeOffset joined;
            await using (var ucmd = new NpgsqlCommand(
                "select id, username, avatar_url, created_at from public.users where lower(username) = lower(@u)", conn))
            {
                ucmd.Parameters.AddWithValue("u", username);
                await using var ur = await ucmd.ExecuteReaderAsync(ct);
                if (!await ur.ReadAsync(ct)) return ApiResults.NotFound("USER_NOT_FOUND");
                uid = ur.GetGuid(0);
                uname = ur.GetString(1);
                avatar = ur.IsDBNull(2) ? null : ur.GetString(2);
                joined = ur.GetFieldValue<DateTimeOffset>(3);
            }

            // 삭제(모더레이션) 리뷰는 공개엔 숨기고, 작성자 본인이 볼 때만 묘비로 노출.
            var viewer = Me(user);
            var isOwner = viewer == uid;

            // 차단 관계(상호) 확인 — NON-115. 상대가 나를 차단 → 숨김(404).
            // 내가 상대를 차단 → 리뷰/통계 숨기고 "차단됨" 상태만(웹이 차단 해제 UI 노출).
            if (viewer is { } vId && !isOwner)
            {
                bool iBlocked = false, blockedByThem = false;
                try
                {
                    await using var bcmd = new NpgsqlCommand(
                        """
                        select coalesce(bool_or(blocker_id = @viewer), false),
                               coalesce(bool_or(blocker_id = @uid), false)
                        from public.blocks
                        where (blocker_id = @viewer and blocked_id = @uid)
                           or (blocker_id = @uid and blocked_id = @viewer)
                        """, conn);
                    bcmd.Parameters.AddWithValue("viewer", vId);
                    bcmd.Parameters.AddWithValue("uid", uid);
                    await using var br = await bcmd.ExecuteReaderAsync(ct);
                    await br.ReadAsync(ct);
                    iBlocked = br.GetBoolean(0);
                    blockedByThem = br.GetBoolean(1);
                }
                catch (NpgsqlException) { } // blocks 미존재(마이그레이션 전) → 차단 없음으로 진행.

                if (blockedByThem && !iBlocked) return ApiResults.NotFound("USER_NOT_FOUND");
                if (iBlocked)
                {
                    var (bl, bxp, bInto, bFor) = ReviewerLevel.Compute(0, 0, 0);
                    return ApiResults.Ok("OK", new UserProfile(
                        uid.ToString(), uname, avatar, joined, 0, 0, bxp, bl, bInto, bFor, 0, 0, false, Array.Empty<ProfileReview>(), true));
                }
            }

            // 작성 리뷰 + 받은 좋아요 수. 마이그레이션(0006/0007) 미적용 등 실패 시 리뷰 없이 프로필만.
            var rows = new List<Row>();
            try
            {
                await using var rcmd = new NpgsqlCommand(
                    """
                    select r.id, r.target_type, r.target_spotify_id, r.target_id, r.score, r.review, r.created_at,
                           count(re.id) filter (where re.value = 'like') as likes, (r.deleted_at is not null) as deleted,
                           bool_or(r.target_explicit) as explicit,
                           r.target_name, r.target_artist, r.target_image_url, r.target_artists
                    from public.ratings r
                    left join public.review_reactions re on re.rating_id = r.id
                    where r.user_id = @uid and (r.deleted_at is null or @owner)
                    group by r.id
                    order by r.created_at desc
                    limit @lim offset @off
                    """, conn);
                rcmd.Parameters.AddWithValue("uid", uid);
                rcmd.Parameters.AddWithValue("owner", isOwner);
                rcmd.Parameters.AddWithValue("lim", lim);
                rcmd.Parameters.AddWithValue("off", off);
                await using var rr = await rcmd.ExecuteReaderAsync(ct);
                while (await rr.ReadAsync(ct))
                    rows.Add(new Row(
                        rr.GetGuid(0).ToString(), rr.GetString(1),
                        rr.IsDBNull(2) ? null : rr.GetString(2),
                        rr.IsDBNull(3) ? null : rr.GetString(3), rr.GetDecimal(4),
                        rr.IsDBNull(5) ? null : rr.GetString(5), rr.GetFieldValue<DateTimeOffset>(6),
                        (int)rr.GetInt64(7), rr.GetBoolean(8), rr.GetBoolean(9),
                        rr.IsDBNull(10) ? null : rr.GetString(10),
                        rr.IsDBNull(11) ? null : rr.GetString(11),
                        rr.IsDBNull(12) ? null : rr.GetString(12),
                        rr.IsDBNull(13) ? null : rr.GetString(13)));
            }
            catch (NpgsqlException)
            {
                rows.Clear();
            }

            // 통계(리뷰수·받은 좋아요)는 전체 활성 평가 기준 — 페이지네이션(위 limit)과 분리(QA6-8).
            var (reviewCount, totalLikes) = await LoadReviewStatsAsync(conn, uid, ct);
            var (followers, following, isFollowing) = await LoadFollowStatsAsync(conn, uid, viewer, ct);
            // 리뷰어 레벨(NON-153): XP = 10*리뷰 + 1*받은따봉 + 15*'픽 당일' 리뷰. 새 테이블 없이 집계.
            var dailyPickReviews = await LoadDailyPickReviewsAsync(conn, uid, ct);
            var (level, xp, xpInto, xpFor) = ReviewerLevel.Compute(reviewCount, totalLikes, dailyPickReviews);

            // 표시: denormalized 컬럼(target_name 등) 우선 — 모던 행은 Spotify 콜 0(피드·발견 방식).
            // 이름 없는 레거시(pre-0013) 활성 행만 Spotify 해석 → 평가당 1콜 폭주 제거(QA6-8).
            var legacy = rows.Where(x => !x.Deleted && x.Name is null).ToList();
            var display = await ResolveAsync(spotify, legacy, ct);

            var reviews = rows.Select(x =>
            {
                if (x.Name is not null)
                    return new ProfileReview(x.Id, x.Tt, x.Sid, x.Score, x.Body, x.Created, x.Likes, x.Name, x.Artist, x.Image, x.Deleted, x.Explicit,
                        x.Artists is not null
                            ? Api.Common.ArtistRef.Parse(x.Artists)?.Select(a => new Api.Detail.ArtistRef(a.Id ?? "", a.Name ?? "")).ToList()
                            : null, null, null);
                display.TryGetValue(x.Id, out var d);
                return new ProfileReview(x.Id, x.Tt, d.SpotifyId ?? x.Sid, x.Score, x.Body, x.Created, x.Likes, d.Name, d.Artist, d.Image, x.Deleted, x.Explicit, d.Artists, d.AlbumId, d.AlbumName);
            }).ToList();

            return ApiResults.Ok("OK", new UserProfile(
                uid.ToString(), uname, avatar, joined, reviewCount, totalLikes, xp, level, xpInto, xpFor,
                followers, following, isFollowing, reviews, false));
        });
    }

    // 전체 활성 리뷰 수 + 받은 좋아요 총합(페이지네이션과 무관한 통계). 실패 시 0/0.
    private static async Task<(int Count, int TotalLikes)> LoadReviewStatsAsync(NpgsqlConnection conn, Guid uid, CancellationToken ct)
    {
        try
        {
            await using var cmd = new NpgsqlCommand(
                """
                select count(*), coalesce(sum(likes), 0) from (
                  select r.id, count(re.id) filter (where re.value = 'like') likes
                  from public.ratings r
                  left join public.review_reactions re on re.rating_id = r.id
                  where r.user_id = @uid and r.deleted_at is null
                  group by r.id
                ) t
                """, conn);
            cmd.Parameters.AddWithValue("uid", uid);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            return ((int)r.GetInt64(0), (int)r.GetInt64(1));
        }
        catch (NpgsqlException) { return (0, 0); }
    }

    // '픽 당일' 리뷰 수(NON-153) — 리뷰 작성일이 그 대상의 daily_picks.pick_date 와 같은 것만.
    // daily_picks 미존재(0061 미적용) 등 실패 시 0.
    private static async Task<int> LoadDailyPickReviewsAsync(NpgsqlConnection conn, Guid uid, CancellationToken ct)
    {
        try
        {
            await using var cmd = new NpgsqlCommand(
                """
                select count(*)
                from public.ratings r
                join public.daily_picks dp
                  on dp.target_type = r.target_type
                 and dp.target_spotify_id = r.target_spotify_id
                 and dp.pick_date = (r.created_at)::date
                where r.user_id = @uid and r.deleted_at is null
                """, conn);
            cmd.Parameters.AddWithValue("uid", uid);
            var n = await cmd.ExecuteScalarAsync(ct);
            return n is long l ? (int)l : 0;
        }
        catch (NpgsqlException) { return 0; }
    }

    // 팔로워/팔로잉 수 + 뷰어의 팔로우 여부. follows 미존재(마이그레이션 전)면 0/false.
    private static async Task<(int Followers, int Following, bool IsFollowing)> LoadFollowStatsAsync(
        NpgsqlConnection conn, Guid uid, Guid? viewer, CancellationToken ct)
    {
        try
        {
            await using var cmd = new NpgsqlCommand(
                """
                select (select count(*) from public.follows where following_id = @uid),
                       (select count(*) from public.follows where follower_id = @uid),
                       exists(select 1 from public.follows where follower_id = @viewer and following_id = @uid)
                """, conn);
            cmd.Parameters.AddWithValue("uid", uid);
            cmd.Parameters.AddWithValue("viewer", (object?)viewer ?? DBNull.Value);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            return ((int)r.GetInt64(0), (int)r.GetInt64(1), r.GetBoolean(2));
        }
        catch (NpgsqlException) { return (0, 0, false); }
    }

    private static Guid? Me(ClaimsPrincipal user) =>
        user.FindFirstValue("sub") is { Length: > 0 } id && Guid.TryParse(id, out var g) ? g : null;

    private record Resolved(string RatingId, string? Name, string? Artist, string? Image, string? SpotifyId,
        IReadOnlyList<Api.Detail.ArtistRef>? Artists = null, string? AlbumId = null, string? AlbumName = null);

    // rating id → 표시(이름/아티스트/이미지/spotify_id). 단건 조회를 동시성 8로 병렬.
    // 1순위: target_spotify_id 직접. 폴백: ISRC(track)/UPC(album) 검색(spotify_id 없는 구 평점).
    private static async Task<Dictionary<string, (string? Name, string? Artist, string? Image, string? SpotifyId, IReadOnlyList<Api.Detail.ArtistRef>? Artists, string? AlbumId, string? AlbumName)>> ResolveAsync(
        SpotifyClient spotify, List<Row> rows, CancellationToken ct)
    {
        var map = new Dictionary<string, (string?, string?, string?, string?, IReadOnlyList<Api.Detail.ArtistRef>?, string?, string?)>();
        if (!spotify.Configured) return map;

        using var sem = new SemaphoreSlim(8);
        var tasks = rows.Select(async row =>
        {
            await sem.WaitAsync(ct);
            try { return await ResolveOneAsync(spotify, row, ct); }
            finally { sem.Release(); }
        });

        foreach (var r in await Task.WhenAll(tasks))
            if (r.Name is not null || r.SpotifyId is not null)
                map[r.RatingId] = (r.Name, r.Artist, r.Image, r.SpotifyId, r.Artists, r.AlbumId, r.AlbumName);

        return map;
    }

    private static async Task<Resolved> ResolveOneAsync(SpotifyClient spotify, Row row, CancellationToken ct)
    {
        try
        {
            // 1순위: spotify_id 직접 조회
            if (!string.IsNullOrEmpty(row.Sid))
            {
                var json = await spotify.GetAsync(row.Tt == "track" ? $"tracks/{row.Sid}" : $"albums/{row.Sid}", ct);
                if (json is not null)
                {
                    using var doc = JsonDocument.Parse(json);
                    return FromElement(row.Id, row.Tt, doc.RootElement);
                }
            }
            // 폴백: ISRC/UPC 검색 (1건)
            if (!string.IsNullOrEmpty(row.TargetId))
            {
                var filter = row.Tt == "track" ? $"isrc:{row.TargetId}" : $"upc:{row.TargetId}";
                var json = await spotify.SearchAsync(filter, row.Tt, 1, 0, ct);
                using var doc = JsonDocument.Parse(json);
                var key = row.Tt == "track" ? "tracks" : "albums";
                if (doc.RootElement.TryGetProperty(key, out var cat)
                    && cat.TryGetProperty("items", out var items)
                    && items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0)
                    return FromElement(row.Id, row.Tt, items[0]);
            }
        }
        catch (HttpRequestException) { }
        return new Resolved(row.Id, null, null, null, null);
    }

    private static Resolved FromElement(string ratingId, string tt, JsonElement el)
    {
        if (tt == "track")
        {
            var t = DetailParse.Track(el);
            return new Resolved(ratingId, t.Name, t.Artists.Count > 0 ? t.Artists[0].Name : null, t.Album?.ImageUrl, t.SpotifyId,
                t.Artists, t.Album?.Id, t.Album?.Name);
        }
        var a = DetailParse.Album(el);
        return new Resolved(ratingId, a.Name, a.Artists.Count > 0 ? a.Artists[0].Name : null, a.ImageUrl, a.SpotifyId, a.Artists);
    }
}
