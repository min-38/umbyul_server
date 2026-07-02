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

                return ApiResults.Ok("OK", new DiscoverData(rising, fresh, myRecent));
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
                   (array_agg(r.target_artists) filter (where r.target_artists is not null))[1]
            from public.ratings r
            where r.target_spotify_id is not null and r.deleted_at is null {where}
            group by r.target_type, r.target_spotify_id
            order by {orderBy}
            limit 15
            """, conn);
        if (me is { } uid) cmd.Parameters.AddWithValue("me", uid);

        var list = new List<DiscoverItem>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new DiscoverItem(
                r.GetString(0), r.GetString(1), (int)r.GetInt64(2), r.GetDouble(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : ParseArtists(r.GetString(7))));
        return list;
    }

    private static IReadOnlyList<ArtistRef>? ParseArtists(string json)
    {
        try { return JsonSerializer.Deserialize<List<ArtistRef>>(json); }
        catch (JsonException) { return null; }
    }

    private static Guid? Me(ClaimsPrincipal user) =>
        user.FindFirstValue("sub") is { Length: > 0 } id && Guid.TryParse(id, out var g) ? g : null;
}

public sealed record DiscoverItem(
    string TargetType, string SpotifyId, int Count, double Average,
    string? Name, string? Artist, string? ImageUrl, IReadOnlyList<ArtistRef>? Artists);
public sealed record RisingWindows(
    IReadOnlyList<DiscoverItem> Day, IReadOnlyList<DiscoverItem> Week,
    IReadOnlyList<DiscoverItem> Month, IReadOnlyList<DiscoverItem> Year);
public sealed record DiscoverData(
    RisingWindows Rising,
    IReadOnlyList<DiscoverItem> New,
    IReadOnlyList<DiscoverItem> MyRecent);
