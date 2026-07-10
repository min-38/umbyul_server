using Npgsql;

namespace Admin.Data;

// 공지사항(NON-158/167). 로케일별 본문 + 게시. 최초 게시 시 전체 유저 강제 알림(끄기 불가) 브로드캐스트.
public sealed partial class AdminDb
{
    // 게시하려면 이 로케일 전부 제목·본문이 있어야 함(NON-158).
    public static readonly string[] RequiredAnnouncementLocales = ["ko", "en", "ja", "es"];

    public async Task<List<AnnouncementRow>> ListAnnouncementsAsync(CancellationToken ct = default)
    {
        if (!Configured) return [];
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            select a.id,
                   coalesce(
                     (select title from public.announcement_locales where announcement_id = a.id and locale = 'ko'),
                     (select title from public.announcement_locales where announcement_id = a.id and locale = 'en'),
                     (select title from public.announcement_locales where announcement_id = a.id order by locale limit 1),
                     ''
                   ) as title,
                   a.published, a.published_at, a.updated_at
            from public.announcements a
            order by a.updated_at desc
            """, conn);
        var list = new List<AnnouncementRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new AnnouncementRow(
                r.GetGuid(0), r.GetString(1), r.GetBoolean(2),
                r.IsDBNull(3) ? null : r.GetFieldValue<DateTimeOffset>(3),
                r.GetFieldValue<DateTimeOffset>(4)));
        return list;
    }

    // 새 초안 공지 생성 → id 반환.
    public async Task<Guid> CreateAnnouncementAsync(Actor actor, CancellationToken ct = default)
    {
        if (!Configured) return Guid.Empty;
        await using var conn = await OpenAsync(ct);
        Guid id;
        await using (var cmd = new NpgsqlCommand(
            "insert into public.announcements (updated_by) values (@by) returning id", conn))
        {
            cmd.Parameters.AddWithValue("by", (object?)actor.Id ?? DBNull.Value);
            id = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
        }
        await LogAsync(conn, actor, "announcement.create", id.ToString(), null, ct);
        return id;
    }

    // 특정 (공지, 로케일) 원문. 없으면 빈 값.
    public async Task<(string Title, string Body)> GetAnnouncementLocaleAsync(Guid id, string locale, CancellationToken ct = default)
    {
        if (!Configured) return ("", "");
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "select title, body from public.announcement_locales where announcement_id = @id and locale = @l", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("l", locale);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return ("", "");
        return (r.GetString(0), r.GetString(1));
    }

    // 로케일 본문 저장(upsert) + 공지 updated_at 갱신.
    public async Task SaveAnnouncementLocaleAsync(Guid id, string locale, string title, string body, Actor actor, CancellationToken ct = default)
    {
        if (!Configured) return;
        await using var conn = await OpenAsync(ct);
        await using (var cmd = new NpgsqlCommand(
            """
            insert into public.announcement_locales (announcement_id, locale, title, body)
            values (@id, @l, @t, @b)
            on conflict (announcement_id, locale) do update set title = excluded.title, body = excluded.body
            """, conn))
        {
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("l", locale);
            cmd.Parameters.AddWithValue("t", title ?? "");
            cmd.Parameters.AddWithValue("b", body ?? "");
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var upd = new NpgsqlCommand(
            "update public.announcements set updated_at = now(), updated_by = @by where id = @id", conn))
        {
            upd.Parameters.AddWithValue("by", (object?)actor.Id ?? DBNull.Value);
            upd.Parameters.AddWithValue("id", id);
            await upd.ExecuteNonQueryAsync(ct);
        }
        await LogAsync(conn, actor, "announcement.save", id.ToString(), locale, ct);
    }

    // 로케일별 완성도(제목·본문 모두 채워짐). 필수 로케일 키만.
    public async Task<Dictionary<string, bool>> GetAnnouncementLocaleStatusAsync(Guid id, CancellationToken ct = default)
    {
        var status = RequiredAnnouncementLocales.ToDictionary(l => l, _ => false);
        if (!Configured) return status;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "select locale, (btrim(title) <> '' and btrim(body) <> '') from public.announcement_locales where announcement_id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            if (status.ContainsKey(r.GetString(0))) status[r.GetString(0)] = r.GetBoolean(1);
        return status;
    }

    // 게시/게시취소. 게시는 필수 로케일 전부 완성돼야 가능(미완성 로케일 목록 반환 시 게시 안 함).
    // 최초 게시(notified_at is null)에만 전체 유저 강제 알림 브로드캐스트(끄기 불가 — opt-out 절 없음). 트랜잭션.
    public async Task<IReadOnlyList<string>> PublishAnnouncementAsync(Guid id, bool publish, Actor actor, CancellationToken ct = default)
    {
        if (!Configured) return Array.Empty<string>();
        await using var conn = await OpenAsync(ct);

        if (publish)
        {
            // 완성도 검사 — 필수 로케일 전부 제목·본문 있어야 게시.
            var complete = new HashSet<string>();
            await using (var chk = new NpgsqlCommand(
                "select locale from public.announcement_locales where announcement_id = @id and btrim(title) <> '' and btrim(body) <> ''", conn))
            {
                chk.Parameters.AddWithValue("id", id);
                await using var cr = await chk.ExecuteReaderAsync(ct);
                while (await cr.ReadAsync(ct)) complete.Add(cr.GetString(0));
            }
            var missing = RequiredAnnouncementLocales.Where(l => !complete.Contains(l)).ToList();
            if (missing.Count > 0) return missing; // 게시 차단
        }

        await using var tx = await conn.BeginTransactionAsync(ct);

        if (publish)
        {
            await using (var cmd = new NpgsqlCommand(
                "update public.announcements set published = true, published_at = coalesce(published_at, now()), updated_at = now(), updated_by = @by where id = @id", conn, tx))
            {
                cmd.Parameters.AddWithValue("by", (object?)actor.Id ?? DBNull.Value);
                cmd.Parameters.AddWithValue("id", id);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            bool firstPublish;
            await using (var chk = new NpgsqlCommand("select notified_at is null from public.announcements where id = @id", conn, tx))
            {
                chk.Parameters.AddWithValue("id", id);
                firstPublish = (bool)(await chk.ExecuteScalarAsync(ct))!;
            }
            if (firstPublish)
            {
                // 전체 유저에게 1행씩. opt-out 절 없음 → 끄기 불가(warning 방식).
                await using (var br = new NpgsqlCommand(
                    "insert into public.notifications (recipient_id, type, target_id) select id, 'announcement', @id::text from public.users", conn, tx))
                {
                    br.Parameters.AddWithValue("id", id);
                    await br.ExecuteNonQueryAsync(ct);
                }
                await using (var mark = new NpgsqlCommand(
                    "update public.announcements set notified_at = now() where id = @id", conn, tx))
                {
                    mark.Parameters.AddWithValue("id", id);
                    await mark.ExecuteNonQueryAsync(ct);
                }
            }
        }
        else
        {
            await using var cmd = new NpgsqlCommand(
                "update public.announcements set published = false, updated_at = now(), updated_by = @by where id = @id", conn, tx);
            cmd.Parameters.AddWithValue("by", (object?)actor.Id ?? DBNull.Value);
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        await LogAsync(conn, actor, publish ? "announcement.publish" : "announcement.unpublish", id.ToString(), null, ct);
        return Array.Empty<string>();
    }

    public async Task DeleteAnnouncementAsync(Guid id, Actor actor, CancellationToken ct = default)
    {
        if (!Configured) return;
        await using var conn = await OpenAsync(ct);
        // 관련 알림 정리(FK 아님 — target_id 텍스트 매칭). 그 뒤 공지 삭제(로케일은 cascade).
        await using (var delN = new NpgsqlCommand(
            "delete from public.notifications where type = 'announcement' and target_id = @id::text", conn))
        {
            delN.Parameters.AddWithValue("id", id);
            await delN.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = new NpgsqlCommand("delete from public.announcements where id = @id", conn))
        {
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await LogAsync(conn, actor, "announcement.delete", id.ToString(), null, ct);
    }
}

// 공지 목록 행(대표 제목 = ko>en>아무거나).
public sealed record AnnouncementRow(Guid Id, string Title, bool Published, DateTimeOffset? PublishedAt, DateTimeOffset UpdatedAt);
