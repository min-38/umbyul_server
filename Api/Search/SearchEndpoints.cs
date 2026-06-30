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
        if (!spotify.Configured) return new CategoryResult<TrackResult>([], 0);
        try
        {
            var json = await spotify.SearchAsync(q, "track", PageSize, offset, ct);
            using var doc = JsonDocument.Parse(json);
            var (items, total) = SpotifyParse.Tracks(doc.RootElement);
            return new CategoryResult<TrackResult>(items, total);
        }
        catch (HttpRequestException)
        {
            return new CategoryResult<TrackResult>([], 0);
        }
    }

    private static async Task<CategoryResult<AlbumResult>> AlbumsAsync(SpotifyClient spotify, string q, int offset, CancellationToken ct)
    {
        if (!spotify.Configured) return new CategoryResult<AlbumResult>([], 0);
        try
        {
            var json = await spotify.SearchAsync(q, "album", PageSize, offset, ct);
            using var doc = JsonDocument.Parse(json);
            var (items, total) = SpotifyParse.Albums(doc.RootElement);
            return new CategoryResult<AlbumResult>(items, total);
        }
        catch (HttpRequestException)
        {
            return new CategoryResult<AlbumResult>([], 0);
        }
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
