using System.Text.Json;
using Api.Common;
using Api.Spotify;
using Npgsql;

namespace Api.Detail;

/// 앨범/곡 상세 (NON-6). 공개(비로그인 열람). Spotify 라이브 조회 + 우리 평점/리뷰.
/// 평점 키 = ISRC(track)/UPC(album), 없으면 spotify_id 폴백.
/// 장르는 앱 토큰으로 안 내려와 제외(실측). 레이블은 앨범 copyrights로 대체.
public static class DetailEndpoints
{
    public static void MapDetailEndpoints(this WebApplication app, string? dbConnString)
    {
        app.MapGet("/detail/track/{id}", async (string id, SpotifyClient spotify, CancellationToken ct) =>
        {
            if (!spotify.Configured) return ApiResults.ServiceUnavailable("SPOTIFY_NOT_CONFIGURED");

            string? json;
            try { json = await spotify.GetAsync($"tracks/{id}", ct); }
            catch (HttpRequestException) { return ApiResults.ServiceUnavailable("SPOTIFY_UNAVAILABLE"); }
            if (json is null) return ApiResults.NotFound("TRACK_NOT_FOUND");

            DetailParse.ParsedTrack t;
            using (var doc = JsonDocument.Parse(json)) t = DetailParse.Track(doc.RootElement);

            // 레이블/저작권은 앨범 객체(copyrights)에 있어 별도 조회. 실패해도 본문은 살린다.
            var copyright = await FetchCopyrightAsync(spotify, t.AlbumId, ct);
            var targetId = t.Isrc ?? t.SpotifyId;
            var (summary, reviews) = await LoadReviewsAsync(dbConnString, "track", targetId, ct);

            return ApiResults.Ok("OK", new TrackDetail(
                t.SpotifyId, t.Name, t.SpotifyUrl, t.Artists, t.Album, t.Isrc, targetId, t.DurationMs,
                t.ReleaseDate, copyright, summary, reviews));
        });

        app.MapGet("/detail/album/{id}", async (string id, SpotifyClient spotify, CancellationToken ct) =>
        {
            if (!spotify.Configured) return ApiResults.ServiceUnavailable("SPOTIFY_NOT_CONFIGURED");

            string? json;
            try { json = await spotify.GetAsync($"albums/{id}", ct); }
            catch (HttpRequestException) { return ApiResults.ServiceUnavailable("SPOTIFY_UNAVAILABLE"); }
            if (json is null) return ApiResults.NotFound("ALBUM_NOT_FOUND");

            DetailParse.ParsedAlbum a;
            using (var doc = JsonDocument.Parse(json)) a = DetailParse.Album(doc.RootElement);

            var targetId = a.Upc ?? a.SpotifyId;
            var (summary, reviews) = await LoadReviewsAsync(dbConnString, "album", targetId, ct);

            return ApiResults.Ok("OK", new AlbumDetail(
                a.SpotifyId, a.Name, a.SpotifyUrl, a.Artists, a.ImageUrl, a.Upc, targetId, a.ReleaseDate, a.Copyright,
                a.TotalTracks, a.Tracks, summary, reviews));
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

    // 우리 DB의 평점/리뷰 + 평균. ratings JOIN users. 아직 NON-7 전이라 보통 비어 있다.
    private static async Task<(RatingSummary, IReadOnlyList<ReviewItem>)> LoadReviewsAsync(
        string? dbConnString, string targetType, string targetId, CancellationToken ct)
    {
        var empty = new RatingSummary(null, 0);
        if (dbConnString is null) return (empty, []);
        try
        {
            await using var conn = new NpgsqlConnection(dbConnString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                """
                select r.id, r.user_id, u.username, u.avatar_url, r.score, r.review, r.created_at
                from public.ratings r
                join public.users u on u.id = r.user_id
                where r.target_type = @tt and r.target_id = @tid
                order by r.created_at desc
                """, conn);
            cmd.Parameters.AddWithValue("tt", targetType);
            cmd.Parameters.AddWithValue("tid", targetId);

            var reviews = new List<ReviewItem>();
            decimal sum = 0;
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                {
                    var score = r.GetDecimal(4);
                    sum += score;
                    reviews.Add(new ReviewItem(
                        r.GetGuid(0).ToString(),
                        r.GetGuid(1).ToString(),
                        r.GetString(2),
                        r.IsDBNull(3) ? null : r.GetString(3),
                        score,
                        r.IsDBNull(5) ? null : r.GetString(5),
                        r.GetFieldValue<DateTimeOffset>(6)));
                }
            }

            var summary = reviews.Count > 0
                ? new RatingSummary(Math.Round((double)(sum / reviews.Count), 2), reviews.Count)
                : empty;
            return (summary, reviews);
        }
        catch (NpgsqlException) { return (empty, []); }
    }
}
