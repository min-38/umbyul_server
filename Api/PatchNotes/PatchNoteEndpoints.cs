using Api.Common;
using Npgsql;

namespace Api.PatchNotes;

/// 공개 패치노트 조회(NON-159/169). 게시된 것만, 요청 로케일→en→아무거나 폴백. 공지사항 패턴 미러.
public static class PatchNoteEndpoints
{
    public static void MapPatchNoteEndpoints(this WebApplication app, string? dbConnString)
    {
        // 게시된 패치노트. 작업 중(in_progress) 먼저, 그 뒤 릴리스(release_at desc). 본문은 로케일 폴백.
        app.MapGet("/patch-notes", async (string? locale) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            var loc = string.IsNullOrWhiteSpace(locale) ? "en" : locale.Trim();
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    """
                    select p.id, p.version, p.status, p.released_at, l.body
                    from public.patch_notes p
                    join lateral (
                        select body from public.patch_note_locales l
                        where l.patch_note_id = p.id
                        order by (l.locale = @loc) desc, (l.locale = 'en') desc, l.locale
                        limit 1
                    ) l on true
                    where p.published
                    order by (p.status = 'in_progress') desc, p.released_at desc nulls last, p.created_at desc
                    limit 100
                    """, conn);
                cmd.Parameters.AddWithValue("loc", loc);
                var items = new List<object>();
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    items.Add(new
                    {
                        id = r.GetGuid(0).ToString(),
                        version = r.GetString(1),
                        status = r.GetString(2),
                        releasedAt = r.IsDBNull(3) ? (DateOnly?)null : r.GetFieldValue<DateOnly>(3),
                        body = r.GetString(4),
                    });
                return ApiResults.Ok("OK", new { items });
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });
    }
}
