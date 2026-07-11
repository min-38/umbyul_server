using Npgsql;

namespace Admin.Data;

// NON-52: Api 시스템 로그(app_logs) 조회 + DB 저장 최소 레벨(app_log_config) 관리.
public sealed partial class AdminDb
{
    public async Task<List<AppLogRow>> ListAppLogsAsync(
        string? level = null, string? search = null, DateTimeOffset? since = null,
        int offset = 0, int limit = 50, CancellationToken ct = default)
    {
        if (!Configured) return [];
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            select level, message, exception, category, created_at from public.app_logs
            where (@level = '' or level = @level)
              and (@q = '' or message ilike '%' || @q || '%' or coalesce(category, '') ilike '%' || @q || '%')
              and (@since is null or created_at >= @since)
            order by created_at desc limit @lim offset @off
            """, conn);
        cmd.Parameters.AddWithValue("level", level ?? "");
        cmd.Parameters.AddWithValue("q", LikeEscape(search ?? ""));
        cmd.Parameters.Add(new NpgsqlParameter("since", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)since ?? DBNull.Value });
        cmd.Parameters.AddWithValue("lim", limit);
        cmd.Parameters.AddWithValue("off", offset);
        var list = new List<AppLogRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new AppLogRow(
                r.GetString(0), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.GetFieldValue<DateTimeOffset>(4)));
        return list;
    }

    // DB 저장 최소 레벨 조회. 테이블 미존재(마이그레이션 0080 전)면 기본 'Warning'.
    public async Task<string> GetLogMinLevelAsync(CancellationToken ct = default)
    {
        if (!Configured) return "Warning";
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("select min_level from public.app_log_config where id = 1", conn);
            return await cmd.ExecuteScalarAsync(ct) as string ?? "Warning";
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedTable) { return "Warning"; }
    }

    // 최소 레벨 저장(+감사). Api가 15초 TTL로 반영.
    public async Task SetLogMinLevelAsync(string level, Actor actor, CancellationToken ct = default)
    {
        if (!Configured) return;
        await using var conn = await OpenAsync(ct);
        await using (var cmd = new NpgsqlCommand(
            "update public.app_log_config set min_level = @l, updated_at = now() where id = 1", conn))
        {
            cmd.Parameters.AddWithValue("l", level);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await LogAsync(conn, actor, "app_log.min_level", level, null, ct);
    }
}

public sealed record AppLogRow(string Level, string Message, string? Exception, string? Category, DateTimeOffset CreatedAt);
