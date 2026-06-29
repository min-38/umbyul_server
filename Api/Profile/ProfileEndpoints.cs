using System.Security.Claims;
using System.Text.Json;
using Api.Common;
using Npgsql;

namespace Api.Profile;

/// 프로필(users row) 조회·프로비저닝. users 쓰기는 .NET Api 가 진실 원천(NON-2 게이트웨이 결정).
/// DB 연결은 postgres 유저 → RLS 우회. 모든 엔드포인트는 로그인 필요.
public static class ProfileEndpoints
{
    public static void MapProfileEndpoints(this WebApplication app, string? dbConnString)
    {
        // 공개: username 가용성 — 회원가입(미로그인)·온보딩 공용. 핸들은 공개라 인증 불필요.
        app.MapGet("/username-available", async (string? username) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (!ProfileValidation.IsUsername(username))
                return ApiResults.Ok("OK", new { available = false, reason = "INVALID" });

            await using var conn = new NpgsqlConnection(dbConnString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "select 1 from public.users where lower(username) = lower(@u) limit 1", conn);
            cmd.Parameters.AddWithValue("u", username!);
            var taken = await cmd.ExecuteScalarAsync() is not null;
            return ApiResults.Ok("OK", new { available = !taken, reason = taken ? "TAKEN" : null });
        });

        // 공개: 이메일 가용성(실시간 중복확인). auth.users 조회 — 가입 이메일 노출(enumeration)을
        // 감수하는 제품 결정. 회원가입 화면이 미로그인이라 인증 없음.
        app.MapGet("/email-available", async (string? email) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
                return ApiResults.Ok("OK", new { available = false, reason = "INVALID" });

            await using var conn = new NpgsqlConnection(dbConnString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "select 1 from auth.users where lower(email) = lower(@e) limit 1", conn);
            cmd.Parameters.AddWithValue("e", email.Trim());
            var taken = await cmd.ExecuteScalarAsync() is not null;
            return ApiResults.Ok("OK", new { available = !taken, reason = taken ? "TAKEN" : null });
        });

        var me = app.MapGroup("/me").RequireAuthorization();

        // 내 프로필 — 없으면 404 (프론트가 온보딩으로 분기)
        me.MapGet("/profile", async (ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (user.FindFirstValue("sub") is not { Length: > 0 } id) return ApiResults.Unauthorized("UNAUTHORIZED");

            await using var conn = new NpgsqlConnection(dbConnString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "select username, country, avatar_url, is_artist, created_at from public.users where id = @id", conn);
            cmd.Parameters.AddWithValue("id", Guid.Parse(id));
            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return ApiResults.NotFound("PROFILE_NOT_FOUND");
            return ApiResults.Ok("OK", new
            {
                id,
                username = r.GetString(0),
                country = r.IsDBNull(1) ? null : r.GetString(1),
                avatarUrl = r.IsDBNull(2) ? null : r.GetString(2),
                isArtist = r.GetBoolean(3),
                createdAt = r.GetFieldValue<DateTimeOffset>(4),
            });
        });

        // 프로비저닝 — body 또는 user_metadata(이메일 가입)에서 username/country/동의 취득. username UNIQUE 보장.
        me.MapPost("/profile", async (ProvisionRequest? body, ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (user.FindFirstValue("sub") is not { Length: > 0 } id) return ApiResults.Unauthorized("UNAUTHORIZED");

            var (metaUsername, metaCountry, metaTerms) = ReadUserMetadata(user);
            var username = Coalesce(body?.Username, metaUsername);
            var country = Coalesce(body?.Country, metaCountry);
            var gender = Coalesce(body?.Gender, null);
            // 동의 신호: body(OAuth 온보딩) 또는 user_metadata(이메일 가입) 어느 쪽이든
            var consented = body?.TermsAccepted == true || metaTerms;

            if (!ProfileValidation.IsUsername(username))
                return ApiResults.BadRequest("INVALID_USERNAME");
            if (country is not null && !ProfileValidation.IsCountry(country))
                return ApiResults.BadRequest("INVALID_COUNTRY");
            if (!ProfileValidation.TryParseBirth(body?.BirthDate, out var birth))
                return ApiResults.BadRequest("INVALID_BIRTHDATE");
            if (ProfileValidation.Age(birth, DateOnly.FromDateTime(DateTime.UtcNow)) < 14)
                return ApiResults.BadRequest("UNDERAGE");
            if (gender is not null && !ProfileValidation.IsGender(gender))
                return ApiResults.BadRequest("INVALID_GENDER");
            if (!consented)
                return ApiResults.BadRequest("TERMS_REQUIRED");

            await using var conn = new NpgsqlConnection(dbConnString);
            await conn.OpenAsync();

            // 멱등: 이미 프로필 있으면 조용히 통과 (이메일 가입 자동 프로비저닝의 재호출 안전)
            await using (var exists = new NpgsqlCommand("select 1 from public.users where id = @id", conn))
            {
                exists.Parameters.AddWithValue("id", Guid.Parse(id));
                if (await exists.ExecuteScalarAsync() is not null)
                    return ApiResults.Ok("ALREADY_PROVISIONED");
            }

            try
            {
                await using var ins = new NpgsqlCommand(
                    "insert into public.users (id, username, country, birth_date, gender, terms_accepted_at) " +
                    "values (@id, @u, @c, @b, @g, now())", conn);
                ins.Parameters.AddWithValue("id", Guid.Parse(id));
                ins.Parameters.AddWithValue("u", username!);
                ins.Parameters.AddWithValue("c", (object?)country ?? DBNull.Value);
                ins.Parameters.AddWithValue("b", birth);
                ins.Parameters.AddWithValue("g", (object?)gender ?? DBNull.Value);
                await ins.ExecuteNonQueryAsync();
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return ApiResults.Conflict("USERNAME_TAKEN");
            }
            return ApiResults.Created("PROVISIONED", new { provisioned = true });
        });
    }

    // Supabase JWT 의 user_metadata 클레임(JSON 문자열)에서 username/country/동의 추출.
    private static (string? username, string? country, bool termsAccepted) ReadUserMetadata(ClaimsPrincipal user)
    {
        if (user.FindFirst("user_metadata")?.Value is not { Length: > 0 } raw) return (null, null, false);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            string? GetStr(string k) =>
                root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            bool GetBool(string k) =>
                root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
            return (GetStr("username"), GetStr("country"), GetBool("terms_accepted"));
        }
        catch (JsonException)
        {
            return (null, null, false);
        }
    }

    private static string? Coalesce(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) ? a.Trim() : (!string.IsNullOrWhiteSpace(b) ? b.Trim() : null);
}

public record ProvisionRequest(
    string? Username,
    string? Country,
    string? BirthDate,
    string? Gender,
    bool TermsAccepted);
