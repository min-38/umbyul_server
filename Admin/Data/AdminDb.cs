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
                   rat.id, rat.target_name, rat.target_artist, rat.review, rau.username, tu.username, rat.deleted_at, rat.user_id
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
            var targetDeleted = false;
            Guid? offenderId = null; // 제재 대상(리뷰 작성자 또는 신고된 유저)
            string? offenderName = null;
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
                    targetDeleted = !r.IsDBNull(14); // rat.deleted_at — 이미 소프트 삭제됨
                    offenderId = r.IsDBNull(15) ? null : r.GetGuid(15);
                    offenderName = author;
                }
            }
            else // user
            {
                offenderName = r.IsDBNull(13) ? null : r.GetString(13);
                title = offenderName is null ? "(알 수 없는 유저)" : $"@{offenderName}";
                offenderId = Guid.TryParse(r.GetString(3), out var ug) ? ug : null;
                sub = null;
                body = null;
            }
            list.Add(new ReportRow(
                r.GetGuid(0), r.GetString(1), targetType, r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6), r.GetFieldValue<DateTimeOffset>(7),
                title, sub, body, targetDeleted, offenderId, offenderName));
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

    /// 신고된 리뷰 소프트 삭제(deleted_at) + 해당 신고 resolved 처리.
    /// 하드 삭제 대신 흔적을 남겨 복구·감사·신고화면 표시가 가능. 이미 삭제된 건 건드리지 않음.
    public async Task DeleteRatingAndResolveAsync(Guid reportId, string ratingId, Actor actor, CancellationToken ct = default)
    {
        if (!Configured || !Guid.TryParse(ratingId, out var rid)) return;
        await using var conn = await OpenAsync(ct);
        await using (var del = new NpgsqlCommand(
            """
            update public.ratings
            set deleted_at = now(), deleted_by = @by, deleted_reason = @reason
            where id = @rid and deleted_at is null
            """, conn))
        {
            del.Parameters.AddWithValue("rid", rid);
            del.Parameters.AddWithValue("by", (object?)actor.Id ?? DBNull.Value);
            del.Parameters.AddWithValue("reason", $"report:{reportId}");
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

    // ── 유저 모더레이션(NON-47) ──
    public async Task<List<UserRow>> ListUsersAsync(string? search, CancellationToken ct = default)
    {
        if (!Configured) return [];
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            select u.id, u.username, u.avatar_url, u.created_at, u.suspended_until, u.banned,
                   (select count(*) from public.reports r
                      where (r.target_type = 'user' and r.target_id = u.id::text)
                         or (r.target_type = 'rating' and r.target_id in
                             (select ra.id::text from public.ratings ra where ra.user_id = u.id))) as reports
            from public.users u
            where @q = '' or u.username ilike '%' || @q || '%'
            order by (u.banned or u.suspended_until > now()) desc, u.created_at desc
            limit 100
            """, conn);
        cmd.Parameters.AddWithValue("q", search ?? "");
        var list = new List<UserRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new UserRow(
                r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                r.GetFieldValue<DateTimeOffset>(3),
                r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4),
                r.GetBoolean(5), (int)r.GetInt64(6)));
        return list;
    }

    public async Task<List<SanctionRow>> GetUserSanctionsAsync(Guid userId, CancellationToken ct = default)
    {
        if (!Configured) return [];
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "select type, until, reason, admin_username, created_at from public.user_sanctions where user_id = @uid order by created_at desc limit 50", conn);
        cmd.Parameters.AddWithValue("uid", userId);
        var list = new List<SanctionRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new SanctionRow(r.GetString(0),
                r.IsDBNull(1) ? null : r.GetFieldValue<DateTimeOffset>(1),
                r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3),
                r.GetFieldValue<DateTimeOffset>(4)));
        return list;
    }

    public Task WarnUserAsync(Guid userId, string? reason, Actor actor, Guid? reportId = null, CancellationToken ct = default)
        => ApplySanctionAsync(userId, "warning", null, reason, actor, reportId, ct);

    public Task SuspendUserAsync(Guid userId, DateTimeOffset until, string? reason, Actor actor, Guid? reportId = null, CancellationToken ct = default)
        => ApplySanctionAsync(userId, "suspension", until, reason, actor, reportId, ct);

    public Task BanUserAsync(Guid userId, string? reason, Actor actor, Guid? reportId = null, CancellationToken ct = default)
        => ApplySanctionAsync(userId, "ban", null, reason, actor, reportId, ct);

    public Task UnbanUserAsync(Guid userId, Actor actor, Guid? reportId = null, CancellationToken ct = default)
        => ApplySanctionAsync(userId, "unban", null, null, actor, reportId, ct);

    // 제재 1건 = 이력(user_sanctions) 기록 + 집행 상태(users) 갱신 + 감사 로그(admin_actions).
    private async Task ApplySanctionAsync(Guid userId, string type, DateTimeOffset? until, string? reason, Actor actor, Guid? reportId, CancellationToken ct)
    {
        if (!Configured) return;
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var ins = new NpgsqlCommand(
            """
            insert into public.user_sanctions (user_id, type, until, reason, admin_id, admin_username, report_id)
            values (@uid, @type, @until, @reason, @aid, @auser, @rid)
            """, conn, tx))
        {
            ins.Parameters.AddWithValue("uid", userId);
            ins.Parameters.AddWithValue("type", type);
            ins.Parameters.AddWithValue("until", (object?)until ?? DBNull.Value);
            ins.Parameters.AddWithValue("reason", (object?)reason ?? DBNull.Value);
            ins.Parameters.AddWithValue("aid", (object?)actor.Id ?? DBNull.Value);
            ins.Parameters.AddWithValue("auser", actor.Username);
            ins.Parameters.AddWithValue("rid", (object?)reportId ?? DBNull.Value);
            await ins.ExecuteNonQueryAsync(ct);
        }

        // 집행 상태 갱신(warning 은 상태 변화 없음).
        var setSql = type switch
        {
            "suspension" => "update public.users set suspended_until = @until, banned = false where id = @uid",
            "ban" => "update public.users set banned = true where id = @uid",
            "unban" => "update public.users set banned = false, suspended_until = null where id = @uid",
            _ => null,
        };
        if (setSql is not null)
        {
            await using var upd = new NpgsqlCommand(setSql, conn, tx);
            upd.Parameters.AddWithValue("uid", userId);
            if (type == "suspension") upd.Parameters.AddWithValue("until", (object?)until ?? DBNull.Value);
            await upd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);

        var detail = until is { } u ? $"until {u:u}" : reason;
        await LogAsync(conn, actor, $"user.{type}", userId.ToString(),
            reportId is { } rp ? $"report {rp}; {detail}" : detail, ct);
    }
}

public sealed record ReportRow(
    Guid Id, string Reporter, string TargetType, string TargetId, string Reason, string? Detail,
    string Status, DateTimeOffset CreatedAt, string? Title, string? Sub, string? Body, bool TargetDeleted,
    Guid? OffenderId, string? OffenderName);

public sealed record SpotifyStatusRow(DateTimeOffset? BlockedUntil, int? RetryAfterSeconds, DateTimeOffset? UpdatedAt);

/// 조치를 수행한 관리자(감사 로그용).
public readonly record struct Actor(Guid? Id, string Username);
public sealed record AdminRow(Guid Id, string Username, DateTimeOffset CreatedAt);
public sealed record AdminActionRow(string AdminUsername, string Action, string? Target, string? Detail, DateTimeOffset CreatedAt);

/// 유저 관리 목록 행: 현재 상태 + 신고 누적 수.
public sealed record UserRow(Guid Id, string Username, string? AvatarUrl, DateTimeOffset CreatedAt,
    DateTimeOffset? SuspendedUntil, bool Banned, int ReportCount);
public sealed record SanctionRow(string Type, DateTimeOffset? Until, string? Reason, string AdminUsername, DateTimeOffset CreatedAt);
