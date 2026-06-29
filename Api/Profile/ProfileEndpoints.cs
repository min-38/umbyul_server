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

        // username 사용 가능 여부 (실시간 검사). 가용성은 data 로, reason 도 code 로.
        me.MapGet("/username-available", async (string? username) =>
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

        // 프로비저닝 — body 또는 user_metadata(이메일 가입)에서 username/country 취득. username UNIQUE 보장.
        me.MapPost("/profile", async (ProvisionRequest? body, ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (user.FindFirstValue("sub") is not { Length: > 0 } id) return ApiResults.Unauthorized("UNAUTHORIZED");

            var (metaUsername, metaCountry) = ReadUserMetadata(user);
            var username = Coalesce(body?.Username, metaUsername);
            var country = Coalesce(body?.Country, metaCountry);

            if (!ProfileValidation.IsUsername(username))
                return ApiResults.BadRequest("INVALID_USERNAME");
            if (country is not null && !ProfileValidation.IsCountry(country))
                return ApiResults.BadRequest("INVALID_COUNTRY");

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
                    "insert into public.users (id, username, country) values (@id, @u, @c)", conn);
                ins.Parameters.AddWithValue("id", Guid.Parse(id));
                ins.Parameters.AddWithValue("u", username!);
                ins.Parameters.AddWithValue("c", (object?)country ?? DBNull.Value);
                await ins.ExecuteNonQueryAsync();
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return ApiResults.Conflict("USERNAME_TAKEN");
            }
            return ApiResults.Created("PROVISIONED", new { provisioned = true });
        });
    }

    // Supabase JWT 의 user_metadata 클레임(JSON 문자열)에서 username/country 추출.
    private static (string? username, string? country) ReadUserMetadata(ClaimsPrincipal user)
    {
        if (user.FindFirst("user_metadata")?.Value is not { Length: > 0 } raw) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            string? Get(string k) =>
                root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return (Get("username"), Get("country"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? Coalesce(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) ? a.Trim() : (!string.IsNullOrWhiteSpace(b) ? b.Trim() : null);
}

public record ProvisionRequest(string? Username, string? Country);
