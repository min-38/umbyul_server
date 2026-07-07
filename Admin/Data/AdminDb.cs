using Npgsql;

namespace Admin.Data;

/// 관리자용 DB 접근(Supabase Postgres, service_role → BYPASSRLS). Api와 동일한 DATABASE:* 설정 사용.
public sealed partial class AdminDb(IConfiguration config)
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

    // ILIKE 와일드카드(%, _)를 리터럴로 이스케이프(기본 escape 문자 '\') — 검색어 특수문자가 패턴으로 동작하는 것 방지(ADM-13).
    internal static string LikeEscape(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    // ── 관리자 계정 ──
    public async Task<(Guid Id, string Username, string Hash, bool IsActive)?> GetAdminAuthAsync(string username, CancellationToken ct = default)
    {
        if (!Configured) return null;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "select id, username, password_hash, is_active from public.admins where lower(username) = lower(@u)", conn);
        cmd.Parameters.AddWithValue("u", username);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return (r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetBoolean(3));
    }

    /// 관리자 활성 여부(세션 재검증용, ADM-1). 행 없음(삭제) = 비활성 취급.
    /// 조회 실패(컬럼 미존재 등)는 true(가용성 우선) — is_active 는 0033 이후 존재.
    public async Task<bool> IsAdminActiveAsync(Guid id, CancellationToken ct = default)
    {
        if (!Configured) return true;
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("select is_active from public.admins where id = @id", conn);
            cmd.Parameters.AddWithValue("id", id);
            return await cmd.ExecuteScalarAsync(ct) is true;
        }
        catch (NpgsqlException) { return true; }
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
        await using var cmd = new NpgsqlCommand("select id, username, created_at, is_active from public.admins order by created_at", conn);
        var list = new List<AdminRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new AdminRow(r.GetGuid(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2), r.GetBoolean(3)));
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

    /// 관리자 활성/비활성 토글(NON-103). 마지막 활성 관리자를 끄는 것은 거부(잠김 방지).
    /// 반환: (성공, 실패 사유코드). 감사 로그 admin.deactivate / admin.activate.
    public async Task<(bool Ok, string? Error)> SetAdminActiveAsync(Guid id, bool active, Actor actor, CancellationToken ct = default)
    {
        if (!Configured) return (false, "DB_NOT_CONFIGURED");
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        if (!active)
        {
            // 대상을 제외한 활성 관리자 행을 잠가 동시 비활성화 경합(TOCTOU) 방지 — 남는 활성이 0이면 거부(ADM-10).
            await using var cntCmd = new NpgsqlCommand(
                "select count(*) from (select 1 from public.admins where is_active and id <> @id for update) x", conn, tx);
            cntCmd.Parameters.AddWithValue("id", id);
            if ((long)(await cntCmd.ExecuteScalarAsync(ct))! < 1) return (false, "LAST_ADMIN");
        }

        await using (var cmd = new NpgsqlCommand("update public.admins set is_active = @a where id = @id", conn, tx))
        {
            cmd.Parameters.AddWithValue("a", active);
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) return (false, "NOT_FOUND");
        }
        await tx.CommitAsync(ct);
        await LogAsync(conn, actor, active ? "admin.activate" : "admin.deactivate", id.ToString(), null, ct);
        return (true, null);
    }

    /// 본인 비밀번호 변경(ADM-4). 현재 비번을 먼저 검증 → 세션 탈취만으로 계정 탈취되는 것 차단.
    /// 반환: false = 현재 비번 불일치(또는 계정 없음). 감사 로그 admin.password_change.
    public async Task<bool> ChangeAdminPasswordAsync(Guid id, string currentPassword, string newHash, Actor actor, CancellationToken ct = default)
    {
        if (!Configured) return false;
        await using var conn = await OpenAsync(ct);
        string currentHash;
        await using (var sel = new NpgsqlCommand("select password_hash from public.admins where id = @id", conn))
        {
            sel.Parameters.AddWithValue("id", id);
            if (await sel.ExecuteScalarAsync(ct) is not string h) return false;
            currentHash = h;
        }
        if (!BCrypt.Net.BCrypt.Verify(currentPassword, currentHash)) return false;
        await using (var cmd = new NpgsqlCommand("update public.admins set password_hash = @h where id = @id", conn))
        {
            cmd.Parameters.AddWithValue("h", newHash);
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await LogAsync(conn, actor, "admin.password_change", id.ToString(), null, ct);
        return true;
    }

    // ── 감사 로그 ──
    public async Task LogAsync(Actor actor, string action, string? target, string? detail, CancellationToken ct = default)
    {
        if (!Configured) return;
        await using var conn = await OpenAsync(ct);
        await LogAsync(conn, actor, action, target, detail, ct);
    }

    // 활성 트랜잭션이 있으면 그 안에서 기록(감사 로그를 본 조치와 원자적으로 — ADM-12). tx=null 이면 단독 커맨드.
    private static async Task LogAsync(NpgsqlConnection conn, Actor actor, string action, string? target, string? detail, CancellationToken ct, NpgsqlTransaction? tx = null)
    {
        await using var cmd = new NpgsqlCommand(
            """
            insert into public.admin_actions (admin_id, admin_username, action, target, detail)
            values (@id, @u, @a, @t, @d)
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", (object?)actor.Id ?? DBNull.Value);
        cmd.Parameters.AddWithValue("u", actor.Username);
        cmd.Parameters.AddWithValue("a", action);
        cmd.Parameters.AddWithValue("t", (object?)target ?? DBNull.Value);
        cmd.Parameters.AddWithValue("d", (object?)detail ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<AdminActionRow>> RecentActionsAsync(
        string? action = null, string? admin = null, DateTimeOffset? since = null,
        int offset = 0, int limit = 50, CancellationToken ct = default)
    {
        if (!Configured) return [];
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            select admin_username, action, target, detail, created_at from public.admin_actions
            where (@action = '' or action ilike '%' || @action || '%')
              and (@admin = '' or admin_username ilike '%' || @admin || '%')
              and (@since is null or created_at >= @since)
            order by created_at desc limit @lim offset @off
            """, conn);
        cmd.Parameters.AddWithValue("action", LikeEscape(action ?? ""));
        cmd.Parameters.AddWithValue("admin", LikeEscape(admin ?? ""));
        // nullable 시각은 명시적 타입으로(무타입 DBNull은 Postgres 파라미터 타입 추론 실패 위험).
        cmd.Parameters.Add(new NpgsqlParameter("since", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)since ?? DBNull.Value });
        cmd.Parameters.AddWithValue("lim", limit);
        cmd.Parameters.AddWithValue("off", offset);
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

    // ── 대시보드(NON-100) ── 운영 지표를 한 번의 왕복으로 집계.
    public async Task<DashboardStats> GetDashboardAsync(CancellationToken ct = default)
    {
        if (!Configured) return new DashboardStats(0, 0, 0, 0, 0, 0, 0, []);
        await using var conn = await OpenAsync(ct);

        int pendingReports, openInquiries, usersToday, usersTotal, reviewsToday, reviewsTotal, suspended;
        await using (var cmd = new NpgsqlCommand(
            """
            select
              (select count(*) from public.reports where status = 'pending'),
              (select count(*) from public.inquiries where handled = false),
              (select count(*) from public.users where created_at >= date_trunc('day', now())),
              (select count(*) from public.users),
              (select count(*) from public.ratings where review is not null and length(trim(review)) > 0 and deleted_at is null and created_at >= date_trunc('day', now())),
              (select count(*) from public.ratings where review is not null and length(trim(review)) > 0 and deleted_at is null),
              (select count(*) from public.users where banned = true or suspended_until > now())
            """, conn))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            pendingReports = (int)r.GetInt64(0);
            openInquiries = (int)r.GetInt64(1);
            usersToday = (int)r.GetInt64(2);
            usersTotal = (int)r.GetInt64(3);
            reviewsToday = (int)r.GetInt64(4);
            reviewsTotal = (int)r.GetInt64(5);
            suspended = (int)r.GetInt64(6);
        }

        // 최근 7일(오늘 포함) 가입/리뷰 추이.
        var trend = new List<DashboardDay>();
        await using (var cmd = new NpgsqlCommand(
            """
            with days as (
              select generate_series(date_trunc('day', now()) - interval '6 days', date_trunc('day', now()), interval '1 day') as d
            )
            select days.d::date,
              (select count(*) from public.users u where u.created_at >= days.d and u.created_at < days.d + interval '1 day'),
              (select count(*) from public.ratings ra where ra.review is not null and length(trim(ra.review)) > 0 and ra.deleted_at is null and ra.created_at >= days.d and ra.created_at < days.d + interval '1 day')
            from days order by days.d
            """, conn))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                trend.Add(new DashboardDay(r.GetFieldValue<DateOnly>(0), (int)r.GetInt64(1), (int)r.GetInt64(2)));
        }

        return new DashboardStats(pendingReports, openInquiries, usersToday, usersTotal, reviewsToday, reviewsTotal, suspended, trend);
    }
}

public sealed record FaqRow(Guid Id, string Category, string Question, string Answer, int SortOrder, bool Published, DateTimeOffset UpdatedAt);
public sealed record InquiryRow(Guid Id, string Category, string Email, string Title, string Content, bool Handled, DateTimeOffset CreatedAt);
public sealed record LegalDocRow(string Type, string Locale, bool Published, DateTimeOffset UpdatedAt);
public sealed record LegalVersionRow(Guid Id, string? Version, DateTimeOffset PublishedAt, bool IsCurrent, DateOnly? EffectiveDate);

public sealed record ReportRow(
    Guid Id, string Reporter, string TargetType, string TargetId, string Reason, string? Detail,
    string Status, DateTimeOffset CreatedAt, string? Title, string? Sub, string? Body, bool TargetDeleted,
    Guid? OffenderId, string? OffenderName);

public sealed record SpotifyStatusRow(DateTimeOffset? BlockedUntil, int? RetryAfterSeconds, DateTimeOffset? UpdatedAt);

/// 조치를 수행한 관리자(감사 로그용).
public readonly record struct Actor(Guid? Id, string Username);
public sealed record AdminRow(Guid Id, string Username, DateTimeOffset CreatedAt, bool IsActive);
public sealed record AdminActionRow(string AdminUsername, string Action, string? Target, string? Detail, DateTimeOffset CreatedAt);

/// 유저 관리 목록 행: 현재 상태 + 신고 누적 수.
public sealed record UserRow(Guid Id, string Username, string? AvatarUrl, DateTimeOffset CreatedAt,
    DateTimeOffset? SuspendedUntil, bool Banned, int ReportCount);
public sealed record SanctionRow(string Type, DateTimeOffset? Until, string? Reason, string AdminUsername, DateTimeOffset CreatedAt);

/// 리뷰 모더레이션 목록 행(NON-98).
public sealed record ReviewRow(
    Guid Id, Guid UserId, string Username, string TargetType, string? Name, string? Artist, string? SpotifyId,
    decimal Score, string Review, DateTimeOffset CreatedAt);

/// 대시보드 운영 지표(NON-100).
public sealed record DashboardStats(
    int PendingReports, int OpenInquiries, int UsersToday, int UsersTotal,
    int ReviewsToday, int ReviewsTotal, int SuspendedUsers, IReadOnlyList<DashboardDay> Trend);
public sealed record DashboardDay(DateOnly Date, int Users, int Reviews);
