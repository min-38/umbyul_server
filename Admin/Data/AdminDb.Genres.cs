using Npgsql;

namespace Admin.Data;

// AdminDb 장르 사전 관리(NON-123). 큐레이트 장르 CRUD + 상위 지정. 장르명 영어 단일(name), 상위→하위 2단계.
// slug는 name에서 자동 생성(안정 식별자라 편집 대상 아님). 삭제 시 하위 있으면 차단, 태그는 FK cascade.
public sealed partial class AdminDb
{
    public async Task<List<GenreAdminRow>> ListGenresAsync(CancellationToken ct = default)
    {
        if (!Configured) return [];
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            select g.id, g.slug, g.name, g.parent_id, p.name, g.sort_order,
                   (select count(*) from public.genre_tags t where t.genre_id = g.id)
            from public.genres g
            left join public.genres p on p.id = g.parent_id
            order by g.sort_order, g.id
            """, conn);
        var list = new List<GenreAdminRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new GenreAdminRow(
                r.GetInt32(0), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetInt32(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.GetInt32(5), (int)r.GetInt64(6)));
        return list;
    }

    /// 장르 추가. slug 자동 생성. 상위 지정 시 최상위 장르만 허용(2단계 유지). 반환: (성공, 실패 사유).
    public async Task<(bool Ok, string? Error)> CreateGenreAsync(string name, int? parentId, int sortOrder, Actor actor, CancellationToken ct = default)
    {
        if (!Configured) return (false, "DB_NOT_CONFIGURED");
        name = name.Trim();
        if (name.Length == 0) return (false, "장르명을 입력하세요.");
        await using var conn = await OpenAsync(ct);

        if (parentId is { } pid && !await IsTopLevelAsync(conn, pid, ct))
            return (false, "상위 장르가 유효하지 않습니다.");

        var slug = await UniqueSlugAsync(conn, Slugify(name), ct);
        int newId;
        await using (var ins = new NpgsqlCommand(
            "insert into public.genres (slug, name, parent_id, sort_order) values (@s, @n, @p, @o) returning id", conn))
        {
            ins.Parameters.AddWithValue("s", slug);
            ins.Parameters.AddWithValue("n", name);
            ins.Parameters.AddWithValue("p", (object?)parentId ?? DBNull.Value);
            ins.Parameters.AddWithValue("o", sortOrder);
            newId = (int)(await ins.ExecuteScalarAsync(ct))!;
        }
        await LogAsync(conn, actor, "genre.create", newId.ToString(), $"{slug} · {name}", ct);
        return (true, null);
    }

    /// 장르 수정(name·parent·sort). slug는 안정 식별자라 유지. 하위 있는 장르는 다른 장르 밑으로 못 옮김(2단계).
    public async Task<(bool Ok, string? Error)> UpdateGenreAsync(int id, string name, int? parentId, int sortOrder, Actor actor, CancellationToken ct = default)
    {
        if (!Configured) return (false, "DB_NOT_CONFIGURED");
        name = name.Trim();
        if (name.Length == 0) return (false, "장르명을 입력하세요.");
        if (parentId == id) return (false, "자기 자신을 상위로 지정할 수 없습니다.");
        await using var conn = await OpenAsync(ct);

        if (parentId is { } pid)
        {
            if (await HasChildrenAsync(conn, id, ct))
                return (false, "하위 장르가 있어 다른 장르 밑으로 옮길 수 없습니다.");
            if (!await IsTopLevelAsync(conn, pid, ct))
                return (false, "상위 장르가 유효하지 않습니다.");
        }

        await using (var upd = new NpgsqlCommand(
            "update public.genres set name = @n, parent_id = @p, sort_order = @o where id = @id", conn))
        {
            upd.Parameters.AddWithValue("n", name);
            upd.Parameters.AddWithValue("p", (object?)parentId ?? DBNull.Value);
            upd.Parameters.AddWithValue("o", sortOrder);
            upd.Parameters.AddWithValue("id", id);
            if (await upd.ExecuteNonQueryAsync(ct) == 0) return (false, "NOT_FOUND");
        }
        await LogAsync(conn, actor, "genre.update", id.ToString(), name, ct);
        return (true, null);
    }

    /// 장르 삭제. 하위 장르가 있으면 차단(고아 방지). 태그(genre_tags)는 FK on delete cascade 로 함께 삭제.
    public async Task<(bool Ok, string? Error)> DeleteGenreAsync(int id, Actor actor, CancellationToken ct = default)
    {
        if (!Configured) return (false, "DB_NOT_CONFIGURED");
        await using var conn = await OpenAsync(ct);

        if (await HasChildrenAsync(conn, id, ct))
            return (false, "하위 장르가 있어 삭제할 수 없습니다. 먼저 하위 장르를 옮기거나 지우세요.");

        await using (var del = new NpgsqlCommand("delete from public.genres where id = @id", conn))
        {
            del.Parameters.AddWithValue("id", id);
            if (await del.ExecuteNonQueryAsync(ct) == 0) return (false, "NOT_FOUND");
        }
        await LogAsync(conn, actor, "genre.delete", id.ToString(), null, ct);
        return (true, null);
    }

    private static async Task<bool> IsTopLevelAsync(NpgsqlConnection conn, int id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("select parent_id is null from public.genres where id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteScalarAsync(ct) is true; // 없으면 null → false
    }

    private static async Task<bool> HasChildrenAsync(NpgsqlConnection conn, int id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("select exists(select 1 from public.genres where parent_id = @id)", conn);
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    // 영문/숫자만 남긴 소문자 slug. 충돌 시 접미 숫자.
    private static string Slugify(string name)
    {
        var s = new string(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return s.Length == 0 ? "genre" : s;
    }

    private static async Task<string> UniqueSlugAsync(NpgsqlConnection conn, string baseSlug, CancellationToken ct)
    {
        var slug = baseSlug;
        for (var i = 2; ; i++)
        {
            await using var cmd = new NpgsqlCommand("select 1 from public.genres where slug = @s", conn);
            cmd.Parameters.AddWithValue("s", slug);
            if (await cmd.ExecuteScalarAsync(ct) is null) return slug;
            slug = $"{baseSlug}{i}";
        }
    }
}

public sealed record GenreAdminRow(int Id, string Slug, string Name, int? ParentId, string? ParentName, int SortOrder, int TagCount);
