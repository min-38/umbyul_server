using System.Text.Json;
using Api.Common;
using Api.Spotify;
using Npgsql;

namespace Api.Detail;

/// 아티스트 상세 (NON-13). 공개(비로그인 열람). Spotify 카탈로그 + 우리 커뮤니티 평가.
/// 앱 토큰(Client Credentials) 제약(실측): 아티스트 객체에 followers·popularity·genres 없음,
/// top-tracks는 403 차단. → 헤더(이름·이미지·링크) + 앨범(디스코그래피) 중심으로 구성.
/// 아티스트 종합점수는 만들지 않음(스탠 전쟁 방지) — 릴리스별 기존 평가만 노출.
public static class ArtistEndpoints
{
    public static void MapArtistEndpoints(this WebApplication app, string? dbConnString)
    {
        app.MapGet("/artist/{id}", async (string id, SpotifyClient spotify, CancellationToken ct) =>
        {
            if (!spotify.Configured) return ApiResults.ServiceUnavailable("SPOTIFY_NOT_CONFIGURED");

            string? artistJson;
            try { artistJson = await spotify.GetAsync($"artists/{id}", ct); }
            catch (HttpRequestException) { return ApiResults.ServiceUnavailable("SPOTIFY_UNAVAILABLE"); }
            if (artistJson is null) return ApiResults.NotFound("ARTIST_NOT_FOUND");

            var albums = await LoadAlbumsAsync(spotify, id, ct);
            var albumIds = albums.Select(a => a.SpotifyId).ToList();

            // 트랙 평가/리뷰도 반영하려면 수록곡이 필요(top-tracks는 403). 앨범↔트랙 매핑 유지:
            //  - 리뷰 대상명·조회 세트에 트랙 포함
            //  - 앨범 배지는 "레코드 반응"(앨범 자체 + 수록곡 평가 합산)으로 산출
            var albumTracks = await LoadTracksAsync(spotify, albumIds, ct);
            var allTracks = albumTracks.Values.SelectMany(x => x).ToList();
            var allIds = albumIds.Concat(allTracks.Select(x => x.Id)).Distinct().ToArray();

            var nameMap = new Dictionary<string, string>();
            foreach (var a in albums) nameMap[a.SpotifyId] = a.Name;
            foreach (var (tid, tname) in allTracks) nameMap[tid] = tname;

            var badges = await LoadBadgesAsync(dbConnString, allIds, ct);
            var recentReviews = (await LoadRecentReviewsAsync(dbConnString, allIds, ct))
                .Select(r => r with { TargetName = nameMap.GetValueOrDefault(r.TargetSpotifyId, "") })
                .ToList();

            // 앨범 레코드 점수: {앨범 id} ∪ {수록곡 id}의 평가를 가중평균(avg*count 합 / count 합).
            RatingBadge? RecordRating(string albumId)
            {
                double sum = 0;
                var count = 0;
                if (badges.TryGetValue(albumId, out var ab)) { sum += ab.Average * ab.Count; count += ab.Count; }
                if (albumTracks.TryGetValue(albumId, out var ts))
                    foreach (var (tid, _) in ts)
                        if (badges.TryGetValue(tid, out var tb)) { sum += tb.Average * tb.Count; count += tb.Count; }
                return count > 0 ? new RatingBadge(Math.Round(sum / count, 2), count) : null;
            }

            var ratedAlbums = albums.Select(a => a with { Rating = RecordRating(a.SpotifyId) }).ToList();
            var totalRatings = badges.Values.Sum(b => b.Count);
            var ratedCount = ratedAlbums.Count(a => a.Rating is not null);

            // 평가된 트랙 목록(점수순). 이미지·이름은 앨범 맥락에서 채움.
            var albumImage = albums.ToDictionary(a => a.SpotifyId, a => a.ImageUrl);
            var seenTrack = new HashSet<string>();
            var ratedTracks = new List<ArtistRatedTrack>();
            foreach (var (albumId, ts) in albumTracks)
                foreach (var (tid, tname) in ts)
                    if (seenTrack.Add(tid) && badges.TryGetValue(tid, out var tb))
                        ratedTracks.Add(new ArtistRatedTrack(tid, tname, albumImage.GetValueOrDefault(albumId), tb));
            ratedTracks = ratedTracks
                .OrderByDescending(t => t.Rating.Average).ThenByDescending(t => t.Rating.Count).ToList();

            using var doc = JsonDocument.Parse(artistJson);
            var root = doc.RootElement;

            var detail = new ArtistDetail(
                Str(root, "id") ?? id,
                Str(root, "name") ?? "",
                FirstImage(root),
                root.TryGetProperty("external_urls", out var e) ? Str(e, "spotify") ?? "" : "",
                ratedCount,
                totalRatings,
                ratedTracks,
                ratedAlbums,
                recentReviews);

            return ApiResults.Ok("OK", detail);
        });
    }

    private static async Task<List<T>> SafeParse<T>(
        SpotifyClient spotify, string path, Func<JsonElement, List<T>> parse, CancellationToken ct)
    {
        try
        {
            var json = await spotify.GetAsync(path, ct);
            if (json is null) return [];
            using var doc = JsonDocument.Parse(json);
            return parse(doc.RootElement);
        }
        catch (HttpRequestException) { return []; }
    }

    // 디스코그래피. 앱 토큰은 이 엔드포인트의 limit이 10으로 제한됨(실측) + market과 조합 시 400 →
    // market 없이 limit=10 + offset 페이지네이션으로 최대 50개 수집.
    private static async Task<List<ArtistAlbum>> LoadAlbumsAsync(SpotifyClient spotify, string id, CancellationToken ct)
    {
        // 앱 토큰은 페이지 크기가 요청 limit보다 작게 오기도 함 → offset을 "실제 받은 개수"만큼
        // 전진시켜 구멍을 막고, 빈 페이지에서 종료. 최대 5콜로 지연을 제한.
        var all = new List<ArtistAlbum>();
        var offset = 0;
        for (var page = 0; page < 5 && offset < 50; page++)
        {
            var items = await SafeParse(spotify,
                $"artists/{id}/albums?include_groups=album,single&limit=10&offset={offset}", ParseAlbums, ct);
            if (items.Count == 0) break;
            all.AddRange(items);
            offset += items.Count;
        }
        // 같은 이름의 지역/버전 중복 제거(대소문자 무시), 발매일 내림차순.
        var seen = new HashSet<string>();
        var deduped = new List<ArtistAlbum>();
        foreach (var a in all)
            if (seen.Add(a.Name.ToLowerInvariant())) deduped.Add(a);
        return deduped.OrderByDescending(a => a.ReleaseDate).ToList();
    }

    private static List<ArtistAlbum> ParseAlbums(JsonElement root)
    {
        var list = new List<ArtistAlbum>();
        if (!root.TryGetProperty("items", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var it in arr.EnumerateArray())
            list.Add(new ArtistAlbum(
                Str(it, "id") ?? "", Str(it, "name") ?? "", FirstImage(it),
                Str(it, "release_date"), Str(it, "album_type") ?? "album", null));
        return list;
    }

    // 앨범별 수록곡(id·이름) 수집. 앨범당 1콜, 병렬. 동시성 상한(과도한 호출·레이트리밋 방지)으로 앨범 수 제한.
    private static async Task<Dictionary<string, List<(string Id, string Name)>>> LoadTracksAsync(
        SpotifyClient spotify, IReadOnlyList<string> albumIds, CancellationToken ct)
    {
        var target = albumIds.Take(30).ToList();
        var tasks = target.Select(aid => SafeParse(spotify, $"albums/{aid}/tracks?limit=50", ParseTracks, ct));
        var results = await Task.WhenAll(tasks);
        var map = new Dictionary<string, List<(string Id, string Name)>>();
        for (var i = 0; i < target.Count; i++) map[target[i]] = results[i];
        return map;
    }

    private static List<(string Id, string Name)> ParseTracks(JsonElement root)
    {
        var list = new List<(string, string)>();
        if (!root.TryGetProperty("items", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var it in arr.EnumerateArray())
            if (Str(it, "id") is { } tid) list.Add((tid, Str(it, "name") ?? ""));
        return list;
    }

    // spotify_id → (평균, 개수). 구 평점(target_spotify_id null)은 매칭 안 됨(허용).
    private static async Task<Dictionary<string, RatingBadge>> LoadBadgesAsync(
        string? dbConnString, string[] ids, CancellationToken ct)
    {
        var map = new Dictionary<string, RatingBadge>();
        if (dbConnString is null || ids.Length == 0) return map;
        try
        {
            await using var conn = new NpgsqlConnection(dbConnString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                """
                select target_spotify_id, round(avg(score), 2)::float8, count(*)
                from public.ratings
                where target_spotify_id = any(@ids)
                group by target_spotify_id
                """, conn);
            cmd.Parameters.AddWithValue("ids", ids);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                map[r.GetString(0)] = new RatingBadge(r.GetDouble(1), (int)r.GetInt64(2));
        }
        catch (NpgsqlException) { /* 평가 배지는 없어도 페이지는 살린다 */ }
        return map;
    }

    // 이 아티스트 릴리스에 달린 최근 리뷰(본문 있는 것). 이미 모은 spotify_id 세트로 조회.
    private static async Task<List<ArtistReview>> LoadRecentReviewsAsync(
        string? dbConnString, string[] ids, CancellationToken ct)
    {
        var list = new List<ArtistReview>();
        if (dbConnString is null || ids.Length == 0) return list;
        try
        {
            await using var conn = new NpgsqlConnection(dbConnString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                """
                select r.target_type, r.target_spotify_id, u.username, u.avatar_url, r.score, r.review, r.created_at
                from public.ratings r
                join public.users u on u.id = r.user_id
                where r.target_spotify_id = any(@ids) and r.review is not null and r.review <> ''
                order by r.created_at desc
                limit 10
                """, conn);
            cmd.Parameters.AddWithValue("ids", ids);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new ArtistReview(
                    r.GetString(0), r.GetString(1), r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3), r.GetDecimal(4), r.GetString(5),
                    r.GetFieldValue<DateTimeOffset>(6), ""));
        }
        catch (NpgsqlException) { /* 리뷰 피드는 없어도 페이지는 살린다 */ }
        return list;
    }

    private static string? FirstImage(JsonElement root) =>
        root.TryGetProperty("images", out var imgs)
        && imgs.ValueKind == JsonValueKind.Array && imgs.GetArrayLength() > 0
            ? Str(imgs[0], "url") : null;

    private static string? Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

public sealed record RatingBadge(double Average, int Count);
public sealed record ArtistAlbum(
    string SpotifyId, string Name, string? ImageUrl, string? ReleaseDate, string AlbumType, RatingBadge? Rating);
public sealed record ArtistRatedTrack(string SpotifyId, string Name, string? ImageUrl, RatingBadge Rating);
public sealed record ArtistReview(
    string TargetType, string TargetSpotifyId, string Username, string? AvatarUrl,
    decimal Score, string Body, DateTimeOffset CreatedAt, string TargetName);
public sealed record ArtistDetail(
    string SpotifyId, string Name, string? ImageUrl, string SpotifyUrl,
    int RatedCount, int TotalRatings,
    IReadOnlyList<ArtistRatedTrack> RatedTracks,
    IReadOnlyList<ArtistAlbum> Albums, IReadOnlyList<ArtistReview> RecentReviews);
