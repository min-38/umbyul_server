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
    // 과거 버전 내용 불러오기(롤백 의도). 감사 로그 남김(NON-103) — 실제 공개 반영은 재게시(legal.publish).
    public async Task<string?> GetLegalVersionContentAsync(Guid versionId, Actor actor, CancellationToken ct = default)
    {
        if (!Configured) return null;
        await using var conn = await OpenAsync(ct);
        string? content;
        await using (var cmd = new NpgsqlCommand("select content from public.legal_versions where id = @id", conn))
        {
            cmd.Parameters.AddWithValue("id", versionId);
            content = await cmd.ExecuteScalarAsync(ct) as string;
        }
        if (content is not null)
            await LogAsync(conn, actor, "legal.version_load", versionId.ToString(), null, ct);
        return content;
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
}
