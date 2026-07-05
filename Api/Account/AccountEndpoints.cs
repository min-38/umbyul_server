using System.Security.Claims;
using Api.Common;
using Api.Profile;
using Api.Storage;
using Npgsql;

namespace Api.Account;

/// 계정 설정 (NON-30). 아바타(R2), 닉네임 변경, 회원 탈퇴. 비밀번호는 프론트(Supabase) 담당.
public static class AccountEndpoints
{
    public const long MaxAvatarBytes = 5 * 1024 * 1024;
    private static readonly Dictionary<string, string> AvatarTypes = new()
    {
        ["image/jpeg"] = "jpg",
        ["image/png"] = "png",
        ["image/webp"] = "webp",
    };

    public sealed record UsernameRequest(string? Username);
    public sealed record LocaleRequest(string? Locale);

    public static void MapAccountEndpoints(this WebApplication app, string? dbConnString)
    {
        var me = app.MapGroup("/me").RequireAuthorization();

        // 아바타 업로드 → R2 → avatar_url 갱신
        me.MapPost("/avatar", async (IFormFile? file, ClaimsPrincipal user, R2Storage storage, HttpRequest req, CancellationToken ct) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (!storage.Configured) return ApiResults.ServiceUnavailable("STORAGE_NOT_CONFIGURED");
            if (Sub(user) is not { } uid) return ApiResults.Unauthorized("UNAUTHORIZED");
            if (file is null || file.Length == 0) return ApiResults.BadRequest("NO_FILE");
            if (file.Length > MaxAvatarBytes) return ApiResults.BadRequest("FILE_TOO_LARGE");
            if (!AvatarTypes.TryGetValue(file.ContentType, out var ext)) return ApiResults.BadRequest("INVALID_FILE_TYPE");

            var key = $"avatars/{uid}/{Guid.NewGuid():N}.{ext}";
            try
            {
                await using (var stream = file.OpenReadStream())
                    await storage.PutAsync(key, stream, file.ContentType, ct);
            }
            catch (Exception)
            {
                return ApiResults.ServiceUnavailable("UPLOAD_FAILED");
            }

            var avatarUrl = $"{req.Scheme}://{req.Host}/media/avatar/{key}";
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand("update public.users set avatar_url = @u where id = @id", conn);
                cmd.Parameters.AddWithValue("u", avatarUrl);
                cmd.Parameters.AddWithValue("id", uid);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }

            return ApiResults.Ok("OK", new { avatarUrl });
        }).DisableAntiforgery();

        // 닉네임(username) 변경
        me.MapPost("/username", async (UsernameRequest body, ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Sub(user) is not { } uid) return ApiResults.Unauthorized("UNAUTHORIZED");
            if (!ProfileValidation.IsUsername(body.Username)) return ApiResults.BadRequest("INVALID_USERNAME");

            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                await using (var dup = new NpgsqlCommand(
                    "select 1 from public.users where lower(username) = lower(@u) and id <> @id limit 1", conn))
                {
                    dup.Parameters.AddWithValue("u", body.Username!);
                    dup.Parameters.AddWithValue("id", uid);
                    if (await dup.ExecuteScalarAsync() is not null) return ApiResults.Conflict("USERNAME_TAKEN");
                }
                await using var cmd = new NpgsqlCommand("update public.users set username = @u where id = @id", conn);
                cmd.Parameters.AddWithValue("u", body.Username!);
                cmd.Parameters.AddWithValue("id", uid);
                await cmd.ExecuteNonQueryAsync();
                return ApiResults.Ok("OK", new { username = body.Username });
            }
            catch (PostgresException ex) when (ex.SqlState == "23505") { return ApiResults.Conflict("USERNAME_TAKEN"); }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // 표시 언어(locale) 저장 — 회원의 기기 무관 언어 설정(NON-39)
        me.MapPost("/locale", async (LocaleRequest body, ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Sub(user) is not { } uid) return ApiResults.Unauthorized("UNAUTHORIZED");
            if (body.Locale is not ("ko" or "en" or "ja" or "es")) return ApiResults.BadRequest("INVALID_LOCALE");
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand("update public.users set locale = @l where id = @id", conn);
                cmd.Parameters.AddWithValue("l", body.Locale);
                cmd.Parameters.AddWithValue("id", uid);
                await cmd.ExecuteNonQueryAsync();
                return ApiResults.Ok("OK", new { locale = body.Locale });
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // 회원 탈퇴 — auth.users 삭제 → public.users 등 cascade
        me.MapDelete("/account", async (ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Sub(user) is not { } uid) return ApiResults.Unauthorized("UNAUTHORIZED");
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand("delete from auth.users where id = @id", conn);
                cmd.Parameters.AddWithValue("id", uid);
                await cmd.ExecuteNonQueryAsync();
                return ApiResults.Ok("OK");
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // 내 데이터 내보내기(NON-111) — 규정 대응. 내 프로필·평가·팔로우·댓글을 JSON 으로.
        me.MapGet("/export", async (ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Sub(user) is not { } uid) return ApiResults.Unauthorized("UNAUTHORIZED");
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync(ct);

                ExportProfile? profile = null;
                await using (var cmd = new NpgsqlCommand(
                    "select username, country, created_at from public.users where id = @id", conn))
                {
                    cmd.Parameters.AddWithValue("id", uid);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    if (await r.ReadAsync(ct))
                        profile = new ExportProfile(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetFieldValue<DateTimeOffset>(2));
                }

                var ratings = new List<ExportRating>();
                await using (var cmd = new NpgsqlCommand(
                    """
                    select target_type, target_spotify_id, target_name, target_artist, score, review, created_at
                    from public.ratings where user_id = @id and deleted_at is null order by created_at
                    """, conn))
                {
                    cmd.Parameters.AddWithValue("id", uid);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                        ratings.Add(new ExportRating(
                            r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
                            r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                            r.GetDecimal(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetFieldValue<DateTimeOffset>(6)));
                }

                var following = await UsernamesAsync(conn,
                    "select u.username from public.follows f join public.users u on u.id = f.following_id where f.follower_id = @id order by u.username", uid, ct);
                var followers = await UsernamesAsync(conn,
                    "select u.username from public.follows f join public.users u on u.id = f.follower_id where f.following_id = @id order by u.username", uid, ct);

                var comments = new List<ExportComment>();
                try
                {
                    await using var cmd = new NpgsqlCommand(
                        "select body, created_at from public.review_comments where user_id = @id and deleted_at is null order by created_at", conn);
                    cmd.Parameters.AddWithValue("id", uid);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                        comments.Add(new ExportComment(r.GetString(0), r.GetFieldValue<DateTimeOffset>(1)));
                }
                catch (NpgsqlException) { } // 댓글 테이블 미존재/컬럼 상이 — 생략

                return ApiResults.Ok("OK", new ExportData(
                    DateTimeOffset.UtcNow, profile, ratings, following, followers, comments));
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // 아바타 서빙 (공개) — R2 프록시
        app.MapGet("/media/avatar/{**key}", async (string key, R2Storage storage, CancellationToken ct) =>
        {
            if (!storage.Configured) return Results.NotFound();
            var obj = await storage.GetAsync(key, ct);
            if (obj is null) return Results.NotFound();
            return Results.Stream(obj.Value.Content, obj.Value.ContentType);
        });
    }

    private static Guid? Sub(ClaimsPrincipal user) =>
        user.FindFirstValue("sub") is { Length: > 0 } id && Guid.TryParse(id, out var g) ? g : null;

    private static async Task<List<string>> UsernamesAsync(NpgsqlConnection conn, string sql, Guid uid, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", uid);
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    }
}

// 데이터 내보내기(NON-111) 스키마.
public sealed record ExportData(
    DateTimeOffset ExportedAt, ExportProfile? Profile,
    IReadOnlyList<ExportRating> Ratings, IReadOnlyList<string> Following,
    IReadOnlyList<string> Followers, IReadOnlyList<ExportComment> Comments);
public sealed record ExportProfile(string Username, string? Country, DateTimeOffset JoinedAt);
public sealed record ExportRating(
    string TargetType, string? SpotifyId, string? Name, string? Artist,
    decimal Score, string? Review, DateTimeOffset CreatedAt);
public sealed record ExportComment(string Body, DateTimeOffset CreatedAt);
