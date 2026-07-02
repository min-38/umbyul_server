using Api.Common;
using Npgsql;

namespace Api.Legal;

/// 공개 약관/개인정보 조회(NON-64). 게시본만, 요청 로케일 없으면 en(정본)으로 폴백.
public static class LegalEndpoints
{
    public static void MapLegalEndpoints(this WebApplication app, string? dbConnString)
    {
        app.MapGet("/legal/{type}", async (string type, string? locale) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (type is not ("terms" or "privacy")) return ApiResults.BadRequest("INVALID_TYPE");
            var loc = string.IsNullOrWhiteSpace(locale) ? "en" : locale.Trim();
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                // 요청 로케일의 최신 게시 버전 우선, 없으면 en 최신. 둘 다 없으면 404. (NON-69/70)
                await using var cmd = new NpgsqlCommand(
                    """
                    select locale, content, published_at, version from public.legal_versions
                    where type = @type and locale in (@loc, 'en')
                    order by (locale = @loc) desc, published_at desc
                    limit 1
                    """, conn);
                cmd.Parameters.AddWithValue("type", type);
                cmd.Parameters.AddWithValue("loc", loc);
                await using var r = await cmd.ExecuteReaderAsync();
                if (!await r.ReadAsync()) return ApiResults.NotFound("NOT_PUBLISHED");
                return ApiResults.Ok("OK", new
                {
                    type,
                    locale = r.GetString(0),
                    content = r.GetString(1),
                    updatedAt = r.GetFieldValue<DateTimeOffset>(2),
                    version = r.IsDBNull(3) ? null : r.GetString(3),
                });
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });
    }
}
