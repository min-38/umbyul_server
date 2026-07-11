using Npgsql;

namespace Api.Maintenance;

/// 보존 기한이 있는 개인정보성 행을 주기적으로 파기(9차 QA 프라이버시 감사). 부팅 직후 1회 + 이후 24h마다.
/// 각 항목은 독립 best-effort — 한 테이블 실패(미존재·순단)가 나머지 파기를 막지 않는다.
/// SpotifyCachePurgeService와 같은 패턴. 무기한 보존 대신 자동 파기로 수동 SQL 의존을 없앤다.
public sealed class RetentionPurgeService(string dbConnString) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    // (라벨, SQL). age 컷오프는 SQL 내 interval로. 운영자 결정 보존기간을 여기 한곳에 모은다.
    private static readonly (string Label, string Sql)[] Purges =
    [
        // QA9-1: 익명 공지 조회 dedup 행(ip:)은 90일 후 파기 — 무기한 연결성 제거.
        // 로그인 u: 행은 계정 데이터라 유지(계정삭제 시 cascade).
        ("announcement_views ip:",
            "delete from public.announcement_views where viewer like 'ip:%' and created_at < now() - interval '90 days'"),
        // QA9-2: 관리자 로그인 실패 로그(login.failed, IP는 해시 저장)는 90일 후 파기.
        ("admin_actions login.failed",
            "delete from public.admin_actions where action = 'login.failed' and created_at < now() - interval '90 days'"),
        // QA9-5: 처리 완료된 문의(이메일+자유텍스트, user_id 링크 없어 계정삭제 cascade 밖)는 1년 후 파기.
        ("inquiries handled",
            "delete from public.inquiries where handled and handled_at < now() - interval '1 year'"),
        // NON-52: Api 시스템 로그는 30일 후 파기 — 무한 증가 방지.
        ("app_logs",
            "delete from public.app_logs where created_at < now() - interval '30 days'"),
    ];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var (_, sql) in Purges)
            {
                try
                {
                    await using var conn = new NpgsqlConnection(dbConnString);
                    await conn.OpenAsync(ct);
                    await using var cmd = new NpgsqlCommand(sql, conn);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                catch (Exception) { /* 테이블 미존재·순단 등 — 다음 주기 재시도 */ }
            }

            try { await Task.Delay(Interval, ct); }
            catch (TaskCanceledException) { break; }
        }
    }
}
