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

    // ── 리뷰 모더레이션(신고 없이 직접) NON-98 ──

    /// 리뷰(review 있는 평가) 목록. search 는 유저명·리뷰 본문 부분일치. 삭제된 것은 제외.
    public async Task<List<ReviewRow>> ListReviewsAsync(string? search, CancellationToken ct = default)
    {
        if (!Configured) return [];
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            select r.id, u.id, u.username, r.target_type, r.target_name, r.target_artist, r.target_spotify_id,
                   r.score, r.review, r.created_at
            from public.ratings r
            join public.users u on u.id = r.user_id
            where r.review is not null and length(trim(r.review)) > 0 and r.deleted_at is null
              and (@q = '' or u.username ilike '%' || @q || '%' or r.review ilike '%' || @q || '%')
            order by r.created_at desc
            limit 100
            """, conn);
        cmd.Parameters.AddWithValue("q", search ?? "");
        var list = new List<ReviewRow>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            list.Add(new ReviewRow(
                rd.GetGuid(0), rd.GetGuid(1), rd.GetString(2), rd.GetString(3),
                rd.IsDBNull(4) ? null : rd.GetString(4), rd.IsDBNull(5) ? null : rd.GetString(5),
                rd.IsDBNull(6) ? null : rd.GetString(6), rd.GetDecimal(7), rd.GetString(8),
                rd.GetFieldValue<DateTimeOffset>(9)));
        return list;
    }

    /// 신고 없이 리뷰를 소프트삭제(사유 기록). 감사 로그 남김.
    public async Task SoftDeleteRatingAsync(Guid ratingId, string? reason, Actor actor, CancellationToken ct = default)
    {
        if (!Configured) return;
        await using var conn = await OpenAsync(ct);
        await using var del = new NpgsqlCommand(
            """
            update public.ratings
            set deleted_at = now(), deleted_by = @by, deleted_reason = @reason
            where id = @rid and deleted_at is null
            """, conn);
        del.Parameters.AddWithValue("rid", ratingId);
        del.Parameters.AddWithValue("by", (object?)actor.Id ?? DBNull.Value);
        del.Parameters.AddWithValue("reason", (object?)reason ?? DBNull.Value);
        await del.ExecuteNonQueryAsync(ct);
        await LogAsync(conn, actor, "rating.delete", ratingId.ToString(), "moderation", ct);
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

        // 경고는 유저에게 알림으로 전달(NON-58). 신고에서 부여했으면 대상 rating id를 target 으로 연결.
        if (type == "warning")
        {
            string? targetRatingId = null;
            if (reportId is { } rid2)
            {
                await using var q = new NpgsqlCommand(
                    "select target_id from public.reports where id = @rid and target_type = 'rating'", conn, tx);
                q.Parameters.AddWithValue("rid", rid2);
                targetRatingId = await q.ExecuteScalarAsync(ct) as string;
            }
            await using var notif = new NpgsqlCommand(
                """
                insert into public.notifications (recipient_id, actor_id, type, target_id, detail)
                values (@uid, null, 'warning', @target, @detail)
                """, conn, tx);
            notif.Parameters.AddWithValue("uid", userId);
            notif.Parameters.AddWithValue("target", (object?)targetRatingId ?? DBNull.Value);
            notif.Parameters.AddWithValue("detail", (object?)reason ?? DBNull.Value);
            await notif.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);

        var detail = until is { } u ? $"until {u:u}" : reason;
        await LogAsync(conn, actor, $"user.{type}", userId.ToString(),
            reportId is { } rp ? $"report {rp}; {detail}" : detail, ct);
    }

    // ── 약관/개인정보 (NON-64) ──
    public async Task<List<LegalDocRow>> ListLegalDocsAsync(CancellationToken ct = default)
    {
        if (!Configured) return [];
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "select type, locale, published, updated_at from public.legal_documents order by type, locale", conn);
        var list = new List<LegalDocRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new LegalDocRow(r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetFieldValue<DateTimeOffset>(3)));
        return list;
    }

    // 편집용: 미게시본 포함 원문·게시여부. 없으면 null.
    public async Task<(string Content, bool Published)?> GetLegalDocAsync(string type, string locale, CancellationToken ct = default)
    {
        if (!Configured) return null;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "select content, published from public.legal_documents where type = @t and locale = @l", conn);
        cmd.Parameters.AddWithValue("t", type);
        cmd.Parameters.AddWithValue("l", locale);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return (r.GetString(0), r.GetBoolean(1));
    }

    // 초안 저장. publish=true 면 legal_versions 에 불변 스냅샷도 남긴다(NON-69). version=라벨(NON-70), effectiveDate=시행일(미입력이면 게시일, NON-72).
    public async Task SaveLegalDocAsync(string type, string locale, string content, string? version, DateOnly? effectiveDate, bool published, Actor actor, CancellationToken ct = default)
    {
        if (!Configured) return;
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var cmd = new NpgsqlCommand(
            """
            insert into public.legal_documents (type, locale, content, published, updated_at, updated_by)
            values (@t, @l, @c, @p, now(), @by)
            on conflict (type, locale) do update
                set content = excluded.content, published = legal_documents.published or excluded.published,
                    updated_at = now(), updated_by = excluded.updated_by
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("t", type);
            cmd.Parameters.AddWithValue("l", locale);
            cmd.Parameters.AddWithValue("c", content);
            cmd.Parameters.AddWithValue("p", published);
            cmd.Parameters.AddWithValue("by", (object?)actor.Id ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (published)
        {
            // (종류×로케일)당 current 는 하나만: 이전 current 해제 후 새 것을 current 로(NON-71).
            await using (var clear = new NpgsqlCommand(
                "update public.legal_versions set is_current = false where type = @t and locale = @l and is_current", conn, tx))
            {
                clear.Parameters.AddWithValue("t", type);
                clear.Parameters.AddWithValue("l", locale);
                await clear.ExecuteNonQueryAsync(ct);
            }
            await using var ver = new NpgsqlCommand(
                """
                insert into public.legal_versions (type, locale, content, version, is_current, effective_date, published_by)
                values (@t, @l, @c, @v, true, coalesce(@eff, current_date), @by)
                """, conn, tx);
            ver.Parameters.AddWithValue("t", type);
            ver.Parameters.AddWithValue("l", locale);
            ver.Parameters.AddWithValue("c", content);
            ver.Parameters.AddWithValue("v", (object?)(string.IsNullOrWhiteSpace(version) ? null : version.Trim()) ?? DBNull.Value);
            ver.Parameters.AddWithValue("eff", (object?)effectiveDate ?? DBNull.Value);
            ver.Parameters.AddWithValue("by", (object?)actor.Id ?? DBNull.Value);
            await ver.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        await LogAsync(conn, actor, published ? "legal.publish" : "legal.save", $"{type}/{locale}", null, ct);
    }

    // 게시 버전 이력(최신순).
    public async Task<List<LegalVersionRow>> ListLegalVersionsAsync(string type, string locale, CancellationToken ct = default)
    {
        if (!Configured) return [];
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "select id, version, published_at, is_current, effective_date from public.legal_versions where type = @t and locale = @l order by published_at desc limit 50", conn);
        cmd.Parameters.AddWithValue("t", type);
        cmd.Parameters.AddWithValue("l", locale);
        var list = new List<LegalVersionRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new LegalVersionRow(r.GetGuid(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetFieldValue<DateTimeOffset>(2), r.GetBoolean(3),
                r.IsDBNull(4) ? null : r.GetFieldValue<DateOnly>(4)));
        return list;
    }

    // 특정 버전 원문(롤백·보기용).
    public async Task<string?> GetLegalVersionContentAsync(Guid versionId, CancellationToken ct = default)
    {
        if (!Configured) return null;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("select content from public.legal_versions where id = @id", conn);
        cmd.Parameters.AddWithValue("id", versionId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    // ── FAQ (NON-73) ──
    public async Task<List<FaqRow>> ListFaqAsync(string locale, CancellationToken ct = default)
    {
        if (!Configured) return [];
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "select id, category, question, answer, sort_order, published, updated_at from public.faq_items where locale = @l order by sort_order, category", conn);
        cmd.Parameters.AddWithValue("l", locale);
        var list = new List<FaqRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new FaqRow(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetBoolean(5), r.GetFieldValue<DateTimeOffset>(6)));
        return list;
    }

    // id 있으면 수정, 없으면 생성. 저장된 id 반환.
    public async Task<Guid> SaveFaqAsync(Guid? id, string locale, string category, string question, string answer, int sortOrder, bool published, Actor actor, CancellationToken ct = default)
    {
        if (!Configured) return Guid.Empty;
        await using var conn = await OpenAsync(ct);
        Guid savedId;
        if (id is { } existing)
        {
            await using var upd = new NpgsqlCommand(
                """
                update public.faq_items set locale = @l, category = @cat, question = @q, answer = @a,
                    sort_order = @o, published = @p, updated_at = now(), updated_by = @by
                where id = @id
                """, conn);
            upd.Parameters.AddWithValue("id", existing);
            AddFaqParams(upd, locale, category, question, answer, sortOrder, published, actor);
            await upd.ExecuteNonQueryAsync(ct);
            savedId = existing;
        }
        else
        {
            await using var ins = new NpgsqlCommand(
                """
                insert into public.faq_items (locale, category, question, answer, sort_order, published, updated_by)
                values (@l, @cat, @q, @a, @o, @p, @by) returning id
                """, conn);
            AddFaqParams(ins, locale, category, question, answer, sortOrder, published, actor);
            savedId = (Guid)(await ins.ExecuteScalarAsync(ct))!;
        }
        await LogAsync(conn, actor, "faq.save", savedId.ToString(), $"{locale}/{category}", ct);
        return savedId;
    }

    private static void AddFaqParams(NpgsqlCommand c, string locale, string category, string question, string answer, int sortOrder, bool published, Actor actor)
    {
        c.Parameters.AddWithValue("l", locale);
        c.Parameters.AddWithValue("cat", category ?? "");
        c.Parameters.AddWithValue("q", question);
        c.Parameters.AddWithValue("a", answer ?? "");
        c.Parameters.AddWithValue("o", sortOrder);
        c.Parameters.AddWithValue("p", published);
        c.Parameters.AddWithValue("by", (object?)actor.Id ?? DBNull.Value);
    }

    public async Task DeleteFaqAsync(Guid id, Actor actor, CancellationToken ct = default)
    {
        if (!Configured) return;
        await using var conn = await OpenAsync(ct);
        await using (var cmd = new NpgsqlCommand("delete from public.faq_items where id = @id", conn))
        {
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await LogAsync(conn, actor, "faq.delete", id.ToString(), null, ct);
    }

    // ── 문의 (NON-76) ──
    public async Task<List<InquiryRow>> ListInquiriesAsync(bool? handled, CancellationToken ct = default)
    {
        if (!Configured) return [];
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            select id, category, email, title, content, handled, created_at from public.inquiries
            where @all or handled = @h
            order by created_at desc limit 300
            """, conn);
        cmd.Parameters.AddWithValue("all", handled is null);
        cmd.Parameters.AddWithValue("h", handled ?? false);
        var list = new List<InquiryRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new InquiryRow(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetBoolean(5), r.GetFieldValue<DateTimeOffset>(6)));
        return list;
    }

    public async Task SetInquiryHandledAsync(Guid id, bool handled, Actor actor, CancellationToken ct = default)
    {
        if (!Configured) return;
        await using var conn = await OpenAsync(ct);
        await using (var cmd = new NpgsqlCommand(
            "update public.inquiries set handled = @h, handled_at = case when @h then now() else null end, handled_by = case when @h then @by else null end where id = @id", conn))
        {
            cmd.Parameters.AddWithValue("h", handled);
            cmd.Parameters.AddWithValue("by", (object?)actor.Id ?? DBNull.Value);
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await LogAsync(conn, actor, handled ? "inquiry.handled" : "inquiry.reopen", id.ToString(), null, ct);
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
public sealed record AdminRow(Guid Id, string Username, DateTimeOffset CreatedAt);
public sealed record AdminActionRow(string AdminUsername, string Action, string? Target, string? Detail, DateTimeOffset CreatedAt);

/// 유저 관리 목록 행: 현재 상태 + 신고 누적 수.
public sealed record UserRow(Guid Id, string Username, string? AvatarUrl, DateTimeOffset CreatedAt,
    DateTimeOffset? SuspendedUntil, bool Banned, int ReportCount);
public sealed record SanctionRow(string Type, DateTimeOffset? Until, string? Reason, string AdminUsername, DateTimeOffset CreatedAt);

/// 리뷰 모더레이션 목록 행(NON-98).
public sealed record ReviewRow(
    Guid Id, Guid UserId, string Username, string TargetType, string? Name, string? Artist, string? SpotifyId,
    decimal Score, string Review, DateTimeOffset CreatedAt);
