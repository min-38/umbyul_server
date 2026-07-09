using Npgsql;

namespace Admin.Data;

// 곡/앨범별 YouTube 링크(NON-154). 링크가 있으면 상세 페이지·오늘의 픽 어디서든 YouTube 아이콘 노출.
// 대상(target_type, target_spotify_id)이 PK — 대상당 링크 1개. upsert 저장.
public sealed partial class AdminDb
{
    public async Task<List<YoutubeLinkAdminRow>> ListYoutubeLinksAsync(CancellationToken ct = default)
    {
        if (!Configured) return [];
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "select target_type, target_spotify_id, youtube_url, updated_at from public.target_youtube_links order by updated_at desc", conn);
        var list = new List<YoutubeLinkAdminRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new YoutubeLinkAdminRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3)));
        return list;
    }

    public async Task<(bool Ok, string? Error)> SaveYoutubeLinkAsync(string targetType, string spotifyId, string url, Actor actor, CancellationToken ct = default)
    {
        targetType = targetType?.Trim() ?? "";
        spotifyId = spotifyId?.Trim() ?? "";
        url = url?.Trim() ?? "";
        if (targetType is not ("track" or "album")) return (false, "대상 종류가 올바르지 않습니다.");
        if (spotifyId.Length == 0) return (false, "Spotify ID를 입력하세요.");
        if (url.Length == 0) return (false, "YouTube URL을 입력하세요.");
        if (!Configured) return (false, "DB_NOT_CONFIGURED");
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            insert into public.target_youtube_links (target_type, target_spotify_id, youtube_url, updated_at)
            values (@t, @s, @u, now())
            on conflict (target_type, target_spotify_id) do update set youtube_url = excluded.youtube_url, updated_at = now()
            """, conn);
        cmd.Parameters.AddWithValue("t", targetType);
        cmd.Parameters.AddWithValue("s", spotifyId);
        cmd.Parameters.AddWithValue("u", url);
        await cmd.ExecuteNonQueryAsync(ct);
        await LogAsync(conn, actor, "youtubelink.save", $"{targetType}:{spotifyId}", url, ct);
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> DeleteYoutubeLinkAsync(string targetType, string spotifyId, Actor actor, CancellationToken ct = default)
    {
        if (!Configured) return (false, "DB_NOT_CONFIGURED");
        await using var conn = await OpenAsync(ct);
        await using var del = new NpgsqlCommand(
            "delete from public.target_youtube_links where target_type = @t and target_spotify_id = @s", conn);
        del.Parameters.AddWithValue("t", targetType);
        del.Parameters.AddWithValue("s", spotifyId);
        if (await del.ExecuteNonQueryAsync(ct) == 0) return (false, "NOT_FOUND");
        await LogAsync(conn, actor, "youtubelink.delete", $"{targetType}:{spotifyId}", null, ct);
        return (true, null);
    }
}

public sealed record YoutubeLinkAdminRow(string TargetType, string SpotifyId, string YoutubeUrl, DateTimeOffset UpdatedAt);
