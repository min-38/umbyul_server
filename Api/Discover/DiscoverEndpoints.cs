using System.Security.Claims;
using System.Text.Json;
using Api.Common;
using Npgsql;

namespace Api.Discover;

/// Discover (NON-81). 공개(옵셔널 인증). 앨범 커버만 보여주는 스크롤 섹션:
///   Rising(Day/Week/Month/Year 평가 급증) · New(최근 리뷰된 대상) · Recent(내 최근 리뷰 대상).
/// 전부 우리 DB만 조회 — 표시 메타가 ratings에 캐시돼 있어 Spotify 호출 없음.
public static class DiscoverEndpoints
{
    /// 대표 장르 합의 최소 표수(GenreEndpoints와 동일 기준). 1표로는 장르를 신뢰하지 않는다.
    private const int MinConsensusVotes = 2;
    /// "높게 준" 리뷰 기준(0.5~5.0 척도). 취향 장르 추출용.
    private const decimal HighScore = 4.0m;

    public static void MapDiscoverEndpoints(this WebApplication app, string? dbConnString)
    {
        app.MapGet("/discover", async (ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            var me = Me(user);
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync(ct);

                var rising = new RisingWindows(
                    await LoadAsync(conn, "and r.created_at > now() - interval '1 day'", "count(*) desc, avg(r.score) desc", null, ct),
                    await LoadAsync(conn, "and r.created_at > now() - interval '7 days'", "count(*) desc, avg(r.score) desc", null, ct),
                    await LoadAsync(conn, "and r.created_at > now() - interval '30 days'", "count(*) desc, avg(r.score) desc", null, ct),
                    await LoadAsync(conn, "and r.created_at > now() - interval '365 days'", "count(*) desc, avg(r.score) desc", null, ct));
                // New: 최근 리뷰된 대상(리뷰 있는 것), 대상별 최신순.
                var fresh = await LoadAsync(conn, "and r.review is not null and r.review <> ''", "max(r.created_at) desc", null, ct);
                // Recent: 내가 최근 리뷰한 대상(로그인 시).
                var myRecent = me is { } uid
                    ? await LoadAsync(conn, "and r.review is not null and r.review <> '' and r.user_id = @me", "max(r.created_at) desc", uid, ct)
                    : [];
                // Recommend(NON-155): 콘텐츠 기반(내가 높게 준 장르의 다른 곡) → 신호 없으면 전체 인기 폴백.
                var recommend = await LoadRecommendAsync(conn, me, ct);

                return ApiResults.Ok("OK", new DiscoverData(rising, fresh, myRecent, recommend));
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });
    }

    // 대상(앨범/곡)별 집계 커버. where/orderBy로 섹션 구분. me 지정 시 @me 파라미터.
    // artists: 대상별로 동일하므로 non-null 하나를 집계로 취함(개별 아티스트 링크용, NON-85).
    private static async Task<List<DiscoverItem>> LoadAsync(
        NpgsqlConnection conn, string where, string orderBy, Guid? me, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            select r.target_type, r.target_spotify_id, count(*), round(avg(r.score), 2)::float8,
                   max(r.target_name), max(r.target_artist), max(r.target_image_url),
                   (array_agg(r.target_artists) filter (where r.target_artists is not null))[1],
                   bool_or(r.target_explicit)
            from public.ratings r
            where r.target_spotify_id is not null and r.deleted_at is null {where}
            group by r.target_type, r.target_spotify_id
            order by {orderBy}
            limit 15
            """, conn);
        if (me is { } uid) cmd.Parameters.AddWithValue("me", uid);
        return await ReadItemsAsync(cmd, ct);
    }

    // 커버 집계 select(위 LoadAsync와 동일 컬럼)의 공용 리더.
    private static async Task<List<DiscoverItem>> ReadItemsAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var list = new List<DiscoverItem>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new DiscoverItem(
                r.GetString(0), r.GetString(1), (int)r.GetInt64(2), r.GetDouble(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : ArtistRef.Parse(r.GetString(7)),
                r.GetBoolean(8)));
        return list;
    }

    // Recommend(NON-155): 로그인 유저는 취향 장르 기반 콘텐츠 추천, 없으면(콜드/비로그인) 전체 인기 폴백.
    private static async Task<List<DiscoverItem>> LoadRecommendAsync(NpgsqlConnection conn, Guid? me, CancellationToken ct)
    {
        if (me is { } uid)
        {
            var genres = await PreferredGenresAsync(conn, uid, ct);
            if (genres.Count > 0)
            {
                var byGenre = await LoadByGenresAsync(conn, uid, genres, ct);
                if (byGenre.Count > 0) return byGenre;
            }
        }
        return await LoadPopularAsync(conn, me, ct);
    }

    // 취향 장르: 내가 높게 준(≥HighScore) 대상들의 크라우드 태깅 상위 장르.
    private static async Task<List<int>> PreferredGenresAsync(NpgsqlConnection conn, Guid uid, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            select gt.genre_id
            from public.ratings r
            join public.genre_tags gt on gt.target_type = r.target_type and gt.target_spotify_id = r.target_spotify_id
            where r.user_id = @me and r.deleted_at is null and r.score >= @high
            group by gt.genre_id
            order by count(*) desc
            limit 5
            """, conn);
        cmd.Parameters.AddWithValue("me", uid);
        cmd.Parameters.AddWithValue("high", HighScore);
        var ids = new List<int>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) ids.Add(r.GetInt32(0));
        return ids;
    }

    // 콘텐츠 기반 후보: 취향 장르로 합의 태깅된(≥MinConsensusVotes) 대상 중 내가 아직 평가 안 한 것, 리뷰수·평점순.
    private static async Task<List<DiscoverItem>> LoadByGenresAsync(NpgsqlConnection conn, Guid uid, List<int> genres, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            select r.target_type, r.target_spotify_id, count(*), round(avg(r.score), 2)::float8,
                   max(r.target_name), max(r.target_artist), max(r.target_image_url),
                   (array_agg(r.target_artists) filter (where r.target_artists is not null))[1],
                   bool_or(r.target_explicit)
            from public.ratings r
            where r.target_spotify_id is not null and r.deleted_at is null
              and exists (
                  select 1 from public.genre_tags gt
                  where gt.target_type = r.target_type and gt.target_spotify_id = r.target_spotify_id
                    and gt.genre_id = any(@genres)
                  group by gt.genre_id
                  having count(*) >= @min
              )
              and not exists (
                  select 1 from public.ratings mine
                  where mine.user_id = @me and mine.target_type = r.target_type
                    and mine.target_spotify_id = r.target_spotify_id and mine.deleted_at is null
              )
            group by r.target_type, r.target_spotify_id
            order by count(*) desc, avg(r.score) desc
            limit 15
            """, conn);
        cmd.Parameters.AddWithValue("genres", genres.ToArray());
        cmd.Parameters.AddWithValue("me", uid);
        cmd.Parameters.AddWithValue("min", MinConsensusVotes);
        return await ReadItemsAsync(cmd, ct);
    }

    // 폴백(콜드스타트·비로그인): 전체 인기(누적 리뷰순). 로그인 시 내가 평가한 건 제외.
    private static async Task<List<DiscoverItem>> LoadPopularAsync(NpgsqlConnection conn, Guid? me, CancellationToken ct)
    {
        var exclude = me is null ? "" :
            """
            and not exists (
                select 1 from public.ratings mine
                where mine.user_id = @me and mine.target_type = r.target_type
                  and mine.target_spotify_id = r.target_spotify_id and mine.deleted_at is null
            )
            """;
        await using var cmd = new NpgsqlCommand(
            $"""
            select r.target_type, r.target_spotify_id, count(*), round(avg(r.score), 2)::float8,
                   max(r.target_name), max(r.target_artist), max(r.target_image_url),
                   (array_agg(r.target_artists) filter (where r.target_artists is not null))[1],
                   bool_or(r.target_explicit)
            from public.ratings r
            where r.target_spotify_id is not null and r.deleted_at is null {exclude}
            group by r.target_type, r.target_spotify_id
            order by count(*) desc, avg(r.score) desc
            limit 15
            """, conn);
        if (me is { } uid) cmd.Parameters.AddWithValue("me", uid);
        return await ReadItemsAsync(cmd, ct);
    }

    private static Guid? Me(ClaimsPrincipal user) =>
        user.FindFirstValue("sub") is { Length: > 0 } id && Guid.TryParse(id, out var g) ? g : null;
}

public sealed record DiscoverItem(
    string TargetType, string SpotifyId, int Count, double Average,
    string? Name, string? Artist, string? ImageUrl, IReadOnlyList<ArtistRef>? Artists, bool Explicit);
public sealed record RisingWindows(
    IReadOnlyList<DiscoverItem> Day, IReadOnlyList<DiscoverItem> Week,
    IReadOnlyList<DiscoverItem> Month, IReadOnlyList<DiscoverItem> Year);
public sealed record DiscoverData(
    RisingWindows Rising,
    IReadOnlyList<DiscoverItem> New,
    IReadOnlyList<DiscoverItem> MyRecent,
    IReadOnlyList<DiscoverItem> Recommend);
