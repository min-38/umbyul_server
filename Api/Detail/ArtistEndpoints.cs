using System.Text.Json;
using Api.Common;
using Api.Spotify;
using Npgsql;

namespace Api.Detail;

/// 아티스트 상세 (NON-13). 공개(비로그인 열람). Spotify 라이브 조회 + 우리 커뮤니티 평가 배지.
/// 아티스트 종합점수는 만들지 않음(스탠 전쟁 방지) — 릴리스별로 이미 존재하는 평가만 노출.
/// 장르는 앱 토큰으로 안 내려와 제외(실측). 공연·인증 배지는 범위 밖(후속).
public static class ArtistEndpoints
{
    private const string Market = "US"; // top-tracks는 market 필수. 글로벌 기본값.

    public static void MapArtistEndpoints(this WebApplication app, string? dbConnString)
    {
        app.MapGet("/artist/{id}", async (string id, SpotifyClient spotify, CancellationToken ct) =>
        {
            if (!spotify.Configured) return ApiResults.ServiceUnavailable("SPOTIFY_NOT_CONFIGURED");

            string? artistJson;
            try { artistJson = await spotify.GetAsync($"artists/{id}", ct); }
            catch (HttpRequestException) { return ApiResults.ServiceUnavailable("SPOTIFY_UNAVAILABLE"); }
            if (artistJson is null) return ApiResults.NotFound("ARTIST_NOT_FOUND");

            // 인기 곡 / 디스코그래피는 실패해도 헤더는 살린다.
            var topTracks = await SafeParse(spotify, $"artists/{id}/top-tracks?market={Market}", ParseTopTracks, ct);
            var albums = await SafeParse(spotify,
                $"artists/{id}/albums?include_groups=album,single&market={Market}&limit=50", ParseAlbums, ct);

            // 등장하는 모든 spotify_id를 모아 우리 평가를 한 번에 조회 → 평가 있는 항목에만 배지.
            var ids = topTracks.Select(t => t.SpotifyId).Concat(albums.Select(a => a.SpotifyId)).ToArray();
            var badges = await LoadBadgesAsync(dbConnString, ids, ct);
            var recentReviews = await LoadRecentReviewsAsync(dbConnString, ids, ct);

            using var doc = JsonDocument.Parse(artistJson);
            var root = doc.RootElement;

            var detail = new ArtistDetail(
                Str(root, "id") ?? id,
                Str(root, "name") ?? "",
                FirstImage(root),
                root.TryGetProperty("followers", out var f) ? Int(f, "total") : 0,
                Int(root, "popularity"),
                root.TryGetProperty("external_urls", out var e) ? Str(e, "spotify") ?? "" : "",
                topTracks.Select(t => t with { Rating = badges.GetValueOrDefault(t.SpotifyId) }).ToList(),
                albums.Select(a => a with { Rating = badges.GetValueOrDefault(a.SpotifyId) }).ToList(),
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

    private static List<ArtistTrack> ParseTopTracks(JsonElement root)
    {
        var list = new List<ArtistTrack>();
        if (!root.TryGetProperty("tracks", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var it in arr.EnumerateArray())
        {
            string? img = it.TryGetProperty("album", out var al) ? FirstImage(al) : null;
            list.Add(new ArtistTrack(Str(it, "id") ?? "", Str(it, "name") ?? "", img, null));
        }
        return list;
    }

    private static List<ArtistAlbum> ParseAlbums(JsonElement root)
    {
        var list = new List<ArtistAlbum>();
        if (!root.TryGetProperty("items", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;

        // 같은 이름의 지역/버전 중복 제거(대소문자 무시), 발매일 내림차순.
        var seen = new HashSet<string>();
        foreach (var it in arr.EnumerateArray())
        {
            var name = Str(it, "name") ?? "";
            if (!seen.Add(name.ToLowerInvariant())) continue;
            list.Add(new ArtistAlbum(
                Str(it, "id") ?? "", name, FirstImage(it),
                Str(it, "release_date"), Str(it, "album_type") ?? "album", null));
        }
        return list.OrderByDescending(a => a.ReleaseDate).ToList();
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
                    r.GetFieldValue<DateTimeOffset>(6)));
        }
        catch (NpgsqlException) { /* 리뷰 피드는 없어도 페이지는 살린다 */ }
        return list;
    }

    private static string? FirstImage(JsonElement root) =>
        root.TryGetProperty("images", out var imgs)
        && imgs.ValueKind == JsonValueKind.Array && imgs.GetArrayLength() > 0
            ? Str(imgs[0], "url") : null;

    private static int Int(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static string? Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

public sealed record RatingBadge(double Average, int Count);
public sealed record ArtistTrack(string SpotifyId, string Name, string? ImageUrl, RatingBadge? Rating);
public sealed record ArtistAlbum(
    string SpotifyId, string Name, string? ImageUrl, string? ReleaseDate, string AlbumType, RatingBadge? Rating);
public sealed record ArtistReview(
    string TargetType, string TargetSpotifyId, string Username, string? AvatarUrl,
    decimal Score, string Body, DateTimeOffset CreatedAt);
public sealed record ArtistDetail(
    string SpotifyId, string Name, string? ImageUrl, int Followers, int Popularity, string SpotifyUrl,
    IReadOnlyList<ArtistTrack> TopTracks, IReadOnlyList<ArtistAlbum> Albums,
    IReadOnlyList<ArtistReview> RecentReviews);
