using Npgsql;

namespace Admin.Data;

// 곡/앨범별 YouTube 링크(NON-154). 음악 고유 ID(target_id = ISRC/UPC ?? spotify_id)에 귀속 — ratings와 동일 키라
// 같은 곡의 다른 에디션에도 링크가 뜬다. 운영자는 spotify_id로 입력 → 우리 ratings.target_id 로 해석해 저장.
public sealed partial class AdminDb
{
    public async Task<List<YoutubeLinkAdminRow>> ListYoutubeLinksAsync(CancellationToken ct = default)
    {
        if (!Configured) return [];
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "select target_type, target_id, youtube_url, updated_at from public.target_youtube_links order by updated_at desc", conn);
        var list = new List<YoutubeLinkAdminRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new YoutubeLinkAdminRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3)));
        return list;
    }

    // idInput: 신규는 spotify_id(운영자 입력), 편집은 이미 target_id. ratings로 해석해 target_id 키로 upsert.
    public async Task<(bool Ok, string? Error)> SaveYoutubeLinkAsync(string targetType, string idInput, string url, Actor actor, CancellationToken ct = default)
    {
        targetType = targetType?.Trim() ?? "";
        idInput = idInput?.Trim() ?? "";
        url = url?.Trim() ?? "";
        if (targetType is not ("track" or "album")) return (false, "대상 종류가 올바르지 않습니다.");
        if (idInput.Length == 0) return (false, "Spotify ID를 입력하세요.");
        if (url.Length == 0) return (false, "YouTube URL을 입력하세요.");
        if (!Configured) return (false, "DB_NOT_CONFIGURED");
        await using var conn = await OpenAsync(ct);

        var targetId = await ResolveTargetIdAsync(conn, targetType, idInput, ct);
        await using var cmd = new NpgsqlCommand(
            """
            insert into public.target_youtube_links (target_type, target_id, youtube_url, updated_at)
            values (@t, @id, @u, now())
            on conflict (target_type, target_id) do update set youtube_url = excluded.youtube_url, updated_at = now()
            """, conn);
        cmd.Parameters.AddWithValue("t", targetType);
        cmd.Parameters.AddWithValue("id", targetId);
        cmd.Parameters.AddWithValue("u", url);
        await cmd.ExecuteNonQueryAsync(ct);
        await LogAsync(conn, actor, "youtubelink.save", $"{targetType}:{targetId}", url, ct);
        return (true, null);
    }

    // 오늘의 픽 편집 프리필용(NON-265): spotify_id(또는 target_id)로 저장된 YouTube 링크 조회. 없으면 null.
    public async Task<string?> GetYoutubeUrlForTargetAsync(string targetType, string idInput, CancellationToken ct = default)
    {
        if (!Configured) return null;
        targetType = targetType?.Trim() ?? "";
        idInput = idInput?.Trim() ?? "";
        if (targetType is not ("track" or "album") || idInput.Length == 0) return null;
        await using var conn = await OpenAsync(ct);
        var targetId = await ResolveTargetIdAsync(conn, targetType, idInput, ct);
        await using var cmd = new NpgsqlCommand(
            "select youtube_url from public.target_youtube_links where target_type = @t and target_id = @id", conn);
        cmd.Parameters.AddWithValue("t", targetType);
        cmd.Parameters.AddWithValue("id", targetId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task<(bool Ok, string? Error)> DeleteYoutubeLinkAsync(string targetType, string targetId, Actor actor, CancellationToken ct = default)
    {
        if (!Configured) return (false, "DB_NOT_CONFIGURED");
        await using var conn = await OpenAsync(ct);
        await using var del = new NpgsqlCommand(
            "delete from public.target_youtube_links where target_type = @t and target_id = @id", conn);
        del.Parameters.AddWithValue("t", targetType);
        del.Parameters.AddWithValue("id", targetId);
        if (await del.ExecuteNonQueryAsync(ct) == 0) return (false, "NOT_FOUND");
        await LogAsync(conn, actor, "youtubelink.delete", $"{targetType}:{targetId}", null, ct);
        return (true, null);
    }

    // spotify_id → 음악 고유 ID(ISRC/UPC). 우리 ratings.target_id(리뷰 시 저장됨)로 해석.
    // 이미 target_id거나(편집) 아직 리뷰 안 된 곡이면 입력값 그대로(폴백).
    private static async Task<string> ResolveTargetIdAsync(NpgsqlConnection conn, string targetType, string idInput, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "select target_id from public.ratings where target_type = @t and target_spotify_id = @s and deleted_at is null limit 1", conn);
        cmd.Parameters.AddWithValue("t", targetType);
        cmd.Parameters.AddWithValue("s", idInput);
        return await cmd.ExecuteScalarAsync(ct) as string ?? idInput;
    }
}

public sealed record YoutubeLinkAdminRow(string TargetType, string TargetId, string YoutubeUrl, DateTimeOffset UpdatedAt);
