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
                // 요청 로케일 게시본 우선, 없으면 en. 둘 다 없으면 404.
                await using var cmd = new NpgsqlCommand(
                    """
                    select locale, content, updated_at from public.legal_documents
                    where type = @type and published and locale in (@loc, 'en')
                    order by (locale = @loc) desc
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
                });
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });
    }
}
