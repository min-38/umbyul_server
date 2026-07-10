using Npgsql;

namespace Admin.Data;

// 패치노트(NON-159/170). 버전·상태·릴리스일 + 로케일별 본문. 게시는 모든 언어 완성 시에만. 강제 알림 없음.
public sealed partial class AdminDb
{
    public async Task<List<PatchNoteRow>> ListPatchNotesAsync(CancellationToken ct = default)
    {
        if (!Configured) return [];
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            select id, version, status, released_at, published, updated_at
            from public.patch_notes
            order by (status = 'in_progress') desc, released_at desc nulls last, updated_at desc
            """, conn);
        var list = new List<PatchNoteRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new PatchNoteRow(
                r.GetGuid(0), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetFieldValue<DateOnly>(3),
                r.GetBoolean(4), r.GetFieldValue<DateTimeOffset>(5)));
        return list;
    }

    public async Task<Guid> CreatePatchNoteAsync(Actor actor, CancellationToken ct = default)
    {
        if (!Configured) return Guid.Empty;
        await using var conn = await OpenAsync(ct);
        Guid id;
        await using (var cmd = new NpgsqlCommand(
            "insert into public.patch_notes (updated_by) values (@by) returning id", conn))
        {
            cmd.Parameters.AddWithValue("by", (object?)actor.Id ?? DBNull.Value);
            id = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
        }
        await LogAsync(conn, actor, "patchnote.create", id.ToString(), null, ct);
        return id;
    }

    // 메타(버전·상태·릴리스일). 없으면 기본값.
    public async Task<(string Version, string Status, DateOnly? ReleasedAt)> GetPatchNoteMetaAsync(Guid id, CancellationToken ct = default)
    {
        if (!Configured) return ("", "released", null);
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "select version, status, released_at from public.patch_notes where id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return ("", "released", null);
        return (r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetFieldValue<DateOnly>(2));
    }

    public async Task<string> GetPatchNoteLocaleAsync(Guid id, string locale, CancellationToken ct = default)
    {
        if (!Configured) return "";
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "select body from public.patch_note_locales where patch_note_id = @id and locale = @l", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("l", locale);
        return (await cmd.ExecuteScalarAsync(ct)) as string ?? "";
    }

    // 메타 + 현재 로케일 본문 저장(한 번에).
    public async Task SavePatchNoteAsync(Guid id, string version, string status, DateOnly? releasedAt, string locale, string body, Actor actor, CancellationToken ct = default)
    {
        if (!Configured) return;
        await using var conn = await OpenAsync(ct);
        await using (var meta = new NpgsqlCommand(
            "update public.patch_notes set version = @v, status = @s, released_at = @d, updated_at = now(), updated_by = @by where id = @id", conn))
        {
            meta.Parameters.AddWithValue("v", version ?? "");
            meta.Parameters.AddWithValue("s", status == "in_progress" ? "in_progress" : "released");
            meta.Parameters.AddWithValue("d", (object?)releasedAt ?? DBNull.Value);
            meta.Parameters.AddWithValue("by", (object?)actor.Id ?? DBNull.Value);
            meta.Parameters.AddWithValue("id", id);
            await meta.ExecuteNonQueryAsync(ct);
        }
        await using (var loc = new NpgsqlCommand(
            """
            insert into public.patch_note_locales (patch_note_id, locale, body)
            values (@id, @l, @b)
            on conflict (patch_note_id, locale) do update set body = excluded.body
            """, conn))
        {
            loc.Parameters.AddWithValue("id", id);
            loc.Parameters.AddWithValue("l", locale);
            loc.Parameters.AddWithValue("b", body ?? "");
            await loc.ExecuteNonQueryAsync(ct);
        }
        await LogAsync(conn, actor, "patchnote.save", id.ToString(), locale, ct);
    }

    // 로케일별 완성도(본문 채워짐). 필수 로케일 키만(공지와 동일 기준 재사용).
    public async Task<Dictionary<string, bool>> GetPatchNoteLocaleStatusAsync(Guid id, CancellationToken ct = default)
    {
        var status = RequiredAnnouncementLocales.ToDictionary(l => l, _ => false);
        if (!Configured) return status;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "select locale, (body <> '') from public.patch_note_locales where patch_note_id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            if (status.ContainsKey(r.GetString(0))) status[r.GetString(0)] = r.GetBoolean(1);
        return status;
    }

    // 게시/게시취소. 게시는 모든 필수 로케일 본문 완성 시에만(미완성 목록 반환 시 게시 안 함).
    public async Task<IReadOnlyList<string>> PublishPatchNoteAsync(Guid id, bool publish, Actor actor, CancellationToken ct = default)
    {
        if (!Configured) return Array.Empty<string>();
        await using var conn = await OpenAsync(ct);

        if (publish)
        {
            var complete = new HashSet<string>();
            await using (var chk = new NpgsqlCommand(
                "select locale from public.patch_note_locales where patch_note_id = @id and body <> ''", conn))
            {
                chk.Parameters.AddWithValue("id", id);
                await using var cr = await chk.ExecuteReaderAsync(ct);
                while (await cr.ReadAsync(ct)) complete.Add(cr.GetString(0));
            }
            var missing = RequiredAnnouncementLocales.Where(l => !complete.Contains(l)).ToList();
            if (missing.Count > 0) return missing;
        }

        await using (var cmd = new NpgsqlCommand(
            "update public.patch_notes set published = @p, updated_at = now(), updated_by = @by where id = @id", conn))
        {
            cmd.Parameters.AddWithValue("p", publish);
            cmd.Parameters.AddWithValue("by", (object?)actor.Id ?? DBNull.Value);
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await LogAsync(conn, actor, publish ? "patchnote.publish" : "patchnote.unpublish", id.ToString(), null, ct);
        return Array.Empty<string>();
    }

    public async Task DeletePatchNoteAsync(Guid id, Actor actor, CancellationToken ct = default)
    {
        if (!Configured) return;
        await using var conn = await OpenAsync(ct);
        await using (var cmd = new NpgsqlCommand("delete from public.patch_notes where id = @id", conn))
        {
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await LogAsync(conn, actor, "patchnote.delete", id.ToString(), null, ct);
    }
}

public sealed record PatchNoteRow(Guid Id, string Version, string Status, DateOnly? ReleasedAt, bool Published, DateTimeOffset UpdatedAt);
