using Api.Common;
using Npgsql;

namespace Api.Faq;

/// 공개 FAQ 조회(NON-73). 게시 항목만, 요청 로케일에 게시본 없으면 en(정본)으로 폴백.
public static class FaqEndpoints
{
    public static void MapFaqEndpoints(this WebApplication app, string? dbConnString)
    {
        app.MapGet("/faq", async (string? locale) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            var loc = string.IsNullOrWhiteSpace(locale) ? "en" : locale.Trim();
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                // 요청 로케일에 게시 항목이 있으면 그 로케일, 없으면 en.
                await using var cmd = new NpgsqlCommand(
                    """
                    select category, question, answer from public.faq_items
                    where published and locale = case
                        when exists(select 1 from public.faq_items where published and locale = @loc) then @loc else 'en' end
                    order by sort_order, category
                    """, conn);
                cmd.Parameters.AddWithValue("loc", loc);
                var items = new List<object>();
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    items.Add(new { category = r.GetString(0), question = r.GetString(1), answer = r.GetString(2) });
                return ApiResults.Ok("OK", new { items });
            }
            catch (NpgsqlException) { return ApiResults.Ok("OK", new { items = Array.Empty<object>() }); }
        });
    }
}
