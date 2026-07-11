using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Api.Logging;

/// app_logs 싱크(NON-52). DbLoggerProvider가 만든 로거들이 여기로 enqueue → 배치 insert.
///  - 게이트: app_log_config.min_level을 15초 TTL로 캐싱해, 로그마다 DB를 치지 않고 레벨을 필터.
///  - bounded 채널(만차 시 새 로그 드롭) + 주기 배치 insert(COPY)로 핫패스 영향 최소화.
///  - 리프레시/insert 실패는 조용히 스킵 — 로깅이 서비스에 영향을 주지 않게(로깅은 부가 기능).
public sealed class DbLogSink(string connString) : BackgroundService
{
    private static readonly TimeSpan ConfigTtl = TimeSpan.FromSeconds(15);
    private const int BatchMax = 500;

    private readonly Channel<AppLogEntry> _channel = Channel.CreateBounded<AppLogEntry>(
        new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropWrite, // 만차 시 새 로그 드롭(서비스 우선)
            SingleReader = true,
        });

    // 캐시된 최소 레벨(기본 Warning). volatile — DbLogger의 락-프리 읽기와 리프레시 쓰기가 안전하게.
    private volatile int _minLevel = (int)LogLevel.Warning;

    /// 게이트 — 캐시된 min_level 이상만 통과. None은 항상 거부.
    public bool ShouldLog(LogLevel level) => level != LogLevel.None && (int)level >= _minLevel;

    public void Enqueue(AppLogEntry entry) => _channel.Writer.TryWrite(entry);

    /// 중요 비즈니스 이벤트 기록(NON-52 / NON-249) — 개발자가 '중요'로 명시한 것이라 min_level 게이트를
    /// 우회해 항상 기록한다(min_level은 프레임워크·부가 로그의 노이즈 바닥만 제어). 채널 만차면 드롭.
    public void LogEvent(LogLevel level, string category, string message, Exception? ex = null) =>
        Enqueue(new AppLogEntry(level, message, ex?.ToString(), category, 0, DateTimeOffset.UtcNow));

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var lastConfig = DateTimeOffset.MinValue;
        var buffer = new List<AppLogEntry>(BatchMax);

        while (!ct.IsCancellationRequested)
        {
            // 로그가 오길 최대 15초 대기 — 타임아웃이면 설정만 리프레시하고 다시 대기(정적 기간에도 게이트 최신 유지).
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(ConfigTtl);
            try { await _channel.Reader.WaitToReadAsync(wait.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* 15초 타임아웃 */ }
            catch (OperationCanceledException) { break; }

            if (DateTimeOffset.UtcNow - lastConfig >= ConfigTtl)
            {
                await RefreshConfigAsync(ct);
                lastConfig = DateTimeOffset.UtcNow;
            }

            buffer.Clear();
            while (buffer.Count < BatchMax && _channel.Reader.TryRead(out var e)) buffer.Add(e);
            if (buffer.Count > 0) await FlushAsync(buffer, ct);
        }
    }

    // app_log_config.min_level → 캐시. 테이블 미존재(마이그레이션 0080 전)·순단이면 기존 캐시 유지(기본 Warning).
    private async Task RefreshConfigAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = new NpgsqlConnection(connString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("select min_level from public.app_log_config where id = 1", conn);
            if (await cmd.ExecuteScalarAsync(ct) is string s && Enum.TryParse<LogLevel>(s, ignoreCase: true, out var lvl))
                _minLevel = (int)lvl;
        }
        catch (Exception) { /* 유지 */ }
    }

    // 배치 COPY insert. 실패(테이블 미존재·순단 등)는 조용히 드롭.
    private async Task FlushAsync(List<AppLogEntry> batch, CancellationToken ct)
    {
        try
        {
            await using var conn = new NpgsqlConnection(connString);
            await conn.OpenAsync(ct);
            await using var w = await conn.BeginBinaryImportAsync(
                "copy public.app_logs (level, message, exception, category, event_id, created_at) from stdin (format binary)", ct);
            foreach (var e in batch)
            {
                await w.StartRowAsync(ct);
                await w.WriteAsync(e.Level.ToString(), NpgsqlDbType.Text, ct);
                await w.WriteAsync(e.Message, NpgsqlDbType.Text, ct);
                if (e.Exception is null) await w.WriteNullAsync(ct);
                else await w.WriteAsync(e.Exception, NpgsqlDbType.Text, ct);
                await w.WriteAsync(e.Category, NpgsqlDbType.Text, ct);
                await w.WriteAsync(e.EventId, NpgsqlDbType.Integer, ct);
                await w.WriteAsync(e.At, NpgsqlDbType.TimestampTz, ct);
            }
            await w.CompleteAsync(ct);
        }
        catch (Exception) { /* 드롭 */ }
    }
}

/// 싱크로 넘기는 로그 1건.
public sealed record AppLogEntry(LogLevel Level, string Message, string? Exception, string Category, int EventId, DateTimeOffset At);
