using System.Text.Json;
using Api.Common;
using Api.Spotify;
using Npgsql;

namespace Api.Search;

/// 통합 검색 (비로그인 공개). 타입별로 따로 호출(각 최대 50, offset 페이징) 후 합친다.
/// 평점/리뷰수는 NON-7/8 이후. 캐싱은 항목이 실제 참조될 때(NON-6/7).
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

            var tracks = TracksAsync(spotify, q, 0, ct);
            var albums = AlbumsAsync(spotify, q, 0, ct);
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
                "track" => ApiResults.Ok("OK", await TracksAsync(spotify, q, off, ct)),
                "album" => ApiResults.Ok("OK", await AlbumsAsync(spotify, q, off, ct)),
                "artist" => ApiResults.Ok("OK", await ArtistsAsync(spotify, q, off, ct)),
                "user" => ApiResults.Ok("OK", await UsersAsync(dbConnString, q, off, ct)),
                _ => ApiResults.BadRequest("INVALID_TYPE"),
            };
        });
    }

    private static async Task<CategoryResult<TrackResult>> TracksAsync(SpotifyClient spotify, string q, int offset, CancellationToken ct)
    {
        var items = new List<TrackResult>();
        var total = 0;
        if (spotify.Configured)
        {
            try
            {
                var json = await spotify.SearchAsync(q, "track", PageSize, offset, ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("tracks", out var t))
                {
                    total = TotalOf(t);
                    foreach (var it in ItemsOf(t))
                    {
                        string? albumName = null, image = null;
                        if (it.TryGetProperty("album", out var al))
                        {
                            albumName = Str(al, "name");
                            image = FirstImage(al);
                        }
                        items.Add(new TrackResult(
                            Str(it, "id") ?? "", Str(it, "name") ?? "", FirstArtist(it), albumName, image, Isrc(it)));
                    }
                }
            }
            catch (HttpRequestException) { }
        }
        return new CategoryResult<TrackResult>(items, total);
    }

    private static async Task<CategoryResult<AlbumResult>> AlbumsAsync(SpotifyClient spotify, string q, int offset, CancellationToken ct)
    {
        var items = new List<AlbumResult>();
        var total = 0;
        if (spotify.Configured)
        {
            try
            {
                var json = await spotify.SearchAsync(q, "album", PageSize, offset, ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("albums", out var a))
                {
                    total = TotalOf(a);
                    foreach (var it in ItemsOf(a))
                    {
                        items.Add(new AlbumResult(
                            Str(it, "id") ?? "", Str(it, "name") ?? "", FirstArtist(it), FirstImage(it), Str(it, "release_date")));
                    }
                }
            }
            catch (HttpRequestException) { }
        }
        return new CategoryResult<AlbumResult>(items, total);
    }

    private static async Task<CategoryResult<ArtistResult>> ArtistsAsync(SpotifyClient spotify, string q, int offset, CancellationToken ct)
    {
        var items = new List<ArtistResult>();
        var total = 0;
        if (spotify.Configured)
        {
            try
            {
                var json = await spotify.SearchAsync(q, "artist", PageSize, offset, ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("artists", out var ar))
                {
                    total = TotalOf(ar);
                    foreach (var it in ItemsOf(ar))
                    {
                        items.Add(new ArtistResult(Str(it, "id") ?? "", Str(it, "name") ?? "", FirstImage(it)));
                    }
                }
            }
            catch (HttpRequestException) { }
        }
        return new CategoryResult<ArtistResult>(items, total);
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

    private static int TotalOf(JsonElement category) =>
        category.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0;

    private static IEnumerable<JsonElement> ItemsOf(JsonElement category) =>
        category.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray()
            : [];

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string FirstArtist(JsonElement e) =>
        e.TryGetProperty("artists", out var a) && a.ValueKind == JsonValueKind.Array && a.GetArrayLength() > 0
            ? Str(a[0], "name") ?? ""
            : "";

    private static string? FirstImage(JsonElement e) =>
        e.TryGetProperty("images", out var img) && img.ValueKind == JsonValueKind.Array && img.GetArrayLength() > 0
            ? Str(img[0], "url")
            : null;

    private static string? Isrc(JsonElement e) =>
        e.TryGetProperty("external_ids", out var x) && x.TryGetProperty("isrc", out var i) &&
        i.ValueKind == JsonValueKind.String
            ? i.GetString()
            : null;
}
