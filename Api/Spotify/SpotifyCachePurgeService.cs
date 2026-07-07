using Npgsql;

namespace Api.Spotify;

/// spotify_cache 의 오래된 행을 주기적으로 삭제(LEG-7 / DB-16).
/// 읽기 TTL(1h/12h)만 있고 삭제 경로가 없어 테이블이 무한 증가하고, Spotify 정책상 메타를 무기한
/// 보존하지 않아야 한다 → 부팅 직후 1회 + 이후 24h 마다 7일 지난 행을 삭제. 실패는 조용히 스킵.
public sealed class SpotifyCachePurgeService(string dbConnString) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand(
                    "delete from public.spotify_cache where fetched_at < @cutoff", conn);
                cmd.Parameters.AddWithValue("cutoff", DateTimeOffset.UtcNow - MaxAge);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (Exception) { /* 퍼지 실패는 서비스에 영향 없음 */ }

            try { await Task.Delay(Interval, ct); }
            catch (TaskCanceledException) { break; }
        }
    }
}
