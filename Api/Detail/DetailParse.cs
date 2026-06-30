using System.Text.Json;

namespace Api.Detail;

/// Spotify track/album JSON → 상세 모델 파싱. JsonElement만 받는 순수 함수라 단위 테스트 가능.
/// 트랙의 레이블(copyrights)은 앨범 객체에 있으므로 AlbumId를 follow-up용으로 같이 돌려준다.
public static class DetailParse
{
    public sealed record ParsedTrack(
        string SpotifyId, string Name, string SpotifyUrl,
        IReadOnlyList<ArtistRef> Artists, AlbumRef? Album,
        string? Isrc, int DurationMs, string? ReleaseDate, string? AlbumId);

    public sealed record ParsedAlbum(
        string SpotifyId, string Name, string SpotifyUrl,
        IReadOnlyList<ArtistRef> Artists, string? ImageUrl,
        string? Upc, string? ReleaseDate, string? Copyright,
        int TotalTracks, IReadOnlyList<TrackRef> Tracks);

    public static ParsedTrack Track(JsonElement root)
    {
        string? isrc = root.TryGetProperty("external_ids", out var ext) ? Str(ext, "isrc") : null;

        AlbumRef? album = null;
        string? albumId = null;
        string? releaseDate = null;
        if (root.TryGetProperty("album", out var al) && al.ValueKind == JsonValueKind.Object)
        {
            albumId = Str(al, "id");
            album = new AlbumRef(albumId ?? "", Str(al, "name") ?? "", FirstImage(al));
            releaseDate = Str(al, "release_date");
        }

        return new ParsedTrack(
            Str(root, "id") ?? "",
            Str(root, "name") ?? "",
            SpotifyUrl(root),
            Artists(root),
            album,
            isrc,
            Int(root, "duration_ms"),
            releaseDate,
            albumId);
    }

    public static ParsedAlbum Album(JsonElement root)
    {
        string? upc = root.TryGetProperty("external_ids", out var ext) ? Str(ext, "upc") : null;

        var tracks = new List<TrackRef>();
        if (root.TryGetProperty("tracks", out var tr)
            && tr.TryGetProperty("items", out var items)
            && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in items.EnumerateArray())
                tracks.Add(new TrackRef(
                    Str(it, "id") ?? "", Str(it, "name") ?? "",
                    Int(it, "duration_ms"), Int(it, "track_number")));
        }

        return new ParsedAlbum(
            Str(root, "id") ?? "",
            Str(root, "name") ?? "",
            SpotifyUrl(root),
            Artists(root),
            FirstImage(root),
            upc,
            Str(root, "release_date"),
            Copyright(root),
            Int(root, "total_tracks"),
            tracks);
    }

    /// 앨범 copyrights → 레이블/저작권 텍스트. ℗(P, 음반저작권=보통 레이블) 우선, 없으면 ©(C).
    public static string? Copyright(JsonElement albumRoot)
    {
        if (!albumRoot.TryGetProperty("copyrights", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;

        string? c = null, p = null;
        foreach (var it in arr.EnumerateArray())
        {
            var text = Str(it, "text");
            if (string.IsNullOrEmpty(text)) continue;
            if (Str(it, "type") == "P") p ??= text;
            else c ??= text;
        }
        return p ?? c;
    }

    private static IReadOnlyList<ArtistRef> Artists(JsonElement root)
    {
        var list = new List<ArtistRef>();
        if (root.TryGetProperty("artists", out var a) && a.ValueKind == JsonValueKind.Array)
            foreach (var ar in a.EnumerateArray())
                list.Add(new ArtistRef(Str(ar, "id") ?? "", Str(ar, "name") ?? ""));
        return list;
    }

    private static string? FirstImage(JsonElement root) =>
        root.TryGetProperty("images", out var imgs)
        && imgs.ValueKind == JsonValueKind.Array
        && imgs.GetArrayLength() > 0
            ? Str(imgs[0], "url")
            : null;

    private static string SpotifyUrl(JsonElement root) =>
        root.TryGetProperty("external_urls", out var e) ? Str(e, "spotify") ?? "" : "";

    private static int Int(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static string? Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
