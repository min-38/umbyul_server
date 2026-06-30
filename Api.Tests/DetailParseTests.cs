using System.Text.Json;
using Api.Detail;

namespace Api.Tests;

public class DetailParseTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Track_extracts_core_fields_and_album_followup_id()
    {
        var root = Parse("""
        {"id":"t1","name":"Mean","duration_ms":238000,
         "external_ids":{"isrc":"USCJY1100123"},
         "external_urls":{"spotify":"https://open.spotify.com/track/t1"},
         "artists":[{"id":"ar1","name":"Taylor Swift"},{"id":"ar2","name":"X"}],
         "album":{"id":"al1","name":"Speak Now","release_date":"2023-07-07",
                  "images":[{"url":"http://img/lg"},{"url":"http://img/sm"}]}}
        """);

        var t = DetailParse.Track(root);

        Assert.Equal("t1", t.SpotifyId);
        Assert.Equal("Mean", t.Name);
        Assert.Equal("https://open.spotify.com/track/t1", t.SpotifyUrl);
        Assert.Equal(238000, t.DurationMs);
        Assert.Equal("USCJY1100123", t.Isrc);
        Assert.Equal(2, t.Artists.Count);
        Assert.Equal("Taylor Swift", t.Artists[0].Name);
        Assert.NotNull(t.Album);
        Assert.Equal("al1", t.Album!.Id);
        Assert.Equal("Speak Now", t.Album.Name);
        Assert.Equal("http://img/lg", t.Album.ImageUrl); // 첫 이미지
        Assert.Equal("al1", t.AlbumId); // copyright follow-up용
        Assert.Equal("2023-07-07", t.ReleaseDate);
    }

    [Fact]
    public void Track_handles_missing_fields_safely()
    {
        var root = Parse("""{"id":"t9","name":"Bare","artists":[]}""");

        var t = DetailParse.Track(root);

        Assert.Equal("t9", t.SpotifyId);
        Assert.Null(t.Isrc);
        Assert.Equal(0, t.DurationMs);
        Assert.Empty(t.Artists);
        Assert.Null(t.Album);
        Assert.Null(t.AlbumId);
        Assert.Equal("", t.SpotifyUrl);
    }

    [Fact]
    public void Album_extracts_fields_upc_copyright_and_tracklist()
    {
        var root = Parse("""
        {"id":"al1","name":"전설","release_date":"2025-01-01","total_tracks":2,
         "external_ids":{"upc":"190296000000"},
         "external_urls":{"spotify":"https://open.spotify.com/album/al1"},
         "artists":[{"id":"ar1","name":"잔나비"}],
         "images":[{"url":"http://cover/lg"}],
         "copyrights":[{"text":"© 2025 ABC","type":"C"},{"text":"℗ 2025 Republic","type":"P"}],
         "tracks":{"items":[
            {"id":"tk1","name":"Intro","duration_ms":216000,"track_number":1},
            {"id":"tk2","name":"신나는 잠","duration_ms":222000,"track_number":2}
         ]}}
        """);

        var a = DetailParse.Album(root);

        Assert.Equal("al1", a.SpotifyId);
        Assert.Equal("전설", a.Name);
        Assert.Equal("190296000000", a.Upc);
        Assert.Equal("℗ 2025 Republic", a.Copyright); // P(℗) 우선
        Assert.Equal("2025-01-01", a.ReleaseDate);
        Assert.Equal("http://cover/lg", a.ImageUrl);
        Assert.Equal("잔나비", a.Artists[0].Name);
        Assert.Equal(2, a.TotalTracks);
        Assert.Equal(2, a.Tracks.Count);
        Assert.Equal("tk1", a.Tracks[0].Id);
        Assert.Equal("Intro", a.Tracks[0].Name);
        Assert.Equal(216000, a.Tracks[0].DurationMs);
        Assert.Equal(1, a.Tracks[0].TrackNumber);
        Assert.Equal("신나는 잠", a.Tracks[1].Name);
    }

    [Fact]
    public void Album_handles_missing_tracks_and_upc()
    {
        var root = Parse("""{"id":"al9","name":"Empty","artists":[]}""");

        var a = DetailParse.Album(root);

        Assert.Null(a.Upc);
        Assert.Null(a.Copyright);
        Assert.Empty(a.Tracks);
        Assert.Equal(0, a.TotalTracks);
    }

    [Fact]
    public void Copyright_prefers_phonogram_then_falls_back_to_c()
    {
        // P 없으면 C 사용
        Assert.Equal("© 2020 Only C",
            DetailParse.Copyright(Parse("""{"copyrights":[{"text":"© 2020 Only C","type":"C"}]}""")));
        // copyrights 없으면 null
        Assert.Null(DetailParse.Copyright(Parse("""{"id":"al1"}""")));
    }
}
