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
                   rat.id, rat.target_name, rat.target_artist, rat.review, rau.username, tu.username
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
                var ratingExists = !r.IsDBNull(8); // rat.id — 존재 여부를 이름 유무와 구분
                if (!ratingExists)
                {
                    title = "(삭제된 리뷰)";
                    sub = null;
                    body = null;
                }
                else
                {
                    title = r.IsDBNull(9) ? "(대상 음악 미상)" : r.GetString(9); // target_name(캐시). 옛 평점은 null 가능
                    var artist = r.IsDBNull(10) ? null : r.GetString(10);
                    var author = r.IsDBNull(12) ? null : r.GetString(12);
                    sub = string.Join(" · ", new[] { artist, author is null ? null : $"by {author}" }.Where(x => x is not null));
                    body = r.IsDBNull(11) ? null : r.GetString(11);
                }
            }
            else // user
            {
                title = r.IsDBNull(13) ? "(알 수 없는 유저)" : $"@{r.GetString(13)}";
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

    public async Task SetReportStatusAsync(Guid id, string status, Actor actor, CancellationToken ct = default)
    {
        if (!Configured) return;
        await using var conn = await OpenAsync(ct);
        await using (var cmd = new NpgsqlCommand("update public.reports set status = @s where id = @id", conn))
        {
            cmd.Parameters.AddWithValue("s", status);
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await LogAsync(conn, actor, $"report.{status}", id.ToString(), null, ct);
    }

    /// 신고된 리뷰 삭제 + 해당 신고 resolved 처리.
    public async Task DeleteRatingAndResolveAsync(Guid reportId, string ratingId, Actor actor, CancellationToken ct = default)
    {
        if (!Configured || !Guid.TryParse(ratingId, out var rid)) return;
        await using var conn = await OpenAsync(ct);
        await using (var del = new NpgsqlCommand("delete from public.ratings where id = @rid", conn))
        {
            del.Parameters.AddWithValue("rid", rid);
            await del.ExecuteNonQueryAsync(ct);
        }
        await using (var upd = new NpgsqlCommand("update public.reports set status = 'resolved' where id = @id", conn))
        {
            upd.Parameters.AddWithValue("id", reportId);
            await upd.ExecuteNonQueryAsync(ct);
        }
        await LogAsync(conn, actor, "rating.delete", ratingId, $"report {reportId}", ct);
    }

    // ── 관리자 계정 ──
    public async Task<(Guid Id, string Username, string Hash)?> GetAdminAuthAsync(string username, CancellationToken ct = default)
    {
        if (!Configured) return null;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "select id, username, password_hash from public.admins where lower(username) = lower(@u)", conn);
        cmd.Parameters.AddWithValue("u", username);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return (r.GetGuid(0), r.GetString(1), r.GetString(2));
    }

    /// 부트스트랩: 해당 username이 없으면 생성(있으면 그대로 둠). 첫 관리자 시딩용.
    /// 마이그레이션(0016) 전이면 조용히 스킵(앱 기동은 막지 않음).
    public async Task EnsureBootstrapAdminAsync(string username, string passwordHash, CancellationToken ct = default)
    {
        if (!Configured) return;
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                """
                insert into public.admins (username, password_hash) values (@u, @h)
                on conflict (lower(username)) do nothing
                """, conn);
            cmd.Parameters.AddWithValue("u", username);
            cmd.Parameters.AddWithValue("h", passwordHash);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (NpgsqlException) { /* 마이그레이션 전 등 — 기동 지속 */ }
    }

    public async Task<List<AdminRow>> ListAdminsAsync(CancellationToken ct = default)
    {
        if (!Configured) return [];
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("select id, username, created_at from public.admins order by created_at", conn);
        var list = new List<AdminRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new AdminRow(r.GetGuid(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2)));
        return list;
    }

    /// 관리자 추가. 중복(username)이면 false.
    public async Task<bool> CreateAdminAsync(string username, string passwordHash, Actor actor, CancellationToken ct = default)
    {
        if (!Configured) return false;
        await using var conn = await OpenAsync(ct);
        try
        {
            await using var cmd = new NpgsqlCommand(
                "insert into public.admins (username, password_hash) values (@u, @h)", conn);
            cmd.Parameters.AddWithValue("u", username);
            cmd.Parameters.AddWithValue("h", passwordHash);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException e) when (e.SqlState == "23505") { return false; }
        await LogAsync(conn, actor, "admin.create", username, null, ct);
        return true;
    }

    // ── 감사 로그 ──
    public async Task LogAsync(Actor actor, string action, string? target, string? detail, CancellationToken ct = default)
    {
        if (!Configured) return;
        await using var conn = await OpenAsync(ct);
        await LogAsync(conn, actor, action, target, detail, ct);
    }

    private static async Task LogAsync(NpgsqlConnection conn, Actor actor, string action, string? target, string? detail, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            insert into public.admin_actions (admin_id, admin_username, action, target, detail)
            values (@id, @u, @a, @t, @d)
            """, conn);
        cmd.Parameters.AddWithValue("id", (object?)actor.Id ?? DBNull.Value);
        cmd.Parameters.AddWithValue("u", actor.Username);
        cmd.Parameters.AddWithValue("a", action);
        cmd.Parameters.AddWithValue("t", (object?)target ?? DBNull.Value);
        cmd.Parameters.AddWithValue("d", (object?)detail ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<AdminActionRow>> RecentActionsAsync(int limit, CancellationToken ct = default)
    {
        if (!Configured) return [];
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "select admin_username, action, target, detail, created_at from public.admin_actions order by created_at desc limit @n", conn);
        cmd.Parameters.AddWithValue("n", limit);
        var list = new List<AdminActionRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new AdminActionRow(r.GetString(0), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.GetFieldValue<DateTimeOffset>(4)));
        return list;
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

/// 조치를 수행한 관리자(감사 로그용).
public readonly record struct Actor(Guid? Id, string Username);
public sealed record AdminRow(Guid Id, string Username, DateTimeOffset CreatedAt);
public sealed record AdminActionRow(string AdminUsername, string Action, string? Target, string? Detail, DateTimeOffset CreatedAt);
