using Api.Common;
using Api.Storage;
using Npgsql;

namespace Api.Announcements;

/// 공개 공지사항 조회(NON-158/165). 게시된 것만, 요청 로케일 없으면 en(정본) 폴백. legal 패턴 미러.
public static class AnnouncementEndpoints
{
    private const long MaxImageBytes = 5 * 1024 * 1024; // 5MB
    // 공지 이미지 허용 타입 → 확장자.
    private static readonly Dictionary<string, string> ImageTypes = new()
    {
        ["image/jpeg"] = "jpg", ["image/png"] = "png", ["image/webp"] = "webp", ["image/gif"] = "gif",
    };

    public static void MapAnnouncementEndpoints(this WebApplication app, string? dbConnString)
    {
        // 관리자 이미지 업로드(NON-168). Admin(Blazor)↔Api 공유 시크릿 헤더로 인증(Supabase JWT 없음). R2 저장 후 프록시 URL 반환.
        app.MapPost("/admin/announcements/image", async (IFormFile? file, HttpRequest req, R2Storage storage, IConfiguration config, CancellationToken ct) =>
        {
            var secret = config["ADMIN:UPLOAD_SECRET"];
            if (string.IsNullOrEmpty(secret) || (string?)req.Headers["X-Admin-Secret"] != secret) return ApiResults.Unauthorized("UNAUTHORIZED");
            if (!storage.Configured) return ApiResults.ServiceUnavailable("STORAGE_NOT_CONFIGURED");
            if (file is null || file.Length == 0) return ApiResults.BadRequest("NO_FILE");
            if (file.Length > MaxImageBytes) return ApiResults.BadRequest("FILE_TOO_LARGE");
            if (!ImageTypes.TryGetValue(file.ContentType, out var ext)) return ApiResults.BadRequest("INVALID_FILE_TYPE");

            var key = $"announcements/{Guid.NewGuid():N}.{ext}";
            try
            {
                await using var stream = file.OpenReadStream();
                await storage.PutAsync(key, stream, file.ContentType, ct);
            }
            catch (Exception) { return ApiResults.ServiceUnavailable("UPLOAD_FAILED"); }

            return ApiResults.Ok("OK", new { url = $"{req.Scheme}://{req.Host}/media/announcement/{key}" });
        }).DisableAntiforgery();

        // 공지 이미지 서빙(공개) — R2 프록시(아바타 프록시 미러).
        app.MapGet("/media/announcement/{**key}", async (string key, R2Storage storage, CancellationToken ct) =>
        {
            if (!storage.Configured) return Results.NotFound();
            if (string.IsNullOrEmpty(key) || !key.StartsWith("announcements/", StringComparison.Ordinal) || key.Contains("..")) return Results.NotFound();
            var obj = await storage.GetAsync(key, ct);
            if (obj is null) return Results.NotFound();
            return Results.Stream(obj.Value.Content, obj.Value.ContentType);
        });

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
