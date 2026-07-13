using Npgsql;

namespace Admin.Data;

// AdminDb 도메인 분리(NON-110): 약관·개인정보 / FAQ / 문의. 코어(AdminDb.cs)의 partial.
public sealed partial class AdminDb
{
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

    // 편집용: 미게시본 포함 원문·게시여부 + 초안에 임시 저장된 버전·시행일. 없으면 null.
    // version/effective_date 컬럼이 아직 없는(마이그레이션 전) DB에선 그 둘을 null로 폴백(NON: graceful degrade).
    public async Task<(string Content, bool Published, string? Version, DateOnly? EffectiveDate)?> GetLegalDocAsync(string type, string locale, CancellationToken ct = default)
    {
        if (!Configured) return null;
        await using var conn = await OpenAsync(ct);
        try
        {
            await using var cmd = new NpgsqlCommand(
                "select content, published, version, effective_date from public.legal_documents where type = @t and locale = @l", conn);
            cmd.Parameters.AddWithValue("t", type);
            cmd.Parameters.AddWithValue("l", locale);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            return (r.GetString(0), r.GetBoolean(1),
                    r.IsDBNull(2) ? null : r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetFieldValue<DateOnly>(3));
        }
        catch (PostgresException ex) when (ex.SqlState == "42703")
        {
            await using var cmd = new NpgsqlCommand(
                "select content, published from public.legal_documents where type = @t and locale = @l", conn);
            cmd.Parameters.AddWithValue("t", type);
            cmd.Parameters.AddWithValue("l", locale);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            return (r.GetString(0), r.GetBoolean(1), null, null);
        }
    }

    // 초안 저장(단일 로케일 content + 임시 버전·시행일). 게시 상태는 건드리지 않음 — 게시는 릴리스(다국어 원자)로만.
    // 버전·시행일은 초안 편의용 임시값(게시 시 legal_versions로 확정). 컬럼 미적용 DB에선 best-effort로 무시.
    public async Task SaveLegalDraftAsync(string type, string locale, string content, string? version, DateOnly? effectiveDate, Actor actor, CancellationToken ct = default)
    {
        if (!Configured) return;
        await using var conn = await OpenAsync(ct);
        await using (var cmd = new NpgsqlCommand(
            """
            insert into public.legal_documents (type, locale, content, published, updated_at, updated_by)
            values (@t, @l, @c, false, now(), @by)
            on conflict (type, locale) do update
                set content = excluded.content, updated_at = now(), updated_by = excluded.updated_by
            """, conn))
        {
            cmd.Parameters.AddWithValue("t", type);
            cmd.Parameters.AddWithValue("l", locale);
            cmd.Parameters.AddWithValue("c", content);
            cmd.Parameters.AddWithValue("by", (object?)actor.Id ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        // 버전·시행일 임시 저장(best-effort — 컬럼 미적용 시 무시).
        try
        {
            await using var meta = new NpgsqlCommand(
                "update public.legal_documents set version = @v, effective_date = @eff where type = @t and locale = @l", conn);
            meta.Parameters.AddWithValue("t", type);
            meta.Parameters.AddWithValue("l", locale);
            meta.Parameters.AddWithValue("v", (object?)(string.IsNullOrWhiteSpace(version) ? null : version.Trim()) ?? DBNull.Value);
            meta.Parameters.AddWithValue("eff", (object?)effectiveDate ?? DBNull.Value);
            await meta.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == "42703") { /* version/effective_date 컬럼 미적용 — 무시 */ }

        await LogAsync(conn, actor, "legal.save", $"{type}/{locale}", null, ct);
    }

    // 원자적 다국어 게시(릴리스): 모든 필수 로케일을 같은 version·effective_date 로 한 트랜잭션에 스냅샷.
    // contents = 로케일→내용(호출 전 전부 비어있지 않음을 검증). 언어별 버전 어긋남 방지(설계 결정).
    public async Task PublishLegalReleaseAsync(string type, string version, DateOnly? effectiveDate,
        IReadOnlyDictionary<string, string> contents, Actor actor, CancellationToken ct = default)
    {
        if (!Configured) return;
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        foreach (var (locale, content) in contents)
        {
            // 초안 upsert(published=true)
            await using (var doc = new NpgsqlCommand(
                """
                insert into public.legal_documents (type, locale, content, published, updated_at, updated_by)
                values (@t, @l, @c, true, now(), @by)
                on conflict (type, locale) do update
                    set content = excluded.content, published = true, updated_at = now(), updated_by = excluded.updated_by
                """, conn, tx))
            {
                doc.Parameters.AddWithValue("t", type);
                doc.Parameters.AddWithValue("l", locale);
                doc.Parameters.AddWithValue("c", content);
                doc.Parameters.AddWithValue("by", (object?)actor.Id ?? DBNull.Value);
                await doc.ExecuteNonQueryAsync(ct);
            }
            // (종류×로케일)당 current 는 하나만: 이전 current 해제(NON-71).
            await using (var clear = new NpgsqlCommand(
                "update public.legal_versions set is_current = false where type = @t and locale = @l and is_current", conn, tx))
            {
                clear.Parameters.AddWithValue("t", type);
                clear.Parameters.AddWithValue("l", locale);
                await clear.ExecuteNonQueryAsync(ct);
            }
            // 불변 스냅샷(로케일별 행, 공유 version·effective_date)
            await using (var ver = new NpgsqlCommand(
                """
                insert into public.legal_versions (type, locale, content, version, is_current, effective_date, published_by)
                values (@t, @l, @c, @v, true, coalesce(@eff, current_date), @by)
                """, conn, tx))
            {
                ver.Parameters.AddWithValue("t", type);
                ver.Parameters.AddWithValue("l", locale);
                ver.Parameters.AddWithValue("c", content);
                ver.Parameters.AddWithValue("v", version.Trim());
                ver.Parameters.AddWithValue("eff", (object?)effectiveDate ?? DBNull.Value);
                ver.Parameters.AddWithValue("by", (object?)actor.Id ?? DBNull.Value);
                await ver.ExecuteNonQueryAsync(ct);
            }
        }

        await tx.CommitAsync(ct);
        await LogAsync(conn, actor, "legal.publish", $"{type}/{version}", null, ct);
    }

    // 릴리스 이력(버전 단위, 최신순). 한 릴리스 = 같은 version 의 여러 로케일 스냅샷.
    public async Task<List<LegalReleaseRow>> ListLegalReleasesAsync(string type, CancellationToken ct = default)
    {
        if (!Configured) return [];
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            select version,
                   max(effective_date) as effective_date,
                   max(published_at)   as published_at,
                   bool_or(is_current) as is_current,
                   array_agg(distinct locale order by locale) as locales
            from public.legal_versions
            where type = @t and version is not null
            group by version
            order by max(published_at) desc
            limit 50
            """, conn);
        cmd.Parameters.AddWithValue("t", type);
        var list = new List<LegalReleaseRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new LegalReleaseRow(
                r.GetString(0),
                r.IsDBNull(1) ? null : r.GetFieldValue<DateOnly>(1),
                r.GetFieldValue<DateTimeOffset>(2),
                r.GetBoolean(3),
                r.GetFieldValue<string[]>(4)));
        return list;
    }

    // 특정 릴리스(버전)의 로케일별 원문. 보기·복원용(감사 로그 남김, NON-103).
    public async Task<Dictionary<string, string>> GetLegalReleaseContentsAsync(string type, string version, Actor actor, CancellationToken ct = default)
    {
        var dict = new Dictionary<string, string>();
        if (!Configured) return dict;
        await using var conn = await OpenAsync(ct);
        await using (var cmd = new NpgsqlCommand(
            "select locale, content from public.legal_versions where type = @t and version = @v", conn))
        {
            cmd.Parameters.AddWithValue("t", type);
            cmd.Parameters.AddWithValue("v", version);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                dict[r.GetString(0)] = r.GetString(1);
        }
        if (dict.Count > 0)
            await LogAsync(conn, actor, "legal.version_load", $"{type}/{version}", null, ct);
        return dict;
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
    public async Task<List<InquiryRow>> ListInquiriesAsync(
        bool? handled, string? search = null, string? category = null, int offset = 0, int limit = 50, CancellationToken ct = default)
    {
        if (!Configured) return [];
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            select id, category, email, title, content, handled, created_at from public.inquiries
            where (@all or handled = @h)
              and (@q = '' or title ilike '%' || @q || '%' or content ilike '%' || @q || '%' or email ilike '%' || @q || '%')
              and (@cat = '' or category = @cat)
            order by created_at desc limit @lim offset @off
            """, conn);
        cmd.Parameters.AddWithValue("all", handled is null);
        cmd.Parameters.AddWithValue("h", handled ?? false);
        cmd.Parameters.AddWithValue("q", LikeEscape(search ?? ""));
        cmd.Parameters.AddWithValue("cat", category ?? "");
        cmd.Parameters.AddWithValue("lim", limit);
        cmd.Parameters.AddWithValue("off", offset);
        var list = new List<InquiryRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new InquiryRow(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetBoolean(5), r.GetFieldValue<DateTimeOffset>(6)));
        return list;
    }

    // 카테고리 필터용 — 실제 저장된 값(로케일별 라벨 혼재)을 그대로 노출.
    public async Task<List<string>> InquiryCategoriesAsync(CancellationToken ct = default)
    {
        if (!Configured) return [];
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "select distinct category from public.inquiries where category is not null and category <> '' order by 1", conn);
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
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

    // 개별 hard-delete(QA9-5) — 이메일 삭제 요청 이행 수단. 감사 로그엔 id만(이메일·본문 미기록).
    public async Task DeleteInquiryAsync(Guid id, Actor actor, CancellationToken ct = default)
    {
        if (!Configured) return;
        await using var conn = await OpenAsync(ct);
        await using (var cmd = new NpgsqlCommand("delete from public.inquiries where id = @id", conn))
        {
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await LogAsync(conn, actor, "inquiry.delete", id.ToString(), null, ct);
    }
}
