using Npgsql;

namespace Api.Common;

/// 대상(track/album)별 운영자 지정 외부 링크(NON-154). 지금은 YouTube. 상세·오늘의 픽 공용 조회.
/// 테이블(0063) 없으면 null 로 degrade — 링크 없음으로 취급.
public static class TargetLinks
{
    public static async Task<string?> YoutubeAsync(NpgsqlConnection conn, string type, string spotifyId, CancellationToken ct)
    {
        try
        {
            await using var cmd = new NpgsqlCommand(
                "select youtube_url from public.target_youtube_links where target_type = @t and target_spotify_id = @id", conn);
            cmd.Parameters.AddWithValue("t", type);
            cmd.Parameters.AddWithValue("id", spotifyId);
            return await cmd.ExecuteScalarAsync(ct) as string;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return null; // 마이그레이션 0063 전
        }
    }

    public static async Task<string?> YoutubeAsync(string? dbConnString, string type, string spotifyId, CancellationToken ct)
    {
        if (dbConnString is null) return null;
        try
        {
            await using var conn = new NpgsqlConnection(dbConnString);
            await conn.OpenAsync(ct);
            return await YoutubeAsync(conn, type, spotifyId, ct);
        }
        catch (NpgsqlException) { return null; }
    }
}
