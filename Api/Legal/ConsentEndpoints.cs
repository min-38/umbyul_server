using System.Security.Claims;
using Api.Common;
using Npgsql;

namespace Api.Legal;

/// 동의 버전 연결·재동의(LEG-2/5, NON-148). 재동의 기준 = en 정본 is_current 버전.
/// user_consents 테이블 미존재(마이그레이션 0057 전)엔 fail-open — 게이트 없이 통과.
public static class ConsentEndpoints
{
    private static readonly string[] Types = ["terms", "privacy"];

    public sealed record ConsentDoc(string Type, bool Required, string? Version, DateOnly? EffectiveDate, string? Content, string? Locale);
    public sealed record ConsentRequest(string[]? Types);

    public static void MapConsentEndpoints(this WebApplication app, string? dbConnString)
    {
        var me = app.MapGroup("/me").RequireAuthorization();

        // 재동의 필요 여부 + (필요 시) 표시용 본문. 테이블 미존재 시 fail-open(required=false).
        me.MapGet("/consent-status", async (string? locale, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Me(user) is not { } meId) return ApiResults.Unauthorized("UNAUTHORIZED");
            var loc = string.IsNullOrWhiteSpace(locale) ? "en" : locale.Trim();
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync(ct);
                var docs = new List<ConsentDoc>();
                foreach (var type in Types)
                    docs.Add(await StatusForTypeAsync(conn, meId, type, loc, ct));
                return ApiResults.Ok("OK", new { required = docs.Any(d => d.Required), docs });
            }
            catch (NpgsqlException) // user_consents 미존재 등 → 게이트 없이 통과(마이그레이션 전 무크래시)
            {
                return ApiResults.Ok("OK", new { required = false, docs = Array.Empty<ConsentDoc>() });
            }
        });

        // 재동의 기록. 서버가 현재 en is_current id 로 기록(클라 spoof 방지).
        me.MapPost("/consent", async (ConsentRequest? body, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Me(user) is not { } meId) return ApiResults.Unauthorized("UNAUTHORIZED");
            var requested = (body?.Types ?? Types).Where(t => t is "terms" or "privacy").Distinct().ToArray();
            if (requested.Length == 0) return ApiResults.BadRequest("INVALID_TYPE");
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync(ct);
                foreach (var type in requested)
                    await RecordAsync(conn, meId, type, ct);
                return ApiResults.Ok("OK");
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });
    }

    private static async Task<ConsentDoc> StatusForTypeAsync(NpgsqlConnection conn, Guid meId, string type, string loc, CancellationToken ct)
    {
        // 현재 en 정본 is_current 버전 = 동의 기준(없으면 미게시 → 게이트 없음).
        Guid currentEnId;
        string? version;
        DateOnly? eff;
        await using (var cmd = new NpgsqlCommand(
            "select id, version, effective_date from public.legal_versions where type = @t and is_current and locale = 'en'", conn))
        {
            cmd.Parameters.AddWithValue("t", type);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                return new ConsentDoc(type, false, null, null, null, null);
            currentEnId = r.GetFieldValue<Guid>(0);
            version = r.IsDBNull(1) ? null : r.GetString(1);
            eff = r.IsDBNull(2) ? (DateOnly?)null : r.GetFieldValue<DateOnly>(2);
        }

        // 내 최신 동의 버전과 비교 → 다르면(또는 없으면) 재동의 필요.
        Guid? myVersion = null;
        await using (var cmd = new NpgsqlCommand(
            "select version_id from public.user_consents where user_id = @me and type = @t order by accepted_at desc limit 1", conn))
        {
            cmd.Parameters.AddWithValue("me", meId);
            cmd.Parameters.AddWithValue("t", type);
            if (await cmd.ExecuteScalarAsync(ct) is Guid g) myVersion = g;
        }
        var required = myVersion != currentEnId;

        // 표시용 본문(요청 로케일 우선, 없으면 en) — 재동의 필요할 때만 로드.
        string? content = null, contentLoc = null;
        if (required)
        {
            await using var cmd = new NpgsqlCommand(
                """
                select locale, content from public.legal_versions
                where type = @t and is_current and locale in (@loc, 'en')
                order by (locale = @loc) desc limit 1
                """, conn);
            cmd.Parameters.AddWithValue("t", type);
            cmd.Parameters.AddWithValue("loc", loc);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) { contentLoc = r.GetString(0); content = r.GetString(1); }
        }
        return new ConsentDoc(type, required, version, eff, content, contentLoc);
    }

    /// 현재 en is_current 버전으로 동의 1행 기록(미게시면 no-op). 프로비저닝·재동의 공용.
    public static async Task RecordAsync(NpgsqlConnection conn, Guid userId, string type, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            insert into public.user_consents (user_id, type, version_id)
            select @me, @t, lv.id from public.legal_versions lv
            where lv.type = @t and lv.is_current and lv.locale = 'en'
            """, conn);
        cmd.Parameters.AddWithValue("me", userId);
        cmd.Parameters.AddWithValue("t", type);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static Guid? Me(ClaimsPrincipal user) =>
        user.FindFirstValue("sub") is { Length: > 0 } id && Guid.TryParse(id, out var g) ? g : null;
}
