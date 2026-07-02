using Npgsql;

namespace Admin.Data;

/// 관리자용 DB 접근(Supabase Postgres, service_role → BYPASSRLS). Api와 동일한 DATABASE:* 설정 사용.
public sealed class AdminDb(IConfiguration config)
{
    private readonly string? _conn = BuildConnString(config);
    public bool Configured => _conn is not null;

    private static string? BuildConnString(IConfiguration config)
    {
        var db = config.GetSection("DATABASE");
        if (string.IsNullOrEmpty(db["HOST"]) || string.IsNullOrEmpty(db["PASSWORD"])) return null;
        return new NpgsqlConnectionStringBuilder
        {
            Host = db["HOST"],
            Port = int.TryParse(db["PORT"], out var p) ? p : 5432,
            Database = string.IsNullOrEmpty(db["DATABASE"]) ? "postgres" : db["DATABASE"],
            Username = string.IsNullOrEmpty(db["USER"]) ? "postgres" : db["USER"],
            Password = db["PASSWORD"],
            SslMode = SslMode.Require,
        }.ConnectionString;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_conn);
        await conn.OpenAsync(ct);
        return conn;
    }

    // ── 신고 ──
    public async Task<List<ReportRow>> GetReportsAsync(string? status, CancellationToken ct = default)
    {
        if (!Configured) return [];
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            select rep.id, ru.username, rep.target_type, rep.target_id, rep.reason, rep.detail, rep.status, rep.created_at,
                   rat.target_name, rat.target_artist, rat.review, rau.username, tu.username
            from public.reports rep
            join public.users ru on ru.id = rep.reporter_id
            left join public.ratings rat on rep.target_type = 'rating' and rat.id = rep.target_id::uuid
            left join public.users rau on rau.id = rat.user_id
            left join public.users tu on rep.target_type = 'user' and tu.id = rep.target_id::uuid
            where (@status = '' or rep.status = @status)
            order by rep.created_at desc
            limit 200
            """, conn);
        cmd.Parameters.AddWithValue("status", status ?? "");

        var list = new List<ReportRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var targetType = r.GetString(2);
            string? title, sub, body;
            if (targetType == "rating")
            {
                title = r.IsDBNull(8) ? "(삭제된 리뷰)" : r.GetString(8);
                var artist = r.IsDBNull(9) ? null : r.GetString(9);
                var author = r.IsDBNull(11) ? null : r.GetString(11);
                sub = string.Join(" · ", new[] { artist, author is null ? null : $"by {author}" }.Where(x => x is not null));
                body = r.IsDBNull(10) ? null : r.GetString(10);
            }
            else // user
            {
                title = r.IsDBNull(12) ? "(알 수 없는 유저)" : $"@{r.GetString(12)}";
                sub = null;
                body = null;
            }
            list.Add(new ReportRow(
                r.GetGuid(0), r.GetString(1), targetType, r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6), r.GetFieldValue<DateTimeOffset>(7),
                title, sub, body));
        }
        return list;
    }

    public async Task SetReportStatusAsync(Guid id, string status, CancellationToken ct = default)
    {
        if (!Configured) return;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("update public.reports set status = @s where id = @id", conn);
        cmd.Parameters.AddWithValue("s", status);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// 신고된 리뷰 삭제 + 해당 신고 resolved 처리.
    public async Task DeleteRatingAndResolveAsync(Guid reportId, string ratingId, CancellationToken ct = default)
    {
        if (!Configured || !Guid.TryParse(ratingId, out var rid)) return;
        await using var conn = await OpenAsync(ct);
        await using (var del = new NpgsqlCommand("delete from public.ratings where id = @rid", conn))
        {
            del.Parameters.AddWithValue("rid", rid);
            await del.ExecuteNonQueryAsync(ct);
        }
        await using var upd = new NpgsqlCommand("update public.reports set status = 'resolved' where id = @id", conn);
        upd.Parameters.AddWithValue("id", reportId);
        await upd.ExecuteNonQueryAsync(ct);
    }

    // ── Spotify 상태 ──
    public async Task<SpotifyStatusRow?> GetSpotifyStatusAsync(CancellationToken ct = default)
    {
        if (!Configured) return null;
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "select blocked_until, retry_after_seconds, updated_at from public.spotify_status where id = 1", conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            return new SpotifyStatusRow(
                r.IsDBNull(0) ? null : r.GetFieldValue<DateTimeOffset>(0),
                r.IsDBNull(1) ? null : r.GetInt32(1),
                r.IsDBNull(2) ? null : r.GetFieldValue<DateTimeOffset>(2));
        }
        catch (NpgsqlException) { return null; } // 마이그레이션 전 등
    }

    public async Task<(long Count, DateTimeOffset? Latest)> GetCacheStatsAsync(CancellationToken ct = default)
    {
        if (!Configured) return (0, null);
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("select count(*), max(fetched_at) from public.spotify_cache", conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            return (r.GetInt64(0), r.IsDBNull(1) ? null : r.GetFieldValue<DateTimeOffset>(1));
        }
        catch (NpgsqlException) { return (0, null); }
    }
}

public sealed record ReportRow(
    Guid Id, string Reporter, string TargetType, string TargetId, string Reason, string? Detail,
    string Status, DateTimeOffset CreatedAt, string? Title, string? Sub, string? Body);

public sealed record SpotifyStatusRow(DateTimeOffset? BlockedUntil, int? RetryAfterSeconds, DateTimeOffset? UpdatedAt);
