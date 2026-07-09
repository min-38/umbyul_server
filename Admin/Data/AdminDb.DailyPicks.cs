using Npgsql;

namespace Admin.Data;

// 오늘의 음악(NON-154) 큐 관리. 날짜 기반 로테이션 — pick_date <= 오늘 중 최신 1건이 Discover 상단에 노출.
// 표시 메타(이름/아티스트/커버)는 저장 안 함(Api가 Spotify 상세로 해석). 어드민은 type + spotify_id + note + pick_date만.
public sealed partial class AdminDb
{
    public async Task<List<DailyPickAdminRow>> ListDailyPicksAsync(CancellationToken ct = default)
    {
        if (!Configured) return [];
        await using var conn = await OpenAsync(ct);
        for (var withYoutube = true; ; withYoutube = false)
        {
            try
            {
                var col = withYoutube ? "youtube_url" : "null::text";
                await using var cmd = new NpgsqlCommand(
                    $"select id, target_type, target_spotify_id, note, pick_date, created_at, {col} from public.daily_picks order by pick_date desc", conn);
                var list = new List<DailyPickAdminRow>();
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    list.Add(new DailyPickAdminRow(
                        r.GetGuid(0), r.GetString(1), r.GetString(2),
                        r.IsDBNull(3) ? null : r.GetString(3),
                        r.GetFieldValue<DateOnly>(4),
                        r.GetFieldValue<DateTimeOffset>(5),
                        r.IsDBNull(6) ? null : r.GetString(6)));
                return list;
            }
            catch (PostgresException e) when (withYoutube && e.SqlState == PostgresErrorCodes.UndefinedColumn)
            {
                continue; // 마이그레이션 0062 전 — youtube_url 없이 재시도
            }
        }
    }

    // 다음 노출일 기본값 = max(pick_date)+1 과 오늘 중 큰 값(과거로 안 쌓이게 = 큐 뒤에 이어붙임).
    public async Task<DateOnly> NextPickDateAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (!Configured) return today;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "select greatest(coalesce(max(pick_date) + 1, current_date), current_date) from public.daily_picks", conn);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v switch { DateOnly d => d, DateTime dt => DateOnly.FromDateTime(dt), _ => today };
    }

    public async Task<(bool Ok, string? Error)> CreateDailyPickAsync(string targetType, string spotifyId, string? note, DateOnly pickDate, string? youtubeUrl, Actor actor, CancellationToken ct = default)
    {
        if (Validate(ref targetType, ref spotifyId) is { } err) return (false, err);
        if (!Configured) return (false, "DB_NOT_CONFIGURED");
        await using var conn = await OpenAsync(ct);
        Guid newId;
        try
        {
            await using var ins = new NpgsqlCommand(
                "insert into public.daily_picks (target_type, target_spotify_id, note, pick_date, youtube_url) values (@t, @s, @n, @d, @y) returning id", conn);
            ins.Parameters.AddWithValue("t", targetType);
            ins.Parameters.AddWithValue("s", spotifyId);
            ins.Parameters.AddWithValue("n", NoteParam(note));
            ins.Parameters.AddWithValue("d", pickDate);
            ins.Parameters.AddWithValue("y", NoteParam(youtubeUrl));
            newId = (Guid)(await ins.ExecuteScalarAsync(ct))!;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return (false, "그 날짜에 이미 픽이 있습니다.");
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedColumn)
        {
            return (false, "youtube_url 컬럼이 없습니다 — 마이그레이션 0062를 적용하세요.");
        }
        await LogAsync(conn, actor, "dailypick.create", newId.ToString(), $"{pickDate:yyyy-MM-dd} · {targetType} {spotifyId}", ct);
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> UpdateDailyPickAsync(Guid id, string targetType, string spotifyId, string? note, DateOnly pickDate, string? youtubeUrl, Actor actor, CancellationToken ct = default)
    {
        if (Validate(ref targetType, ref spotifyId) is { } err) return (false, err);
        if (!Configured) return (false, "DB_NOT_CONFIGURED");
        await using var conn = await OpenAsync(ct);
        try
        {
            await using var upd = new NpgsqlCommand(
                "update public.daily_picks set target_type=@t, target_spotify_id=@s, note=@n, pick_date=@d, youtube_url=@y where id=@id", conn);
            upd.Parameters.AddWithValue("t", targetType);
            upd.Parameters.AddWithValue("s", spotifyId);
            upd.Parameters.AddWithValue("n", NoteParam(note));
            upd.Parameters.AddWithValue("d", pickDate);
            upd.Parameters.AddWithValue("y", NoteParam(youtubeUrl));
            upd.Parameters.AddWithValue("id", id);
            if (await upd.ExecuteNonQueryAsync(ct) == 0) return (false, "NOT_FOUND");
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return (false, "그 날짜에 이미 픽이 있습니다.");
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedColumn)
        {
            return (false, "youtube_url 컬럼이 없습니다 — 마이그레이션 0062를 적용하세요.");
        }
        await LogAsync(conn, actor, "dailypick.update", id.ToString(), $"{pickDate:yyyy-MM-dd} · {targetType} {spotifyId}", ct);
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> DeleteDailyPickAsync(Guid id, Actor actor, CancellationToken ct = default)
    {
        if (!Configured) return (false, "DB_NOT_CONFIGURED");
        await using var conn = await OpenAsync(ct);
        await using var del = new NpgsqlCommand("delete from public.daily_picks where id=@id", conn);
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

public sealed record DailyPickAdminRow(Guid Id, string TargetType, string SpotifyId, string? Note, DateOnly PickDate, DateTimeOffset CreatedAt, string? YoutubeUrl);
