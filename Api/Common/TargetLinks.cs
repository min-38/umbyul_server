using Npgsql;

namespace Api.Common;

/// 대상(track/album)별 운영자 지정 외부 링크(NON-154). 지금은 YouTube. 상세·오늘의 픽 공용 조회.
/// 테이블(0063) 없으면 null 로 degrade — 링크 없음으로 취급.
public static class TargetLinks
{
    // targetId = ISRC(track)/UPC(album), 없으면 spotify_id 폴백 (ratings.target_id와 동일 규약).
    public static async Task<string?> YoutubeAsync(NpgsqlConnection conn, string type, string targetId, string? spotifyId, CancellationToken ct)
    {
        try
        {
            // target_id(ISRC/UPC) 우선, 없으면 spotify_id 로도 매칭 — Admin이 리뷰 없는 곡을 spotify_id로만
            // 링크했을 때도 카드/상세에 뜨게(NON-267). 리뷰된 곡은 target_id 로 정확 매칭 유지.
            var ids = !string.IsNullOrEmpty(spotifyId) && spotifyId != targetId
                ? new[] { targetId, spotifyId! }
                : new[] { targetId };
            await using var cmd = new NpgsqlCommand(
                "select youtube_url from public.target_youtube_links where target_type = @t and target_id = any(@ids) order by (target_id = @primary) desc limit 1", conn);
            cmd.Parameters.AddWithValue("t", type);
            cmd.Parameters.AddWithValue("ids", ids);
            cmd.Parameters.AddWithValue("primary", targetId);
            return await cmd.ExecuteScalarAsync(ct) as string;
        }
        catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn)
        {
            return null; // 마이그레이션 0063 전(또는 구 스키마) — 링크 없음으로 취급
        }
    }

    public static async Task<string?> YoutubeAsync(string? dbConnString, string type, string targetId, string? spotifyId, CancellationToken ct)
    {
        if (dbConnString is null) return null;
        try
        {
            await using var conn = new NpgsqlConnection(dbConnString);
            await conn.OpenAsync(ct);
            return await YoutubeAsync(conn, type, targetId, spotifyId, ct);
        }
        catch (NpgsqlException) { return null; }
    }
}
