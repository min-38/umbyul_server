using System.Text.Json;
using Api.Common;
using Api.Spotify;
using Npgsql;

namespace Api.Search;

/// 통합 검색 (비로그인 공개). 타입별로 따로 호출(offset 페이징) 후 합친다. 파싱은 SpotifyParse.
/// 평점/리뷰수는 NON-7/8 이후. 캐싱/식별은 NON-6(상세)에서 컴플라이언트하게.
public static class SearchEndpoints
{
    // 이 Spotify 앱은 limit>10 이면 "Invalid limit"(개발모드 쿼터 추정) → 10 고정. offset 페이징은 정상.
    private const int PageSize = 10;

    public static void MapSearchEndpoints(this WebApplication app, string? dbConnString)
    {
        // 초기 검색: 각 카테고리 첫 페이지 + total (병렬)
        app.MapGet("/search", async (string? q, SpotifyClient spotify, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q)) return ApiResults.BadRequest("MISSING_QUERY");

            var tracks = TracksAsync(spotify, dbConnString, q, 0, ct);
            var albums = AlbumsAsync(spotify, dbConnString, q, 0, ct);
            var artists = ArtistsAsync(spotify, q, 0, ct);
            var users = UsersAsync(dbConnString, q, 0, ct);
            await Task.WhenAll(tracks, albums, artists, users);

            return ApiResults.Ok("OK", new SearchResults(
                await tracks, await albums, await artists, await users));
        });

        // 더 보기: 단일 카테고리의 다음 페이지
        app.MapGet("/search/more", async (string? q, string? type, int? offset, SpotifyClient spotify, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q)) return ApiResults.BadRequest("MISSING_QUERY");
            var off = offset ?? 0;
            return type switch
            {
                "track" => ApiResults.Ok("OK", await TracksAsync(spotify, dbConnString, q, off, ct)),
                "album" => ApiResults.Ok("OK", await AlbumsAsync(spotify, dbConnString, q, off, ct)),
                "artist" => ApiResults.Ok("OK", await ArtistsAsync(spotify, q, off, ct)),
                "user" => ApiResults.Ok("OK", await UsersAsync(dbConnString, q, off, ct)),
                _ => ApiResults.BadRequest("INVALID_TYPE"),
            };
        });
    }

    private static async Task<CategoryResult<TrackResult>> TracksAsync(SpotifyClient spotify, string? dbConnString, string q, int offset, CancellationToken ct)
    {
        if (!spotify.Configured) return new CategoryResult<TrackResult>([], 0);
        try
        {
            var json = await spotify.SearchAsync(q, "track", PageSize, offset, ct);
            using var doc = JsonDocument.Parse(json);
            var (items, total) = SpotifyParse.Tracks(doc.RootElement);
            // 평점 집계(NON-8): 상세와 동일하게 ISRC(target_id) 기준. ISRC 없는 항목은 집계 생략.
            var keys = items.Where(t => !string.IsNullOrEmpty(t.Isrc)).Select(t => t.Isrc!).Distinct().ToArray();
            var ratings = await LoadRatingsAsync(dbConnString, "track", "target_id", keys, ct);
            var rated = ratings.Count == 0 ? items
                : items.Select(t => t.Isrc is { } i && ratings.TryGetValue(i, out var rs) ? t with { Rating = rs } : t).ToList();
            return new CategoryResult<TrackResult>(rated, total);
        }
        catch (HttpRequestException)
        {
            return new CategoryResult<TrackResult>([], 0);
        }
    }

    private static async Task<CategoryResult<AlbumResult>> AlbumsAsync(SpotifyClient spotify, string? dbConnString, string q, int offset, CancellationToken ct)
    {
        if (!spotify.Configured) return new CategoryResult<AlbumResult>([], 0);
        try
        {
            var json = await spotify.SearchAsync(q, "album", PageSize, offset, ct);
            using var doc = JsonDocument.Parse(json);
            var (items, total) = SpotifyParse.Albums(doc.RootElement);
            // 검색 앨범엔 UPC가 없어 spotify_id 기준 집계(NON-8). 상세(UPC)와 드물게 다를 수 있음.
            var keys = items.Select(a => a.Id).ToArray();
            var ratings = await LoadRatingsAsync(dbConnString, "album", "target_spotify_id", keys, ct);
            var rated = ratings.Count == 0 ? items
                : items.Select(a => ratings.TryGetValue(a.Id, out var rs) ? a with { Rating = rs } : a).ToList();
            return new CategoryResult<AlbumResult>(rated, total);
        }
        catch (HttpRequestException)
        {
            return new CategoryResult<AlbumResult>([], 0);
        }
    }

    // 검색 결과 평점 배치 집계(NON-8). keyColumn = "target_id"(ISRC/UPC) | "target_spotify_id" (검증된 리터럴).
    private static async Task<Dictionary<string, Api.Detail.RatingSummary>> LoadRatingsAsync(
        string? conn, string targetType, string keyColumn, string[] keys, CancellationToken ct)
    {
        var map = new Dictionary<string, Api.Detail.RatingSummary>();
        if (string.IsNullOrEmpty(conn) || keys.Length == 0) return map;
        try
        {
            await using var c = new NpgsqlConnection(conn);
            await c.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                $"select {keyColumn}, round(avg(score), 2)::float8, count(*) from public.ratings " +
                $"where target_type = @tt and {keyColumn} = any(@keys) and deleted_at is null group by {keyColumn}", c);
            cmd.Parameters.AddWithValue("tt", targetType);
            cmd.Parameters.AddWithValue("keys", keys);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                map[r.GetString(0)] = new Api.Detail.RatingSummary(r.GetDouble(1), (int)r.GetInt64(2));
            return map;
        }
        catch (NpgsqlException) { return map; }
    }

    private static async Task<CategoryResult<ArtistResult>> ArtistsAsync(SpotifyClient spotify, string q, int offset, CancellationToken ct)
    {
        if (!spotify.Configured) return new CategoryResult<ArtistResult>([], 0);
        try
        {
            var json = await spotify.SearchAsync(q, "artist", PageSize, offset, ct);
            using var doc = JsonDocument.Parse(json);
            var (items, total) = SpotifyParse.Artists(doc.RootElement);
            return new CategoryResult<ArtistResult>(items, total);
        }
        catch (HttpRequestException)
        {
            return new CategoryResult<ArtistResult>([], 0);
        }
    }

    private static async Task<CategoryResult<UserResult>> UsersAsync(string? conn, string q, int offset, CancellationToken ct)
    {
        var items = new List<UserResult>();
        if (string.IsNullOrEmpty(conn)) return new CategoryResult<UserResult>(items, 0);

        await using var c = new NpgsqlConnection(conn);
        await c.OpenAsync(ct);

        int total;
        await using (var countCmd = new NpgsqlCommand("select count(*) from public.users where username ilike @q", c))
        {
            countCmd.Parameters.AddWithValue("q", "%" + q + "%");
            total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct));
        }

        await using var cmd = new NpgsqlCommand(
            "select id, username, avatar_url from public.users where username ilike @q order by username limit @lim offset @off", c);
        cmd.Parameters.AddWithValue("q", "%" + q + "%");
        cmd.Parameters.AddWithValue("lim", PageSize);
        cmd.Parameters.AddWithValue("off", offset);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            items.Add(new UserResult(r.GetGuid(0).ToString(), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
        }
        return new CategoryResult<UserResult>(items, total);
    }
}
