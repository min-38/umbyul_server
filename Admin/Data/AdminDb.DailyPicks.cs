using Npgsql;

namespace Admin.Data;

// 오늘의 음악(NON-154) 큐. 관리자는 순서(큐)만 관리 — 날짜 지정 없음.
// 매일 06:00 KST 첫 Discover 노출 시 큐 맨 앞(shown_date null, position 최소)이 그날로 소비됨(지연 소비, Api가 처리).
// 표시 메타(이름/아티스트/커버)는 저장 안 함(Api가 Spotify 상세로 해석). YouTube 링크는 '곡 링크' 페이지(target_youtube_links).
public sealed partial class AdminDb
{
    // 대기 큐(아직 노출 안 된 것) — position 순.
    public async Task<List<DailyPickAdminRow>> ListDailyPickQueueAsync(CancellationToken ct = default)
    {
        if (!Configured) return [];
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "select id, target_type, target_spotify_id, note, position, shown_date, created_at from public.daily_picks where shown_date is null order by position asc, created_at asc", conn);
        return await ReadRowsAsync(cmd, ct);
    }

    // 지난 픽(이미 노출된 것) — 최신순.
    public async Task<List<DailyPickAdminRow>> ListDailyPickHistoryAsync(int limit = 60, CancellationToken ct = default)
    {
        if (!Configured) return [];
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "select id, target_type, target_spotify_id, note, position, shown_date, created_at from public.daily_picks where shown_date is not null order by shown_date desc limit @lim", conn);
        cmd.Parameters.AddWithValue("lim", limit);
        return await ReadRowsAsync(cmd, ct);
    }

    private static async Task<List<DailyPickAdminRow>> ReadRowsAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var list = new List<DailyPickAdminRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new DailyPickAdminRow(
                r.GetGuid(0), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.GetInt32(4),
                r.IsDBNull(5) ? null : r.GetFieldValue<DateOnly>(5),
                r.GetFieldValue<DateTimeOffset>(6)));
        return list;
    }

    // 큐 맨 뒤에 추가(대기).
    public async Task<(bool Ok, string? Error)> AddDailyPickAsync(string targetType, string spotifyId, string? note, Actor actor, CancellationToken ct = default)
    {
        if (Validate(ref targetType, ref spotifyId) is { } err) return (false, err);
        if (!Configured) return (false, "DB_NOT_CONFIGURED");
        await using var conn = await OpenAsync(ct);
        Guid newId;
        await using (var ins = new NpgsqlCommand(
            """
            insert into public.daily_picks (target_type, target_spotify_id, note, position, shown_date)
            values (@t, @s, @n, (select coalesce(max(position), -1) + 1 from public.daily_picks where shown_date is null), null)
            returning id
            """, conn))
        {
            ins.Parameters.AddWithValue("t", targetType);
            ins.Parameters.AddWithValue("s", spotifyId);
            ins.Parameters.AddWithValue("n", NoteParam(note));
            newId = (Guid)(await ins.ExecuteScalarAsync(ct))!;
        }
        await LogAsync(conn, actor, "dailypick.add", newId.ToString(), $"{targetType} {spotifyId}", ct);
        return (true, null);
    }

    // 큐 내 순서 이동 — 인접 대기 항목과 position 교환. 이미 끝이면 무시.
    public async Task<(bool Ok, string? Error)> MoveDailyPickAsync(Guid id, bool up, Actor actor, CancellationToken ct = default)
    {
        if (!Configured) return (false, "DB_NOT_CONFIGURED");
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        int curPos;
        await using (var get = new NpgsqlCommand("select position from public.daily_picks where id = @id and shown_date is null", conn, tx))
        {
            get.Parameters.AddWithValue("id", id);
            if (await get.ExecuteScalarAsync(ct) is not int p) return (false, "NOT_FOUND");
            curPos = p;
        }

        // 인접 대기 항목(위=position 더 작은 것 중 최대, 아래=더 큰 것 중 최소).
        Guid neighborId;
        int neighborPos;
        var neighborSql = up
            ? "select id, position from public.daily_picks where shown_date is null and position < @p order by position desc, created_at desc limit 1"
            : "select id, position from public.daily_picks where shown_date is null and position > @p order by position asc, created_at asc limit 1";
        await using (var nb = new NpgsqlCommand(neighborSql, conn, tx))
        {
            nb.Parameters.AddWithValue("p", curPos);
            await using var r = await nb.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) { await tx.CommitAsync(ct); return (true, null); } // 이미 끝 — 무시
            neighborId = r.GetGuid(0);
            neighborPos = r.GetInt32(1);
        }

        await using (var swap = new NpgsqlCommand(
            "update public.daily_picks set position = case id when @a then @pb when @b then @pa end where id in (@a, @b)", conn, tx))
        {
            swap.Parameters.AddWithValue("a", id);
            swap.Parameters.AddWithValue("b", neighborId);
            swap.Parameters.AddWithValue("pa", curPos);
            swap.Parameters.AddWithValue("pb", neighborPos);
            await swap.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        await LogAsync(conn, actor, "dailypick.reorder", id.ToString(), up ? "up" : "down", ct);
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> DeleteDailyPickAsync(Guid id, Actor actor, CancellationToken ct = default)
    {
        if (!Configured) return (false, "DB_NOT_CONFIGURED");
        await using var conn = await OpenAsync(ct);
        await using var del = new NpgsqlCommand("delete from public.daily_picks where id = @id", conn);
        del.Parameters.AddWithValue("id", id);
        if (await del.ExecuteNonQueryAsync(ct) == 0) return (false, "NOT_FOUND");
        await LogAsync(conn, actor, "dailypick.delete", id.ToString(), null, ct);
        return (true, null);
    }

    private static object NoteParam(string? note) =>
        string.IsNullOrWhiteSpace(note) ? DBNull.Value : note.Trim();

    private static string? Validate(ref string targetType, ref string spotifyId)
    {
        targetType = targetType?.Trim() ?? "";
        spotifyId = spotifyId?.Trim() ?? "";
        if (targetType is not ("track" or "album")) return "대상 종류가 올바르지 않습니다.";
        if (spotifyId.Length == 0) return "Spotify ID를 입력하세요.";
        return null;
    }
}

public sealed record DailyPickAdminRow(Guid Id, string TargetType, string SpotifyId, string? Note, int Position, DateOnly? ShownDate, DateTimeOffset CreatedAt);
