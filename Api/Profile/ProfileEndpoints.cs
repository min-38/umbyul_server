using System.Security.Claims;
using System.Text.Json;
using Api.Common;
using Api.Legal;
using Npgsql;

namespace Api.Profile;

/// 프로필(users row) 조회·프로비저닝. users 쓰기는 .NET Api 가 진실 원천(NON-2 게이트웨이 결정).
/// DB 연결은 postgres 유저 → RLS 우회. 모든 엔드포인트는 로그인 필요.
public static class ProfileEndpoints
{
    private const int DemographicsCooldownDays = 60; // 국가/성별/생년월일 재변경 쿨다운(LEG-11) — 잦은 변경 방지

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
        }).RequireRateLimiting("email-check"); // 이메일 열거 방지: IP당 분당 제한 (SEC-A-5)

        var me = app.MapGroup("/me").RequireAuthorization();

        // 내 프로필 — 없으면 404 (프론트가 온보딩으로 분기)
        me.MapGet("/profile", async (ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (user.FindFirstValue("sub") is not { Length: > 0 } id) return ApiResults.Unauthorized("UNAUTHORIZED");

            await using var conn = new NpgsqlConnection(dbConnString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "select username, country, avatar_url, is_artist, created_at, locale from public.users where id = @id", conn);
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
                locale = r.IsDBNull(5) ? null : r.GetString(5),
            });
        });

        // 내 제재 상태(정지/영구정지) — 상단 배너 노출용(NON-55). 마이그레이션(0018) 전이면 제재 없음으로 fail-open.
        // 경고(warning)는 알림으로 전달(NON-58) — 여기서 다루지 않음.
        me.MapGet("/sanction", async (ClaimsPrincipal user) =>
        {
            object None() => new { banned = false, suspendedUntil = (DateTimeOffset?)null, reason = (string?)null };
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (user.FindFirstValue("sub") is not { Length: > 0 } id) return ApiResults.Unauthorized("UNAUTHORIZED");
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    """
                    select u.banned, u.suspended_until,
                           (select s.reason from public.user_sanctions s
                            where s.user_id = u.id and s.type in ('suspension', 'ban')
                            order by s.created_at desc limit 1) as reason
                    from public.users u where u.id = @id
                    """, conn);
                cmd.Parameters.AddWithValue("id", Guid.Parse(id));
                await using var r = await cmd.ExecuteReaderAsync();
                if (!await r.ReadAsync()) return ApiResults.Ok("OK", None());
                var banned = !r.IsDBNull(0) && r.GetBoolean(0);
                var until = r.IsDBNull(1) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(1);
                var reason = r.IsDBNull(2) ? null : r.GetString(2);
                // 만료된 정지는 제재 아님.
                if (!banned && (until is null || until <= DateTimeOffset.UtcNow)) return ApiResults.Ok("OK", None());
                return ApiResults.Ok("OK", new { banned, suspendedUntil = banned ? (DateTimeOffset?)null : until, reason });
            }
            catch (NpgsqlException) { return ApiResults.Ok("OK", None()); } // 컬럼 미존재 등 — 통과
        });

        // 프로비저닝 — body 또는 user_metadata(이메일 가입)에서 username/country/동의 취득. username UNIQUE 보장.
        me.MapPost("/profile", async (ProvisionRequest? body, ClaimsPrincipal user, CancellationToken ct) =>
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

            // 가입 동의를 현재 게시 버전에 연결(LEG-2/5, NON-148). 테이블 미존재(마이그레이션 전)면 무시 — terms_accepted_at 폴백.
            try
            {
                await ConsentEndpoints.RecordAsync(conn, Guid.Parse(id), "terms", ct);
                await ConsentEndpoints.RecordAsync(conn, Guid.Parse(id), "privacy", ct);
            }
            catch (NpgsqlException) { /* user_consents 미존재 등 — 게이트 없이 통과 */ }

            return ApiResults.Created("PROVISIONED", new { provisioned = true });
        });

        // 국가/성별/생년월일 정정용 조회(LEG-11). canChangeAt=null 이면 즉시 변경 가능, 아니면 그 시각 이후 가능.
        me.MapGet("/demographics", async (ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (user.FindFirstValue("sub") is not { Length: > 0 } id) return ApiResults.Unauthorized("UNAUTHORIZED");
            await using var conn = new NpgsqlConnection(dbConnString);
            await conn.OpenAsync(ct);
            try
            {
                await using var cmd = new NpgsqlCommand(
                    "select country, gender, birth_date, demographics_updated_at from public.users where id = @id", conn);
                cmd.Parameters.AddWithValue("id", Guid.Parse(id));
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return ApiResults.NotFound("PROFILE_NOT_FOUND");
                var updated = r.IsDBNull(3) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(3);
                return ApiResults.Ok("OK", new
                {
                    country = r.IsDBNull(0) ? null : r.GetString(0),
                    gender = r.IsDBNull(1) ? null : r.GetString(1),
                    birthDate = r.IsDBNull(2) ? (DateOnly?)null : r.GetFieldValue<DateOnly>(2),
                    canChangeAt = updated?.AddDays(DemographicsCooldownDays),
                });
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedColumn)
            {
                // demographics_updated_at 미존재(마이그레이션 0058 전) — 쿨다운 없이 country/gender/birth_date만
                await using var cmd = new NpgsqlCommand("select country, gender, birth_date from public.users where id = @id", conn);
                cmd.Parameters.AddWithValue("id", Guid.Parse(id));
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return ApiResults.NotFound("PROFILE_NOT_FOUND");
                return ApiResults.Ok("OK", new
                {
                    country = r.IsDBNull(0) ? null : r.GetString(0),
                    gender = r.IsDBNull(1) ? null : r.GetString(1),
                    birthDate = r.IsDBNull(2) ? (DateOnly?)null : r.GetFieldValue<DateOnly>(2),
                    canChangeAt = (DateTimeOffset?)null,
                });
            }
        });

        // 국가/성별/생년월일 정정(GDPR Art.16, LEG-11). 셋을 한 번에 갱신, 쿨다운 내 재변경은 거부(DEMOGRAPHICS_COOLDOWN).
        me.MapPost("/demographics", async (DemographicsRequest? body, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (user.FindFirstValue("sub") is not { Length: > 0 } id) return ApiResults.Unauthorized("UNAUTHORIZED");
            var country = body?.Country?.Trim();
            var gender = string.IsNullOrWhiteSpace(body?.Gender) ? null : body!.Gender!.Trim();
            if (!ProfileValidation.IsCountry(country)) return ApiResults.BadRequest("INVALID_COUNTRY");
            if (gender is not null && !ProfileValidation.IsGender(gender)) return ApiResults.BadRequest("INVALID_GENDER");
            if (!ProfileValidation.TryParseBirth(body?.BirthDate, out var birth)) return ApiResults.BadRequest("INVALID_BIRTHDATE");
            if (ProfileValidation.Age(birth, DateOnly.FromDateTime(DateTime.UtcNow)) < 14) return ApiResults.BadRequest("UNDERAGE");

            await using var conn = new NpgsqlConnection(dbConnString);
            await conn.OpenAsync(ct);
            try
            {
                await using (var chk = new NpgsqlCommand("select demographics_updated_at from public.users where id = @id", conn))
                {
                    chk.Parameters.AddWithValue("id", Guid.Parse(id));
                    if (await chk.ExecuteScalarAsync(ct) is DateTimeOffset last && DateTimeOffset.UtcNow < last.AddDays(DemographicsCooldownDays))
                        return ApiResults.BadRequest("DEMOGRAPHICS_COOLDOWN");
                }
                await using var upd = new NpgsqlCommand(
                    "update public.users set country = @c, gender = @g, birth_date = @b, demographics_updated_at = now() where id = @id", conn);
                upd.Parameters.AddWithValue("id", Guid.Parse(id));
                upd.Parameters.AddWithValue("c", country!);
                upd.Parameters.AddWithValue("g", (object?)gender ?? DBNull.Value);
                upd.Parameters.AddWithValue("b", birth);
                await upd.ExecuteNonQueryAsync(ct);
                return ApiResults.Ok("OK");
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedColumn)
            {
                // 마이그레이션 0058 전 — 쿨다운 없이 갱신(정정 자체는 되게)
                await using var upd = new NpgsqlCommand("update public.users set country = @c, gender = @g, birth_date = @b where id = @id", conn);
                upd.Parameters.AddWithValue("id", Guid.Parse(id));
                upd.Parameters.AddWithValue("c", country!);
                upd.Parameters.AddWithValue("g", (object?)gender ?? DBNull.Value);
                upd.Parameters.AddWithValue("b", birth);
                await upd.ExecuteNonQueryAsync(ct);
                return ApiResults.Ok("OK");
            }
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

public record DemographicsRequest(string? Country, string? Gender, string? BirthDate);
