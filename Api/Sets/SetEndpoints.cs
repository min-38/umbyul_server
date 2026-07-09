using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Api.Common;
using Api.Profile;
using Npgsql;
using NpgsqlTypes;

namespace Api.Sets;

/// DJ 세트 (NON-133): 유저 큐레이션 트랙 세트. 트랙은 우리 카탈로그(spotify_id/isrc) 앵커,
/// 세트에서의 평점은 그 트랙의 일반 평점으로 흘러감. 조회 공개, 생성·수정은 로그인(본인 것만).
/// 곡·듣기 링크 모두 선택 — 링크만/노트만 세트도 가능. (스포티파이 플리 자동 임포트는 앱 토큰 제약으로 불가 → 수동 담기)
/// 곡마다 유튜브 링크(선택) — 스포티파이 없는 유저도 들을 수 있게. 한 믹스 최대 15곡.
public static class SetEndpoints
{
    public const int MaxTracks = 15;

    // 듣기 링크: null/빈값이면 통과, 값 있으면 http(s) 절대 URL이어야 함 (javascript:/data: 저장 차단 — SEC-A-6/SEC-W-1).
    private static bool ValidListenUrl(string? url) =>
        string.IsNullOrWhiteSpace(url) ||
        (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps));

    public sealed record TrackArtist(string Id, string Name);
    public sealed record SetCreateRequest(string? Title, string? Note, string? ListenUrl);
    public sealed record TrackAddRequest(
        string? SpotifyId, string? Isrc, string? Name, string? Artist, string? ImageUrl,
        string? AlbumId, string? AlbumName, List<TrackArtist>? Artists, bool Explicit = false);
    public sealed record ReorderRequest(List<string>? SpotifyIds);
    public sealed record SetCommentRequest(string? Body);
    public sealed record SetCommentItem(string Id, string UserId, string Username, string? AvatarUrl, string Body, DateTimeOffset CreatedAt, bool Edited, int Level = 1);

    public sealed record SetTrackItem(
        string SpotifyId, int Position, string? Isrc, string Name, string Artist, string? ImageUrl, string? YoutubeUrl,
        string? AlbumId, string? AlbumName, IReadOnlyList<TrackArtist> Artists, bool Explicit, double? MyScore, string? MyReview);
    public sealed record SetSummary(
        string Id, string OwnerId, string OwnerUsername, string? OwnerAvatarUrl,
        string Title, string? Note, string? ListenUrl, DateTimeOffset CreatedAt, int TrackCount, DateTimeOffset UpdatedAt,
        IReadOnlyList<string> Covers, int LikeCount, bool LikedByMe, int OwnerLevel = 1);
    public sealed record SetDetail(SetSummary Set, IReadOnlyList<SetTrackItem> Tracks);

    public static void MapSetEndpoints(this WebApplication app, string? dbConnString)
    {
        // ── 공개 조회 ──

        // 믹스 목록(전체 유저). 공개. 검색(q, 제목)·정렬(sort: newest|popular)·페이지네이션(offset/limit).
        app.MapGet("/sets", async (string? q, string? sort, int? offset, int? limit, ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            var me = Sub(user);
            var lim = Math.Clamp(limit ?? 30, 1, 50);
            var off = Math.Max(0, offset ?? 0);
            var orderBy = sort == "popular" ? "like_count desc, s.created_at desc" : "s.created_at desc";
            // 소프트삭제(관리자 테이크다운, NON-141)된 믹스는 목록에서 제외.
            var search = string.IsNullOrWhiteSpace(q) ? "" : "and s.title ilike '%' || @q || '%'";
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    $"""
                    select s.id, s.owner_id, u.username, u.avatar_url, s.title, s.note, s.listen_url, s.created_at,
                           (select count(*) from public.set_tracks t where t.set_id = s.id)::int, s.updated_at, (select array_agg(img) from (select image_url as img from public.set_tracks where set_id = s.id and image_url is not null order by position limit 2) x) as covers, (select count(*) from public.set_likes l where l.set_id = s.id)::int as like_count, (@me is not null and exists (select 1 from public.set_likes l where l.set_id = s.id and l.user_id = @me)) as liked
                    from public.sets s
                    join public.users u on u.id = s.owner_id
                    where s.deleted_at is null
                    {search}
                    order by {orderBy}
                    limit @lim offset @off
                    """, conn);
                cmd.Parameters.Add(new NpgsqlParameter("me", NpgsqlDbType.Uuid) { Value = (object?)me ?? DBNull.Value });
                cmd.Parameters.AddWithValue("q", (object?)q ?? DBNull.Value);
                cmd.Parameters.AddWithValue("lim", lim);
                cmd.Parameters.AddWithValue("off", off);
                var items = new List<SetSummary>();
                await using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                    items.Add(new SetSummary(
                        rd.GetGuid(0).ToString(), rd.GetGuid(1).ToString(), rd.GetString(2),
                        rd.IsDBNull(3) ? null : rd.GetString(3), rd.GetString(4),
                        rd.IsDBNull(5) ? null : rd.GetString(5), rd.IsDBNull(6) ? null : rd.GetString(6),
                        rd.GetFieldValue<DateTimeOffset>(7), rd.GetInt32(8), rd.GetFieldValue<DateTimeOffset>(9), rd.IsDBNull(10) ? Array.Empty<string>() : rd.GetFieldValue<string[]>(10), rd.GetInt32(11), rd.GetBoolean(12)));
                return ApiResults.Ok("OK", new { items });
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // 세트 상세(트랙 + 로그인 시 내 평점). 공개.
        app.MapGet("/sets/{id}", async (string id, ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (!Guid.TryParse(id, out var sid)) return ApiResults.BadRequest("INVALID_TARGET");
            var me = Sub(user);

            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();

                SetSummary? summary = null;
                await using (var cmd = new NpgsqlCommand(
                    """
                    select s.id, s.owner_id, u.username, u.avatar_url, s.title, s.note, s.listen_url, s.created_at,
                           (select count(*) from public.set_tracks t where t.set_id = s.id)::int, s.updated_at, (select array_agg(img) from (select image_url as img from public.set_tracks where set_id = s.id and image_url is not null order by position limit 2) x) as covers, (select count(*) from public.set_likes l where l.set_id = s.id)::int as like_count, (@me is not null and exists (select 1 from public.set_likes l where l.set_id = s.id and l.user_id = @me)) as liked
                    from public.sets s
                    join public.users u on u.id = s.owner_id
                    where s.id = @sid and s.deleted_at is null
                    """, conn))
                {
                    cmd.Parameters.AddWithValue("sid", sid);
                    cmd.Parameters.Add(new NpgsqlParameter("me", NpgsqlDbType.Uuid) { Value = (object?)me ?? DBNull.Value });
                    await using var rd = await cmd.ExecuteReaderAsync();
                    if (await rd.ReadAsync())
                        summary = new SetSummary(
                            rd.GetGuid(0).ToString(), rd.GetGuid(1).ToString(), rd.GetString(2),
                            rd.IsDBNull(3) ? null : rd.GetString(3), rd.GetString(4),
                            rd.IsDBNull(5) ? null : rd.GetString(5), rd.IsDBNull(6) ? null : rd.GetString(6),
                            rd.GetFieldValue<DateTimeOffset>(7), rd.GetInt32(8), rd.GetFieldValue<DateTimeOffset>(9), rd.IsDBNull(10) ? Array.Empty<string>() : rd.GetFieldValue<string[]>(10), rd.GetInt32(11), rd.GetBoolean(12));
                }
                if (summary is null) return ApiResults.NotFound("SET_NOT_FOUND");

                // 소유자 레벨 뱃지(NON-163).
                var ownerLevels = await LevelService.LoadLevelsAsync(conn, new[] { Guid.Parse(summary.OwnerId) }, default);
                if (ownerLevels.TryGetValue(Guid.Parse(summary.OwnerId), out var olv)) summary = summary with { OwnerLevel = olv };

                var tracks = new List<SetTrackItem>();
                await using (var cmd = new NpgsqlCommand(
                    """
                    select t.spotify_id, t.position, t.isrc, t.name, t.artist, t.image_url, null::text as youtube_url,
                           t.album_id, t.album_name, t.artists, t.explicit,
                           r.score::float8 as my_score, r.review as my_review
                    from public.set_tracks t
                    left join public.ratings r
                        on r.user_id = @me and r.target_type = 'track' and r.target_id = t.isrc and r.deleted_at is null
                    where t.set_id = @sid
                    order by t.position
                    """, conn))
                {
                    cmd.Parameters.AddWithValue("sid", sid);
                    cmd.Parameters.Add(new NpgsqlParameter("me", NpgsqlDbType.Uuid) { Value = (object?)me ?? DBNull.Value });
                    await using var rd = await cmd.ExecuteReaderAsync();
                    while (await rd.ReadAsync())
                        tracks.Add(new SetTrackItem(
                            rd.GetString(0), rd.GetInt32(1), rd.IsDBNull(2) ? null : rd.GetString(2),
                            rd.GetString(3), rd.GetString(4), rd.IsDBNull(5) ? null : rd.GetString(5),
                            rd.IsDBNull(6) ? null : rd.GetString(6),
                            rd.IsDBNull(7) ? null : rd.GetString(7), rd.IsDBNull(8) ? null : rd.GetString(8),
                            ParseArtists(rd.IsDBNull(9) ? null : rd.GetString(9)),
                            rd.GetBoolean(10),
                            rd.IsDBNull(11) ? null : rd.GetDouble(11), rd.IsDBNull(12) ? null : rd.GetString(12)));
                }

                // 트랙 YouTube는 곡 자체의 전역 링크(target_youtube_links, ISRC 기준)에서 해석 — DJ 입력 없음(NON-154).
                var isrcs = tracks.Where(t => t.Isrc is { Length: > 0 }).Select(t => t.Isrc!).Distinct().ToArray();
                if (isrcs.Length > 0)
                {
                    var yt = new Dictionary<string, string>();
                    try
                    {
                        await using var yc = new NpgsqlCommand(
                            "select target_id, youtube_url from public.target_youtube_links where target_type = 'track' and target_id = any(@ids)", conn);
                        yc.Parameters.AddWithValue("ids", isrcs);
                        await using var yr = await yc.ExecuteReaderAsync();
                        while (await yr.ReadAsync()) yt[yr.GetString(0)] = yr.GetString(1);
                    }
                    catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedTable) { /* 마이그레이션 0063 전 */ }
                    if (yt.Count > 0)
                        tracks = tracks.Select(t => t.Isrc is { } i && yt.TryGetValue(i, out var u) ? t with { YoutubeUrl = u } : t).ToList();
                }
                return ApiResults.Ok("OK", new SetDetail(summary, tracks));
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // 유저의 세트 목록(최신순). 공개.
        app.MapGet("/users/{username}/sets", async (string username, ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            var me = Sub(user);
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    """
                    select s.id, s.owner_id, u.username, u.avatar_url, s.title, s.note, s.listen_url, s.created_at,
                           (select count(*) from public.set_tracks t where t.set_id = s.id)::int, s.updated_at, (select array_agg(img) from (select image_url as img from public.set_tracks where set_id = s.id and image_url is not null order by position limit 2) x) as covers, (select count(*) from public.set_likes l where l.set_id = s.id)::int as like_count, (@me is not null and exists (select 1 from public.set_likes l where l.set_id = s.id and l.user_id = @me)) as liked
                    from public.sets s
                    join public.users u on u.id = s.owner_id
                    where lower(u.username) = lower(@un) and s.deleted_at is null
                    order by s.created_at desc
                    """, conn);
                cmd.Parameters.AddWithValue("un", username);
                cmd.Parameters.Add(new NpgsqlParameter("me", NpgsqlDbType.Uuid) { Value = (object?)me ?? DBNull.Value });
                var items = new List<SetSummary>();
                await using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                    items.Add(new SetSummary(
                        rd.GetGuid(0).ToString(), rd.GetGuid(1).ToString(), rd.GetString(2),
                        rd.IsDBNull(3) ? null : rd.GetString(3), rd.GetString(4),
                        rd.IsDBNull(5) ? null : rd.GetString(5), rd.IsDBNull(6) ? null : rd.GetString(6),
                        rd.GetFieldValue<DateTimeOffset>(7), rd.GetInt32(8), rd.GetFieldValue<DateTimeOffset>(9), rd.IsDBNull(10) ? Array.Empty<string>() : rd.GetFieldValue<string[]>(10), rd.GetInt32(11), rd.GetBoolean(12)));
                return ApiResults.Ok("OK", new { items });
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // ── 로그인 (본인 것만) ──
        var me = app.MapGroup("/me").RequireAuthorization();

        // 믹스 좋아요 토글. 반환 { liked, likeCount }.
        me.MapPost("/sets/{id}/like", async (string id, ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Sub(user) is not { } uid) return ApiResults.Unauthorized("UNAUTHORIZED");
            if (!Guid.TryParse(id, out var sid)) return ApiResults.BadRequest("INVALID_TARGET");
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                if (await Moderation.CheckAsync(conn, uid, default) is { } block) return Moderation.ToResult(block);
                bool liked;
                await using (var del = new NpgsqlCommand(
                    "delete from public.set_likes where set_id = @sid and user_id = @uid", conn))
                {
                    del.Parameters.AddWithValue("sid", sid);
                    del.Parameters.AddWithValue("uid", uid);
                    liked = await del.ExecuteNonQueryAsync() == 0; // 지운 게 없으면 새로 좋아요
                }
                if (liked)
                {
                    await using var ins = new NpgsqlCommand(
                        "insert into public.set_likes (set_id, user_id) values (@sid, @uid) on conflict do nothing", conn);
                    ins.Parameters.AddWithValue("sid", sid);
                    ins.Parameters.AddWithValue("uid", uid);
                    await ins.ExecuteNonQueryAsync();
                }
                await using var cnt = new NpgsqlCommand("select count(*)::int from public.set_likes where set_id = @sid", conn);
                cnt.Parameters.AddWithValue("sid", sid);
                var likeCount = (int)(await cnt.ExecuteScalarAsync())!;
                return ApiResults.Ok("OK", new { liked, likeCount });
            }
            catch (PostgresException ex) when (ex.SqlState == "23503") { return ApiResults.BadRequest("INVALID_TARGET"); }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // 세트 생성.
        me.MapPost("/sets", async (SetCreateRequest req, ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Sub(user) is not { } uid) return ApiResults.Unauthorized("UNAUTHORIZED");
            var title = req.Title?.Trim();
            if (string.IsNullOrEmpty(title) || title.Length > 100) return ApiResults.BadRequest("INVALID_TITLE");
            if (!ValidListenUrl(req.ListenUrl)) return ApiResults.BadRequest("INVALID_URL");

            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                if (await Moderation.CheckAsync(conn, uid, default) is { } block) return Moderation.ToResult(block);
                await using var cmd = new NpgsqlCommand(
                    """
                    insert into public.sets (owner_id, title, note, listen_url)
                    values (@o, @t, @n, @l) returning id
                    """, conn);
                cmd.Parameters.AddWithValue("o", uid);
                cmd.Parameters.AddWithValue("t", title);
                cmd.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Text) { Value = (object?)Trunc(req.Note, 500) ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("l", NpgsqlDbType.Text) { Value = (object?)Trunc(req.ListenUrl, 500) ?? DBNull.Value });
                var newId = (Guid)(await cmd.ExecuteScalarAsync())!;
                return ApiResults.Created("CREATED", new { id = newId.ToString() });
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // 세트 수정(본인) — 제목·설명·듣기 링크.
        me.MapPost("/sets/{id}/edit", async (string id, SetCreateRequest req, ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Sub(user) is not { } uid) return ApiResults.Unauthorized("UNAUTHORIZED");
            if (!Guid.TryParse(id, out var sid)) return ApiResults.BadRequest("INVALID_TARGET");
            var title = req.Title?.Trim();
            if (string.IsNullOrEmpty(title) || title.Length > 100) return ApiResults.BadRequest("INVALID_TITLE");
            if (!ValidListenUrl(req.ListenUrl)) return ApiResults.BadRequest("INVALID_URL");
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                if (await Moderation.CheckAsync(conn, uid, default) is { } block) return Moderation.ToResult(block);
                await using var cmd = new NpgsqlCommand(
                    "update public.sets set title = @t, note = @n, listen_url = @l, updated_at = now() where id = @sid and owner_id = @o", conn);
                cmd.Parameters.AddWithValue("t", title);
                cmd.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Text) { Value = (object?)Trunc(req.Note, 500) ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("l", NpgsqlDbType.Text) { Value = (object?)Trunc(req.ListenUrl, 500) ?? DBNull.Value });
                cmd.Parameters.AddWithValue("sid", sid);
                cmd.Parameters.AddWithValue("o", uid);
                if (await cmd.ExecuteNonQueryAsync() == 0) return ApiResults.NotFound("SET_NOT_FOUND");
                return ApiResults.Ok("OK");
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // 세트 삭제(본인).
        me.MapDelete("/sets/{id}", async (string id, ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Sub(user) is not { } uid) return ApiResults.Unauthorized("UNAUTHORIZED");
            if (!Guid.TryParse(id, out var sid)) return ApiResults.BadRequest("INVALID_TARGET");
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    "delete from public.sets where id = @sid and owner_id = @o", conn);
                cmd.Parameters.AddWithValue("sid", sid);
                cmd.Parameters.AddWithValue("o", uid);
                if (await cmd.ExecuteNonQueryAsync() == 0) return ApiResults.NotFound("SET_NOT_FOUND");
                return ApiResults.Ok("OK");
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // 트랙 수동 추가(검색 결과에서). 끝에 append.
        me.MapPost("/sets/{id}/tracks", async (string id, TrackAddRequest req, ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Sub(user) is not { } uid) return ApiResults.Unauthorized("UNAUTHORIZED");
            if (!Guid.TryParse(id, out var sid)) return ApiResults.BadRequest("INVALID_TARGET");
            if (string.IsNullOrEmpty(req.SpotifyId) || string.IsNullOrEmpty(req.Name) || string.IsNullOrEmpty(req.Artist))
                return ApiResults.BadRequest("INVALID_TARGET");

            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                if (await Moderation.CheckAsync(conn, uid, default) is { } block) return Moderation.ToResult(block);
                if (!await OwnsAsync(conn, sid, uid)) return ApiResults.NotFound("SET_NOT_FOUND");
                if (await CountAsync(conn, sid) >= MaxTracks) return ApiResults.BadRequest("SET_FULL");

                await using var cmd = new NpgsqlCommand(
                    """
                    insert into public.set_tracks (set_id, spotify_id, position, isrc, name, artist, image_url, album_id, album_name, artists, explicit)
                    values (@sid, @sp, (select coalesce(max(position), -1) + 1 from public.set_tracks where set_id = @sid),
                            @isrc, @name, @artist, @img, @albid, @albname, @artists, @exp)
                    on conflict (set_id, spotify_id) do nothing
                    """, conn);
                cmd.Parameters.AddWithValue("sid", sid);
                cmd.Parameters.AddWithValue("sp", req.SpotifyId);
                cmd.Parameters.Add(new NpgsqlParameter("isrc", NpgsqlDbType.Text) { Value = (object?)req.Isrc ?? DBNull.Value });
                cmd.Parameters.AddWithValue("name", Trunc(req.Name, 300)!);
                cmd.Parameters.AddWithValue("artist", Trunc(req.Artist, 300)!);
                cmd.Parameters.Add(new NpgsqlParameter("img", NpgsqlDbType.Text) { Value = (object?)req.ImageUrl ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("albid", NpgsqlDbType.Text) { Value = (object?)req.AlbumId ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("albname", NpgsqlDbType.Text) { Value = (object?)Trunc(req.AlbumName, 300) ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("artists", NpgsqlDbType.Jsonb) { Value = (object?)(req.Artists is { Count: > 0 } ? JsonSerializer.Serialize(req.Artists) : null) ?? DBNull.Value });
                cmd.Parameters.AddWithValue("exp", req.Explicit);
                await cmd.ExecuteNonQueryAsync();
                await TouchAsync(conn, sid);
                return ApiResults.Ok("OK");
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // 트랙 순서 변경(본인). 전체 spotify_id 순서를 받아 position 재기록.
        me.MapPost("/sets/{id}/order", async (string id, ReorderRequest req, ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Sub(user) is not { } uid) return ApiResults.Unauthorized("UNAUTHORIZED");
            if (!Guid.TryParse(id, out var sid)) return ApiResults.BadRequest("INVALID_TARGET");
            if (req.SpotifyIds is not { Count: > 0 } ids) return ApiResults.BadRequest("INVALID_TARGET");
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                if (await Moderation.CheckAsync(conn, uid, default) is { } block) return Moderation.ToResult(block);
                if (!await OwnsAsync(conn, sid, uid)) return ApiResults.NotFound("SET_NOT_FOUND");
                // 전체 position 재기록을 한 트랜잭션으로 — 중간 중복/갭·부분 실패 방지(LOG-A-1).
                // 유니크 제약(0056)은 deferrable initially deferred 라 커밋 시점에만 검증 → 행별 임시 중복 허용.
                await using var tx = await conn.BeginTransactionAsync();
                for (var i = 0; i < ids.Count; i++)
                {
                    await using var cmd = new NpgsqlCommand(
                        "update public.set_tracks set position = @p where set_id = @sid and spotify_id = @sp", conn, tx);
                    cmd.Parameters.AddWithValue("p", i);
                    cmd.Parameters.AddWithValue("sid", sid);
                    cmd.Parameters.AddWithValue("sp", ids[i]);
                    await cmd.ExecuteNonQueryAsync();
                }
                await TouchAsync(conn, sid, tx);
                await tx.CommitAsync();
                return ApiResults.Ok("OK");
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // 트랙 삭제.
        me.MapDelete("/sets/{id}/tracks/{spotifyId}", async (string id, string spotifyId, ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Sub(user) is not { } uid) return ApiResults.Unauthorized("UNAUTHORIZED");
            if (!Guid.TryParse(id, out var sid)) return ApiResults.BadRequest("INVALID_TARGET");
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                if (!await OwnsAsync(conn, sid, uid)) return ApiResults.NotFound("SET_NOT_FOUND");
                await using var cmd = new NpgsqlCommand(
                    "delete from public.set_tracks where set_id = @sid and spotify_id = @sp", conn);
                cmd.Parameters.AddWithValue("sid", sid);
                cmd.Parameters.AddWithValue("sp", spotifyId);
                await cmd.ExecuteNonQueryAsync();
                await TouchAsync(conn, sid);
                return ApiResults.Ok("OK");
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // 트랙 교체(수정) — 노래 자체를 바꾸거나 유튜브 링크만 바꿈. position 유지.
        me.MapPost("/sets/{id}/tracks/{spotifyId}/replace", async (string id, string spotifyId, TrackAddRequest req, ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Sub(user) is not { } uid) return ApiResults.Unauthorized("UNAUTHORIZED");
            if (!Guid.TryParse(id, out var sid)) return ApiResults.BadRequest("INVALID_TARGET");
            if (string.IsNullOrEmpty(req.SpotifyId) || string.IsNullOrEmpty(req.Name) || string.IsNullOrEmpty(req.Artist))
                return ApiResults.BadRequest("INVALID_TARGET");

            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                if (await Moderation.CheckAsync(conn, uid, default) is { } block) return Moderation.ToResult(block);
                if (!await OwnsAsync(conn, sid, uid)) return ApiResults.NotFound("SET_NOT_FOUND");

                await using var cmd = new NpgsqlCommand(
                    """
                    update public.set_tracks
                    set spotify_id = @new, isrc = @isrc, name = @name, artist = @artist, image_url = @img,
                        album_id = @albid, album_name = @albname, artists = @artists, explicit = @exp
                    where set_id = @sid and spotify_id = @old
                    """, conn);
                cmd.Parameters.AddWithValue("new", req.SpotifyId);
                cmd.Parameters.AddWithValue("old", spotifyId);
                cmd.Parameters.AddWithValue("sid", sid);
                cmd.Parameters.Add(new NpgsqlParameter("isrc", NpgsqlDbType.Text) { Value = (object?)req.Isrc ?? DBNull.Value });
                cmd.Parameters.AddWithValue("name", Trunc(req.Name, 300)!);
                cmd.Parameters.AddWithValue("artist", Trunc(req.Artist, 300)!);
                cmd.Parameters.Add(new NpgsqlParameter("img", NpgsqlDbType.Text) { Value = (object?)req.ImageUrl ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("albid", NpgsqlDbType.Text) { Value = (object?)req.AlbumId ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("albname", NpgsqlDbType.Text) { Value = (object?)Trunc(req.AlbumName, 300) ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("artists", NpgsqlDbType.Jsonb) { Value = (object?)(req.Artists is { Count: > 0 } ? JsonSerializer.Serialize(req.Artists) : null) ?? DBNull.Value });
                cmd.Parameters.AddWithValue("exp", req.Explicit);
                if (await cmd.ExecuteNonQueryAsync() == 0) return ApiResults.NotFound("SET_NOT_FOUND");
                await TouchAsync(conn, sid);
                return ApiResults.Ok("OK");
            }
            catch (PostgresException ex) when (ex.SqlState == "23505") { return ApiResults.BadRequest("DUPLICATE_TRACK"); }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // ── 믹스 댓글 ──

        // 공개: 믹스 댓글 목록(최신순).
        app.MapGet("/sets/{id}/comments", async (string id) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (!Guid.TryParse(id, out var sid)) return ApiResults.BadRequest("INVALID_TARGET");
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    """
                    select c.id, c.user_id, u.username, u.avatar_url, c.body, c.created_at, (c.edited_at is not null)
                    from public.set_comments c
                    join public.users u on u.id = c.user_id
                    where c.set_id = @sid and c.deleted_at is null
                    order by c.created_at desc
                    """, conn);
                cmd.Parameters.AddWithValue("sid", sid);
                var items = new List<SetCommentItem>();
                await using (var rd = await cmd.ExecuteReaderAsync())
                    while (await rd.ReadAsync())
                        items.Add(new SetCommentItem(
                            rd.GetGuid(0).ToString(), rd.GetGuid(1).ToString(), rd.GetString(2),
                            rd.IsDBNull(3) ? null : rd.GetString(3), rd.GetString(4), rd.GetFieldValue<DateTimeOffset>(5), rd.GetBoolean(6)));

                // 믹스 댓글 작성자 레벨 뱃지(NON-163) — 배치 1회. 실패 시 기본 Lv 1.
                var levels = await LevelService.LoadLevelsAsync(conn, items.Select(x => Guid.Parse(x.UserId)).ToList(), default);
                if (levels.Count > 0)
                    for (int i = 0; i < items.Count; i++)
                        if (levels.TryGetValue(Guid.Parse(items[i].UserId), out var lv)) items[i] = items[i] with { Level = lv };

                return ApiResults.Ok("OK", new { items });
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // 믹스 댓글 작성(로그인). 제재 유저 차단.
        me.MapPost("/sets/{id}/comments", async (string id, SetCommentRequest req, ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Sub(user) is not { } uid) return ApiResults.Unauthorized("UNAUTHORIZED");
            if (!Guid.TryParse(id, out var sid)) return ApiResults.BadRequest("INVALID_TARGET");
            var body = req.Body?.Trim();
            if (string.IsNullOrEmpty(body) || body.Length > 1000) return ApiResults.BadRequest("INVALID_BODY");

            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                if (await Moderation.CheckAsync(conn, uid, default) is { } block) return Moderation.ToResult(block);
                await using var cmd = new NpgsqlCommand(
                    """
                    with ins as (
                        insert into public.set_comments (set_id, user_id, body) values (@sid, @uid, @body)
                        returning id, user_id, body, created_at
                    )
                    select ins.id, ins.user_id, u.username, u.avatar_url, ins.body, ins.created_at
                    from ins join public.users u on u.id = ins.user_id
                    """, conn);
                cmd.Parameters.AddWithValue("sid", sid);
                cmd.Parameters.AddWithValue("uid", uid);
                cmd.Parameters.AddWithValue("body", body);
                SetCommentItem item;
                await using (var rd = await cmd.ExecuteReaderAsync())
                {
                    await rd.ReadAsync();
                    item = new SetCommentItem(
                        rd.GetGuid(0).ToString(), rd.GetGuid(1).ToString(), rd.GetString(2),
                        rd.IsDBNull(3) ? null : rd.GetString(3), rd.GetString(4), rd.GetFieldValue<DateTimeOffset>(5), false);
                }
                // 작성자 레벨 뱃지(NON-163).
                var lvls = await LevelService.LoadLevelsAsync(conn, new[] { Guid.Parse(item.UserId) }, default);
                if (lvls.TryGetValue(Guid.Parse(item.UserId), out var mylv)) item = item with { Level = mylv };
                return ApiResults.Created("CREATED", item);
            }
            catch (PostgresException ex) when (ex.SqlState == "23503") { return ApiResults.BadRequest("INVALID_TARGET"); }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // 믹스 댓글 삭제 — 본인 것만(소프트).
        me.MapDelete("/sets/{id}/comments/{commentId}", async (string id, string commentId, ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Sub(user) is not { } uid) return ApiResults.Unauthorized("UNAUTHORIZED");
            if (!Guid.TryParse(commentId, out var cid)) return ApiResults.BadRequest("INVALID_TARGET");
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    "update public.set_comments set deleted_at = now() where id = @cid and user_id = @uid and deleted_at is null", conn);
                cmd.Parameters.AddWithValue("cid", cid);
                cmd.Parameters.AddWithValue("uid", uid);
                if (await cmd.ExecuteNonQueryAsync() == 0) return ApiResults.NotFound("COMMENT_NOT_FOUND");
                return ApiResults.Ok("OK");
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // 믹스 댓글 수정 — 본인 것만.
        me.MapPost("/sets/{id}/comments/{commentId}/edit", async (string id, string commentId, SetCommentRequest req, ClaimsPrincipal user) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Sub(user) is not { } uid) return ApiResults.Unauthorized("UNAUTHORIZED");
            if (!Guid.TryParse(commentId, out var cid)) return ApiResults.BadRequest("INVALID_TARGET");
            var body = req.Body?.Trim();
            if (string.IsNullOrEmpty(body) || body.Length > 1000) return ApiResults.BadRequest("INVALID_BODY");
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync();
                if (await Moderation.CheckAsync(conn, uid, default) is { } block) return Moderation.ToResult(block);
                await using var cmd = new NpgsqlCommand(
                    "update public.set_comments set body = @body, edited_at = now() where id = @cid and user_id = @uid and deleted_at is null", conn);
                cmd.Parameters.AddWithValue("body", body);
                cmd.Parameters.AddWithValue("cid", cid);
                cmd.Parameters.AddWithValue("uid", uid);
                if (await cmd.ExecuteNonQueryAsync() == 0) return ApiResults.NotFound("COMMENT_NOT_FOUND");
                return ApiResults.Ok("OK");
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });
    }

    private static async Task<bool> OwnsAsync(NpgsqlConnection conn, Guid sid, Guid uid)
    {
        await using var cmd = new NpgsqlCommand("select exists (select 1 from public.sets where id = @sid and owner_id = @o)", conn);
        cmd.Parameters.AddWithValue("sid", sid);
        cmd.Parameters.AddWithValue("o", uid);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    // 믹스 변경 시각 갱신(곡 추가/삭제/순서/링크 편집). 활성 트랜잭션이 있으면 그 안에서.
    private static async Task TouchAsync(NpgsqlConnection conn, Guid sid, NpgsqlTransaction? tx = null)
    {
        await using var cmd = new NpgsqlCommand("update public.sets set updated_at = now() where id = @sid", conn, tx);
        cmd.Parameters.AddWithValue("sid", sid);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountAsync(NpgsqlConnection conn, Guid sid)
    {
        await using var cmd = new NpgsqlCommand("select count(*)::int from public.set_tracks where set_id = @sid", conn);
        cmd.Parameters.AddWithValue("sid", sid);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private static IReadOnlyList<TrackArtist> ParseArtists(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try { return JsonSerializer.Deserialize<List<TrackArtist>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string? Trunc(string? s, int max) =>
        string.IsNullOrEmpty(s) ? null : s.Length > max ? s[..max] : s;

    private static Guid? Sub(ClaimsPrincipal user) =>
        user.FindFirstValue("sub") is { Length: > 0 } id && Guid.TryParse(id, out var g) ? g : null;
}
