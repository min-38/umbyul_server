using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
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
            var provided = (string?)req.Headers["X-Admin-Secret"];
            // 조기 종료 `!=`는 접두사 일치 길이에 비례한 타이밍 누출 → 고정 시간 비교(QA4-11). 시크릿 미설정 시 엔드포인트 비활성.
            if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(provided)
                || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(secret)))
                return ApiResults.Unauthorized("UNAUTHORIZED");
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

        // 게시된 공지 목록(게시일 desc, 페이지네이션). 각 공지는 요청 로케일 우선, 없으면 en/아무거나 제목.
        app.MapGet("/announcements", async (string? locale, int? offset, int? limit) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            var loc = string.IsNullOrWhiteSpace(locale) ? "en" : locale.Trim();
            var off = Math.Max(0, offset ?? 0);
            var lim = Math.Clamp(limit ?? 10, 1, 50);
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();

                int total;
                // items는 announcement_locales와 lateral join이라 로케일 0행 공지는 탈락 →
                // total도 같은 기준(로케일 1행 이상 존재)으로 세야 유령 페이지가 안 생김(QA4-7).
                await using (var c = new NpgsqlCommand(
                    """
                    select count(*) from public.announcements a
                    where a.published and exists (select 1 from public.announcement_locales l where l.announcement_id = a.id)
                    """, conn))
                    total = (int)(long)(await c.ExecuteScalarAsync())!;

                var rows = new List<(string Id, string Title, DateTimeOffset? PublishedAt)>();
                await using (var cmd = new NpgsqlCommand(
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
                    limit @lim offset @off
                    """, conn))
                {
                    cmd.Parameters.AddWithValue("loc", loc);
                    cmd.Parameters.AddWithValue("lim", lim);
                    cmd.Parameters.AddWithValue("off", off);
                    await using var r = await cmd.ExecuteReaderAsync();
                    while (await r.ReadAsync())
                        rows.Add((r.GetGuid(0).ToString(), r.GetString(1),
                            r.IsDBNull(2) ? null : r.GetFieldValue<DateTimeOffset>(2)));
                }

                // 조회 수 — best-effort(0066 미적용 시 컬럼 없음 → 0).
                var views = new Dictionary<string, int>();
                if (rows.Count > 0)
                    try
                    {
                        await using var vc = new NpgsqlCommand(
                            "select id, view_count from public.announcements where id = any(@ids)", conn);
                        vc.Parameters.AddWithValue("ids", rows.Select(x => Guid.Parse(x.Id)).ToArray());
                        await using var vr = await vc.ExecuteReaderAsync();
                        while (await vr.ReadAsync()) views[vr.GetGuid(0).ToString()] = vr.GetInt32(1);
                    }
                    catch (PostgresException) { /* view_count 컬럼 없음 → 0 */ }

                var items = rows.Select(x => new
                {
                    id = x.Id,
                    title = x.Title,
                    publishedAt = x.PublishedAt,
                    viewCount = views.GetValueOrDefault(x.Id, 0),
                });
                return ApiResults.Ok("OK", new { items, total });
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // 공지 상세. 게시된 것만, 요청 로케일 우선 en 폴백. 없으면 404. 조회수 집계는 /view(브라우저 핑)로 분리.
        app.MapGet("/announcements/{id}", async (string id, string? locale) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (!Guid.TryParse(id, out var aid)) return ApiResults.BadRequest("INVALID_TARGET");
            var loc = string.IsNullOrWhiteSpace(locale) ? "en" : locale.Trim();
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();

                (string Locale, string Title, string Body, DateTimeOffset? PublishedAt) row;
                await using (var cmd = new NpgsqlCommand(
                    """
                    select l.locale, l.title, l.body, a.published_at
                    from public.announcements a
                    join public.announcement_locales l on l.announcement_id = a.id
                    where a.id = @id and a.published
                    order by (l.locale = @loc) desc, (l.locale = 'en') desc, l.locale
                    limit 1
                    """, conn))
                {
                    cmd.Parameters.AddWithValue("id", aid);
                    cmd.Parameters.AddWithValue("loc", loc);
                    await using var r = await cmd.ExecuteReaderAsync();
                    if (!await r.ReadAsync()) return ApiResults.NotFound("NOT_FOUND");
                    row = (r.GetString(0), r.GetString(1), r.GetString(2),
                        r.IsDBNull(3) ? null : r.GetFieldValue<DateTimeOffset>(3));
                }

                int viewCount = 0;
                try
                {
                    await using var vc = new NpgsqlCommand("select view_count from public.announcements where id = @id", conn);
                    vc.Parameters.AddWithValue("id", aid);
                    viewCount = (await vc.ExecuteScalarAsync()) is int v ? v : 0;
                }
                catch (PostgresException) { /* 컬럼 없음 → 0 */ }

                return ApiResults.Ok("OK", new
                {
                    id = aid.ToString(),
                    locale = row.Locale,
                    title = row.Title,
                    body = row.Body,
                    publishedAt = row.PublishedAt,
                    viewCount,
                });
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // 조회수 집계(NON-173) — 브라우저가 직접 호출(진짜 클라이언트 IP·로그인 identity 전달). 상세가 서버 컴포넌트 SSR라
        // GET에선 뷰어를 구분할 수 없어(항상 Next 서버 IP → 전 방문자 collapse) /view로 분리. 게시 공지만, 뷰어당 최초 1회 +1.
        app.MapPost("/announcements/{id}/view", async (string id, ClaimsPrincipal user, HttpContext http) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (!Guid.TryParse(id, out var aid)) return ApiResults.BadRequest("INVALID_TARGET");
            // 로그인 → u:{sub}, 익명 → RemoteIpAddress 해시. ForwardedHeaders가 신뢰 프록시 기준으로 복원한 IP만 사용
            // (raw XFF는 클라가 스푸핑 가능 → 미신뢰, QA4-3). 브라우저 직결이라 여기서 RemoteIpAddress는 실제 방문자.
            var viewer = user.FindFirstValue("sub") is { Length: > 0 } sub && Guid.TryParse(sub, out _)
                ? $"u:{sub}"
                : $"ip:{HashIp(http)}";
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                // 게시 공지에만 기록(where exists) — 미게시 조회가 view 행을 선점 → 게시 후 첫 조회 미집계 방지(QA4-6).
                int firstView;
                await using (var v = new NpgsqlCommand(
                    """
                    insert into public.announcement_views (announcement_id, viewer)
                    select @id, @v where exists (select 1 from public.announcements where id = @id and published)
                    on conflict do nothing
                    """, conn))
                {
                    v.Parameters.AddWithValue("id", aid);
                    v.Parameters.AddWithValue("v", viewer);
                    firstView = await v.ExecuteNonQueryAsync();
                }
                if (firstView > 0)
                {
                    await using var inc = new NpgsqlCommand(
                        "update public.announcements set view_count = view_count + 1 where id = @id and published", conn);
                    inc.Parameters.AddWithValue("id", aid);
                    await inc.ExecuteNonQueryAsync();
                }
            }
            catch (PostgresException) { /* announcement_views/view_count 없음 → skip */ }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
            return ApiResults.Ok("OK", new { });
        });
    }

    // 익명 뷰어 식별자 — RemoteIpAddress를 솔트+SHA256으로 해시(원본 IP 미저장, 프라이버시). raw XFF는 미신뢰(QA4-3).
    private const string ViewSalt = "glitter-ann-view-v1";
    private static string HashIp(HttpContext http)
    {
        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(ViewSalt + ip));
        return Convert.ToHexString(bytes)[..16];
    }
}
