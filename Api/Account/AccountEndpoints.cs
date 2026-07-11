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
                string? oldUrl;
                await using (var cmd = new NpgsqlCommand(
                    """
                    with old as (select avatar_url as prev from public.users where id = @id)
                    update public.users set avatar_url = @u where id = @id
                    returning (select prev from old)
                    """, conn))
                {
                    cmd.Parameters.AddWithValue("u", avatarUrl);
                    cmd.Parameters.AddWithValue("id", uid);
                    oldUrl = await cmd.ExecuteScalarAsync(ct) as string;
                }
                // 이전 아바타(우리 R2 객체)만 삭제 — 재업로드 시 고아 방지(LEG-3). 외부(OAuth) URL·null 은 스킵.
                const string marker = "/media/avatar/";
                var idx = oldUrl?.IndexOf(marker, StringComparison.Ordinal) ?? -1;
                if (idx >= 0 && oldUrl != avatarUrl)
                {
                    var oldKey = oldUrl![(idx + marker.Length)..];
                    if (oldKey.StartsWith($"avatars/{uid}/", StringComparison.Ordinal))
                        await storage.DeleteAsync(oldKey, ct);
                }
            }
            catch (NpgsqlException)
            {
                // DB 갱신 실패 시 방금 올린 객체가 참조 없이 영구 잔존하지 않게 best-effort 정리(NON-219).
                await storage.DeleteAsync(key, ct);
                return ApiResults.ServiceUnavailable("DB_UNAVAILABLE");
            }

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
        me.MapDelete("/account", async (ClaimsPrincipal user, R2Storage storage, CancellationToken ct) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Sub(user) is not { } uid) return ApiResults.Unauthorized("UNAUTHORIZED");
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                await using var tx = await conn.BeginTransactionAsync();
                // 타인 콘텐츠 보존(DB-5): 떠나는 유저 댓글에 달린 '남의 대댓글'을 최상위로 승격 →
                // 유저 삭제 cascade(review_comments.parent_id) 가 타인 답글까지 하드삭제하는 것 방지.
                await using (var promote = new NpgsqlCommand(
                    """
                    update public.review_comments set parent_id = null
                    where user_id <> @id and parent_id in (select id from public.review_comments where user_id = @id)
                    """, conn, tx))
                {
                    promote.Parameters.AddWithValue("id", uid);
                    await promote.ExecuteNonQueryAsync();
                }
                await using (var del = new NpgsqlCommand("delete from auth.users where id = @id", conn, tx))
                {
                    del.Parameters.AddWithValue("id", uid);
                    await del.ExecuteNonQueryAsync();
                }
                await tx.CommitAsync();
                // DB 삭제 확정 후 R2 아바타 정리(GDPR 소거 — LEG-3). 실패해도 탈퇴는 성공 처리(best-effort).
                await storage.DeletePrefixAsync($"avatars/{uid}/", ct);
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
                // 내가 제공한 개인정보 전부(GDPR 열람/이동성 — LEG-4). email 은 auth.users.
                await using (var cmd = new NpgsqlCommand(
                    """
                    select u.username, au.email, u.country, u.birth_date, u.gender, u.locale, u.avatar_url, u.created_at
                    from public.users u
                    left join auth.users au on au.id = u.id
                    where u.id = @id
                    """, conn))
                {
                    cmd.Parameters.AddWithValue("id", uid);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    if (await r.ReadAsync(ct))
                        profile = new ExportProfile(
                            r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                            r.IsDBNull(3) ? null : r.GetFieldValue<DateOnly>(3).ToString("yyyy-MM-dd"),
                            r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                            r.IsDBNull(6) ? null : r.GetString(6), r.GetFieldValue<DateTimeOffset>(7));
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

                // 내 믹스·차단 목록(LEG-4). 테이블 미존재(구스키마)면 생략.
                // 믹스는 listen_url(유저 입력 URL)·트랙리스트(유저 큐레이션)까지 포함 — 데이터 이동권 완전성(QA9-3).
                var sets = new List<ExportSet>();
                var blocked = new List<string>();
                try
                {
                    // 리더 열린 채로 per-set 트랙을 못 조회하므로 세트 행을 먼저 모은 뒤 트랙을 로드.
                    var setRows = new List<(Guid Id, string Title, string? Note, string? ListenUrl, DateTimeOffset Created)>();
                    await using (var cmd = new NpgsqlCommand(
                        "select id, title, note, listen_url, created_at from public.sets where owner_id = @id and deleted_at is null order by created_at", conn))
                    {
                        cmd.Parameters.AddWithValue("id", uid);
                        await using var r = await cmd.ExecuteReaderAsync(ct);
                        while (await r.ReadAsync(ct))
                            setRows.Add((r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                                r.IsDBNull(3) ? null : r.GetString(3), r.GetFieldValue<DateTimeOffset>(4)));
                    }
                    foreach (var s in setRows)
                    {
                        var tracks = new List<ExportSetTrack>();
                        await using (var tc = new NpgsqlCommand(
                            "select spotify_id, name, artist, position from public.set_tracks where set_id = @sid order by position", conn))
                        {
                            tc.Parameters.AddWithValue("sid", s.Id);
                            await using var tr = await tc.ExecuteReaderAsync(ct);
                            while (await tr.ReadAsync(ct))
                                tracks.Add(new ExportSetTrack(tr.GetString(0), tr.GetString(1), tr.GetString(2), tr.GetInt32(3)));
                        }
                        sets.Add(new ExportSet(s.Title, s.Note, s.ListenUrl, s.Created, tracks));
                    }
                    blocked = await UsernamesAsync(conn,
                        "select u.username from public.blocks b join public.users u on u.id = b.blocked_id where b.blocker_id = @id order by u.username", uid, ct);
                }
                catch (NpgsqlException) { }

                // 믹스 댓글 — 유저가 쓴 자유 텍스트(리뷰 댓글과 동일 부류, QA9-3).
                var setComments = new List<ExportComment>();
                try
                {
                    await using var cmd = new NpgsqlCommand(
                        "select body, created_at from public.set_comments where user_id = @id and deleted_at is null order by created_at", conn);
                    cmd.Parameters.AddWithValue("id", uid);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                        setComments.Add(new ExportComment(r.GetString(0), r.GetFieldValue<DateTimeOffset>(1)));
                }
                catch (NpgsqlException) { } // set_comments 미존재 등 — 생략

                // 로그인 상태로 연 공지 열람 이력(u: 행) — Art.15/20 완전성(QA9-1). 익명 ip: 행은 본인 데이터 아님.
                var announcementViews = new List<ExportAnnouncementView>();
                try
                {
                    await using var cmd = new NpgsqlCommand(
                        """
                        select v.announcement_id, v.created_at,
                               (select l.title from public.announcement_locales l
                                where l.announcement_id = v.announcement_id order by (l.locale <> 'ko') limit 1)
                        from public.announcement_views v
                        where v.viewer = @viewer order by v.created_at
                        """, conn);
                    cmd.Parameters.AddWithValue("viewer", "u:" + uid);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                        announcementViews.Add(new ExportAnnouncementView(
                            r.GetGuid(0).ToString(), r.IsDBNull(2) ? null : r.GetString(2), r.GetFieldValue<DateTimeOffset>(1)));
                }
                catch (NpgsqlException) { } // announcement_views 미존재 등 — 생략

                return ApiResults.Ok("OK", new ExportData(
                    DateTimeOffset.UtcNow, profile, ratings, following, followers, comments, sets, blocked, announcementViews, setComments));
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // 아바타 서빙 (공개) — R2 프록시
        app.MapGet("/media/avatar/{**key}", async (string key, R2Storage storage, CancellationToken ct) =>
        {
            if (!storage.Configured) return Results.NotFound();
            // 버킷 내 아바타만 서빙 — 임의 키/경로 탈출 차단(SEC-A-8).
            if (string.IsNullOrEmpty(key) || !key.StartsWith("avatars/", StringComparison.Ordinal) || key.Contains("..")) return Results.NotFound();
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
    IReadOnlyList<string> Followers, IReadOnlyList<ExportComment> Comments,
    IReadOnlyList<ExportSet> Sets, IReadOnlyList<string> Blocked,
    IReadOnlyList<ExportAnnouncementView> AnnouncementViews,
    IReadOnlyList<ExportComment> SetComments);
public sealed record ExportProfile(
    string Username, string? Email, string? Country, string? BirthDate, string? Gender, string? Locale, string? AvatarUrl, DateTimeOffset JoinedAt);
public sealed record ExportRating(
    string TargetType, string? SpotifyId, string? Name, string? Artist,
    decimal Score, string? Review, DateTimeOffset CreatedAt);
public sealed record ExportComment(string Body, DateTimeOffset CreatedAt);
public sealed record ExportSet(string Title, string? Note, string? ListenUrl, DateTimeOffset CreatedAt, IReadOnlyList<ExportSetTrack> Tracks);
public sealed record ExportSetTrack(string SpotifyId, string Name, string Artist, int Position);
public sealed record ExportAnnouncementView(string AnnouncementId, string? Title, DateTimeOffset ViewedAt);
