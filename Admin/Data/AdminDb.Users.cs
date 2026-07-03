using Npgsql;

namespace Admin.Data;

// AdminDb 도메인 분리(NON-110): 유저 모더레이션 + 제재(경고/정지/영구정지/해제). 코어의 partial.
public sealed partial class AdminDb
{
    // ── 유저 모더레이션(NON-47) ──
    public async Task<List<UserRow>> ListUsersAsync(string? search, int offset = 0, int limit = 50, CancellationToken ct = default)
    {
        if (!Configured) return [];
        await using var conn = await OpenAsync(ct);
        // 검색은 username 또는 가입 이메일(auth.users) ILIKE. service_role 자격이라 auth 스키마 조회 가능.
        await using var cmd = new NpgsqlCommand(
            """
            select u.id, u.username, u.avatar_url, u.created_at, u.suspended_until, u.banned,
                   (select count(*) from public.reports r
                      where (r.target_type = 'user' and r.target_id = u.id::text)
                         or (r.target_type = 'rating' and r.target_id in
                             (select ra.id::text from public.ratings ra where ra.user_id = u.id))) as reports
            from public.users u
            where @q = ''
               or u.username ilike '%' || @q || '%'
               or exists (select 1 from auth.users au where au.id = u.id and au.email ilike '%' || @q || '%')
            order by (u.banned or u.suspended_until > now()) desc, u.created_at desc
            limit @lim offset @off
            """, conn);
        cmd.Parameters.AddWithValue("q", search ?? "");
        cmd.Parameters.AddWithValue("lim", limit);
        cmd.Parameters.AddWithValue("off", offset);
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
}
