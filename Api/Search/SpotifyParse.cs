using System.Text.Json;

namespace Api.Search;

/// Spotify 검색 응답(JSON) → DTO 파싱. HTTP 와 분리해 순수 함수로 두어 테스트 가능하게 한다.
/// 단일 타입 응답 기준(root.{tracks|albums|artists}.{items,total}).
public static class SpotifyParse
{
    public static (List<TrackResult> Items, int Total) Tracks(JsonElement root)
    {
        var list = new List<TrackResult>();
        var total = 0;
        if (root.TryGetProperty("tracks", out var t))
        {
            total = TotalOf(t);
            foreach (var it in ItemsOf(t))
            {
                string? albumId = null, albumName = null, image = null;
                if (it.TryGetProperty("album", out var al))
                {
                    albumId = Str(al, "id");
                    albumName = Str(al, "name");
                    image = FirstImage(al);
                }
                list.Add(new TrackResult(
                    Str(it, "id") ?? "", Str(it, "name") ?? "", FirstArtist(it), albumId, albumName, image, Isrc(it)));
            }
        }
        return (list, total);
    }

    public static (List<AlbumResult> Items, int Total) Albums(JsonElement root)
    {
        var list = new List<AlbumResult>();
        var total = 0;
        if (root.TryGetProperty("albums", out var a))
        {
            total = TotalOf(a);
            foreach (var it in ItemsOf(a))
            {
                list.Add(new AlbumResult(
                    Str(it, "id") ?? "", Str(it, "name") ?? "", FirstArtist(it), FirstImage(it), Str(it, "release_date")));
            }
        }
        return (list, total);
    }

    public static (List<ArtistResult> Items, int Total) Artists(JsonElement root)
    {
        var list = new List<ArtistResult>();
        var total = 0;
        if (root.TryGetProperty("artists", out var ar))
        {
            total = TotalOf(ar);
            foreach (var it in ItemsOf(ar))
            {
                list.Add(new ArtistResult(Str(it, "id") ?? "", Str(it, "name") ?? "", FirstImage(it)));
            }
        }
        return (list, total);
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
