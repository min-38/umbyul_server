using Api.Common;
using Npgsql;

namespace Api.Inquiry;

/// 공개 문의 접수(NON-76). 비로그인 가능. honeypot 로 간단 스팸 차단.
public static class InquiryEndpoints
{
    public sealed record InquiryRequest(string? Category, string? Email, string? Title, string? Content, string? Website);

    public static void MapInquiryEndpoints(this WebApplication app, string? dbConnString)
    {
        app.MapPost("/inquiries", async (InquiryRequest req) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");

            // honeypot: 사람은 비워두는 숨은 필드가 채워지면 봇 → 조용히 OK.
            if (!string.IsNullOrWhiteSpace(req.Website)) return ApiResults.Ok("OK");

            var email = req.Email?.Trim() ?? "";
            var title = req.Title?.Trim() ?? "";
            var content = req.Content?.Trim() ?? "";
            var category = req.Category?.Trim() ?? "";
            if (email.Length is 0 or > 254 || !email.Contains('@')) return ApiResults.BadRequest("INVALID_EMAIL");
            if (title.Length is 0 or > 200) return ApiResults.BadRequest("INVALID_TITLE");
            if (content.Length is 0 or > 5000) return ApiResults.BadRequest("INVALID_CONTENT");
            if (category.Length > 50) category = category[..50];

            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    "insert into public.inquiries (category, email, title, content) values (@cat, @e, @t, @c)", conn);
                cmd.Parameters.AddWithValue("cat", category);
                cmd.Parameters.AddWithValue("e", email);
                cmd.Parameters.AddWithValue("t", title);
                cmd.Parameters.AddWithValue("c", content);
                await cmd.ExecuteNonQueryAsync();
                return ApiResults.Ok("OK");
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });
    }
}
