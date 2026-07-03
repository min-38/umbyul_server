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
    DateTimeOffset CreatedAt, int LikeCount, string? Name, string? Artist, string? ImageUrl, bool Deleted);

public sealed record UserProfile(
    string Id, string Username, string? AvatarUrl, DateTimeOffset JoinedAt,
    int ReviewCount, int TotalLikes,
    int FollowerCount, int FollowingCount, bool IsFollowing,
    IReadOnlyList<ProfileReview> Reviews);

public static class PublicProfileEndpoints
{
    private record Row(string Id, string Tt, string? Sid, string? TargetId, decimal Score, string? Body, DateTimeOffset Created, int Likes, bool Deleted);

    public static void MapPublicProfileEndpoints(this WebApplication app, string? dbConnString)
    {
        app.MapGet("/users/{username}", async (string username, SpotifyClient spotify, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");

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

            // 작성 리뷰 + 받은 좋아요 수. 마이그레이션(0006/0007) 미적용 등 실패 시 리뷰 없이 프로필만.
            var rows = new List<Row>();
            try
            {
                await using var rcmd = new NpgsqlCommand(
                    """
                    select r.id, r.target_type, r.target_spotify_id, r.target_id, r.score, r.review, r.created_at,
                           count(re.id) filter (where re.value = 'like') as likes, (r.deleted_at is not null) as deleted
                    from public.ratings r
                    left join public.review_reactions re on re.rating_id = r.id
                    where r.user_id = @uid and (r.deleted_at is null or @owner)
                    group by r.id
                    order by r.created_at desc
                    """, conn);
                rcmd.Parameters.AddWithValue("uid", uid);
                rcmd.Parameters.AddWithValue("owner", isOwner);
                await using var rr = await rcmd.ExecuteReaderAsync(ct);
                while (await rr.ReadAsync(ct))
                    rows.Add(new Row(
                        rr.GetGuid(0).ToString(), rr.GetString(1),
                        rr.IsDBNull(2) ? null : rr.GetString(2),
                        rr.IsDBNull(3) ? null : rr.GetString(3), rr.GetDecimal(4),
                        rr.IsDBNull(5) ? null : rr.GetString(5), rr.GetFieldValue<DateTimeOffset>(6),
                        (int)rr.GetInt64(7), rr.GetBoolean(8)));
            }
            catch (NpgsqlException)
            {
                rows.Clear();
            }

            // 통계·Spotify 해석은 살아있는 리뷰만 대상(삭제 묘비는 카운트·점수에서 제외).
            var active = rows.Where(x => !x.Deleted).ToList();
            var totalLikes = active.Sum(x => x.Likes);
            var (followers, following, isFollowing) = await LoadFollowStatsAsync(conn, uid, viewer, ct);
            var display = await ResolveAsync(spotify, active, ct);

            var reviews = rows.Select(x =>
            {
                display.TryGetValue(x.Id, out var d);
                return new ProfileReview(x.Id, x.Tt, d.SpotifyId ?? x.Sid, x.Score, x.Body, x.Created, x.Likes, d.Name, d.Artist, d.Image, x.Deleted);
            }).ToList();

            return ApiResults.Ok("OK", new UserProfile(
                uid.ToString(), uname, avatar, joined, active.Count, totalLikes, followers, following, isFollowing, reviews));
        });
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

    private record Resolved(string RatingId, string? Name, string? Artist, string? Image, string? SpotifyId);

    // rating id → 표시(이름/아티스트/이미지/spotify_id). 단건 조회를 동시성 8로 병렬.
    // 1순위: target_spotify_id 직접. 폴백: ISRC(track)/UPC(album) 검색(spotify_id 없는 구 평점).
    private static async Task<Dictionary<string, (string? Name, string? Artist, string? Image, string? SpotifyId)>> ResolveAsync(
        SpotifyClient spotify, List<Row> rows, CancellationToken ct)
    {
        var map = new Dictionary<string, (string?, string?, string?, string?)>();
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
                map[r.RatingId] = (r.Name, r.Artist, r.Image, r.SpotifyId);

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
            return new Resolved(ratingId, t.Name, t.Artists.Count > 0 ? t.Artists[0].Name : null, t.Album?.ImageUrl, t.SpotifyId);
        }
        var a = DetailParse.Album(el);
        return new Resolved(ratingId, a.Name, a.Artists.Count > 0 ? a.Artists[0].Name : null, a.ImageUrl, a.SpotifyId);
    }
}
