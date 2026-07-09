using Api.Common;
using Npgsql;

namespace Api.Announcements;

/// 공개 공지사항 조회(NON-158/165). 게시된 것만, 요청 로케일 없으면 en(정본) 폴백. legal 패턴 미러.
public static class AnnouncementEndpoints
{
    public static void MapAnnouncementEndpoints(this WebApplication app, string? dbConnString)
    {
        // 게시된 공지 목록(최신순). 각 공지는 요청 로케일 우선, 없으면 en 제목.
        app.MapGet("/announcements", async (string? locale) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            var loc = string.IsNullOrWhiteSpace(locale) ? "en" : locale.Trim();
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    """
                    select a.id, l.title, a.published_at
                    from public.announcements a
                    join lateral (
                        select title from public.announcement_locales l
                        where l.announcement_id = a.id
                        order by (l.locale = @loc) desc, (l.locale = 'en') desc, l.locale
                        limit 1
                    ) l on true
                    where a.published
                    order by a.published_at desc nulls last
                    limit 50
                    """, conn);
                cmd.Parameters.AddWithValue("loc", loc);
                var items = new List<object>();
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    items.Add(new
                    {
                        id = r.GetGuid(0).ToString(),
                        title = r.GetString(1),
                        publishedAt = r.IsDBNull(2) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(2),
                    });
                return ApiResults.Ok("OK", new { items });
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // 공지 상세. 게시된 것만, 요청 로케일 우선 en 폴백. 없으면 404.
        app.MapGet("/announcements/{id}", async (string id, string? locale) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (!Guid.TryParse(id, out var aid)) return ApiResults.BadRequest("INVALID_TARGET");
            var loc = string.IsNullOrWhiteSpace(locale) ? "en" : locale.Trim();
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    """
                    select l.locale, l.title, l.body, a.published_at
                    from public.announcements a
                    join public.announcement_locales l on l.announcement_id = a.id
                    where a.id = @id and a.published
                    order by (l.locale = @loc) desc, (l.locale = 'en') desc, l.locale
                    limit 1
                    """, conn);
                cmd.Parameters.AddWithValue("id", aid);
                cmd.Parameters.AddWithValue("loc", loc);
                await using var r = await cmd.ExecuteReaderAsync();
                if (!await r.ReadAsync()) return ApiResults.NotFound("NOT_FOUND");
                return ApiResults.Ok("OK", new
                {
                    id = aid.ToString(),
                    locale = r.GetString(0),
                    title = r.GetString(1),
                    body = r.GetString(2),
                    publishedAt = r.IsDBNull(3) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(3),
                });
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });
    }
}
