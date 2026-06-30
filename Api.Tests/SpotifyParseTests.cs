using System.Text.Json;
using Api.Search;

namespace Api.Tests;

public class SpotifyParseTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Tracks_extracts_fields_and_total()
    {
        var root = Parse("""
        {"tracks":{"total":42,"items":[
          {"id":"t1","name":"Song A","artists":[{"name":"Artist X"},{"name":"Y"}],
           "album":{"id":"al1","name":"Album Z","images":[{"url":"http://img/1"}]},"external_ids":{"isrc":"KRA401600005"}},
          {"id":"t2","name":"Song B","artists":[],"album":{"name":"Album Y","images":[]}}
        ]}}
        """);

        var (items, total) = SpotifyParse.Tracks(root);

        Assert.Equal(42, total);
        Assert.Equal(2, items.Count);

        Assert.Equal("t1", items[0].Id);
        Assert.Equal("Song A", items[0].Name);
        Assert.Equal("Artist X", items[0].Artist); // 첫 아티스트만
        Assert.Equal("al1", items[0].AlbumId);
        Assert.Equal("Album Z", items[0].AlbumName);
        Assert.Equal("http://img/1", items[0].ImageUrl);
        Assert.Equal("KRA401600005", items[0].Isrc);

        // 누락 필드 안전 처리
        Assert.Equal("", items[1].Artist); // artists 빈 배열
        Assert.Null(items[1].AlbumId); // album.id 없음
        Assert.Null(items[1].ImageUrl); // images 빈 배열
        Assert.Null(items[1].Isrc); // external_ids 없음
    }

    [Fact]
    public void Albums_extracts_fields_and_total()
    {
        var root = Parse("""
        {"albums":{"total":7,"items":[
          {"id":"a1","name":"My Album","artists":[{"name":"The Band"}],
           "images":[{"url":"http://cover/1"}],"release_date":"2020-05-01"}
        ]}}
        """);

        var (items, total) = SpotifyParse.Albums(root);

        Assert.Equal(7, total);
        Assert.Single(items);
        Assert.Equal("My Album", items[0].Name);
        Assert.Equal("The Band", items[0].Artist);
        Assert.Equal("http://cover/1", items[0].ImageUrl);
        Assert.Equal("2020-05-01", items[0].ReleaseDate);
    }

    [Fact]
    public void Artists_extracts_fields_and_total()
    {
        var root = Parse("""
        {"artists":{"total":3,"items":[
          {"id":"ar1","name":"Soloist","images":[{"url":"http://face/1"}]},
          {"id":"ar2","name":"NoPhoto","images":[]}
        ]}}
        """);

        var (items, total) = SpotifyParse.Artists(root);

        Assert.Equal(3, total);
        Assert.Equal(2, items.Count);
        Assert.Equal("Soloist", items[0].Name);
        Assert.Equal("http://face/1", items[0].ImageUrl);
        Assert.Null(items[1].ImageUrl);
    }

    [Fact]
    public void Missing_category_returns_empty()
    {
        var root = Parse("{}");
        var (tracks, tTotal) = SpotifyParse.Tracks(root);
        Assert.Empty(tracks);
        Assert.Equal(0, tTotal);

        var (albums, _) = SpotifyParse.Albums(root);
        Assert.Empty(albums);
    }

    [Fact]
    public void Missing_items_array_returns_empty()
    {
        // 카테고리는 있는데 items 없음
        var root = Parse("""{"tracks":{"total":0}}""");
        var (items, total) = SpotifyParse.Tracks(root);
        Assert.Empty(items);
        Assert.Equal(0, total);
    }
}
