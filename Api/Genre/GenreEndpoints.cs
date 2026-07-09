using System.Security.Claims;
using Api.Common;
using Npgsql;

namespace Api.Genre;

/// 유저 장르 태깅 (NON-122). 큐레이트 목록 + 투표(복수 장르). 상위 장르만 노출.
/// 목록·집계는 공개, 태깅은 로그인 + 제재 게이팅. 라이선스 0 — 전부 우리 소유 데이터.
public static class GenreEndpoints
{
    public sealed record GenreItem(int Id, string Slug, string Name, int? ParentId, int SortOrder);
    public sealed record GenreCount(int Id, string Name, string? ParentName, int Count);
    public sealed record GenresForData(IReadOnlyList<GenreCount> Top, IReadOnlyList<int> Mine);
    public sealed record GenreTagRequest(string? TargetType, string? SpotifyId, int GenreId);

    public static void MapGenreEndpoints(this WebApplication app, string? dbConnString)
    {
        // 큐레이트 장르 목록(공개). 장르명은 영어 단일(name) — i18n 불필요.
        app.MapGet("/genres", async (CancellationToken ct) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand(
                    "select id, slug, name, parent_id, sort_order from public.genres order by sort_order, id", conn);
                var list = new List<GenreItem>();
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    list.Add(new GenreItem(r.GetInt32(0), r.GetString(1), r.GetString(2),
                        r.IsDBNull(3) ? null : r.GetInt32(3), r.GetInt32(4)));
                return ApiResults.Ok("OK", list);
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // 대상의 상위 장르(투표수) + 로그인 시 내 선택. 공개(옵셔널 인증).
        app.MapGet("/genres/for", async (string? type, string? id, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (type is not ("track" or "album") || string.IsNullOrWhiteSpace(id))
                return ApiResults.BadRequest("INVALID_TARGET");
            var me = Me(user);
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync(ct);

                var top = new List<GenreCount>();
                await using (var cmd = new NpgsqlCommand(
                    """
                    select g.id, g.name, p.name, count(*) n
                    from public.genre_tags t
                    join public.genres g on g.id = t.genre_id
                    left join public.genres p on p.id = g.parent_id
                    where t.target_type = @tt and t.target_spotify_id = @id
                    group by g.id, g.name, p.name
                    order by n desc, g.sort_order
                    limit 12
                    """, conn))
                {
                    cmd.Parameters.AddWithValue("tt", type);
                    cmd.Parameters.AddWithValue("id", id);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                        top.Add(new GenreCount(r.GetInt32(0), r.GetString(1),
                            r.IsDBNull(2) ? null : r.GetString(2), (int)r.GetInt64(3)));
                }

                var mine = new List<int>();
                if (me is { } uid)
                {
                    await using var cmd = new NpgsqlCommand(
                        "select genre_id from public.genre_tags where user_id = @me and target_type = @tt and target_spotify_id = @id", conn);
                    cmd.Parameters.AddWithValue("me", uid);
                    cmd.Parameters.AddWithValue("tt", type);
                    cmd.Parameters.AddWithValue("id", id);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct)) mine.Add(r.GetInt32(0));
                }

                return ApiResults.Ok("OK", new GenresForData(top, mine));
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // 태그 토글(있으면 삭제/없으면 삽입). 로그인 + 제재 차단.
        app.MapGroup("/me").RequireAuthorization()
           .MapPost("/genre-tags", async (GenreTagRequest req, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (Me(user) is not { } uid) return ApiResults.Unauthorized("UNAUTHORIZED");
            if (req.TargetType is not ("track" or "album") || string.IsNullOrWhiteSpace(req.SpotifyId))
                return ApiResults.BadRequest("INVALID_TARGET");

            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync(ct);
                if (await Moderation.CheckAsync(conn, uid, ct) is { } block) return Moderation.ToResult(block);

                bool tagged;
                await using (var del = new NpgsqlCommand(
                    "delete from public.genre_tags where user_id=@me and target_type=@tt and target_spotify_id=@id and genre_id=@gid", conn))
                {
                    del.Parameters.AddWithValue("me", uid);
                    del.Parameters.AddWithValue("tt", req.TargetType!);
                    del.Parameters.AddWithValue("id", req.SpotifyId!);
                    del.Parameters.AddWithValue("gid", req.GenreId);
                    tagged = await del.ExecuteNonQueryAsync(ct) == 0; // 지운 게 없으면 새로 다는 것
                }

                if (tagged)
                {
                    await using var ins = new NpgsqlCommand(
                        "insert into public.genre_tags (user_id, target_type, target_spotify_id, genre_id) values (@me, @tt, @id, @gid) on conflict do nothing", conn);
                    ins.Parameters.AddWithValue("me", uid);
                    ins.Parameters.AddWithValue("tt", req.TargetType!);
                    ins.Parameters.AddWithValue("id", req.SpotifyId!);
                    ins.Parameters.AddWithValue("gid", req.GenreId);
                    await ins.ExecuteNonQueryAsync(ct);
                }
                return ApiResults.Ok("OK", new { tagged });
            }
            catch (PostgresException ex) when (ex.SqlState == "23503") { return ApiResults.BadRequest("INVALID_TARGET"); } // genre_id FK
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });
    }

    private static Guid? Me(ClaimsPrincipal user) =>
        user.FindFirstValue("sub") is { Length: > 0 } id && Guid.TryParse(id, out var g) ? g : null;
}
