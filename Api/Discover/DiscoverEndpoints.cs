using System.Security.Claims;
using Api.Common;
using Api.Home;
using Npgsql;

namespace Api.Discover;

/// Discover (NON-81). 공개(옵셔널 인증). 스크롤 섹션:
///   Rising(Day/Week/Month/Year 평가 급증) · New(새 리뷰) · Recent(내 최근 리뷰, 로그인 시).
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
                    await LoadRisingAsync(conn, "1 day", ct),
                    await LoadRisingAsync(conn, "7 days", ct),
                    await LoadRisingAsync(conn, "30 days", ct),
                    await LoadRisingAsync(conn, "365 days", ct));
                var newReviews = await LoadReviewsAsync(conn, null, ct);
                var myRecent = me is { } uid ? await LoadReviewsAsync(conn, uid, ct) : [];

                return ApiResults.Ok("OK", new DiscoverData(rising, newReviews, myRecent));
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });
    }

    // 급상승: 최근 기간(interval) 평가 수 상위. 동률이면 평균 높은 순.
    private static async Task<List<DiscoverItem>> LoadRisingAsync(NpgsqlConnection conn, string interval, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            select r.target_type, r.target_spotify_id, count(*), round(avg(r.score), 2)::float8,
                   max(r.target_name), max(r.target_artist), max(r.target_image_url)
            from public.ratings r
            where r.created_at > now() - interval '{interval}' and r.target_spotify_id is not null and r.deleted_at is null
            group by r.target_type, r.target_spotify_id
            order by count(*) desc, avg(r.score) desc
            limit 15
            """, conn);

        var list = new List<DiscoverItem>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new DiscoverItem(
                r.GetString(0), r.GetString(1), (int)r.GetInt64(2), r.GetDouble(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6)));
        return list;
    }

    // 리뷰 목록. authorId 지정 시 그 유저(내 최근), 아니면 전역 최신(새 리뷰).
    private static async Task<List<HomeReview>> LoadReviewsAsync(NpgsqlConnection conn, Guid? authorId, CancellationToken ct)
    {
        var scope = authorId is null ? "" : "and r.user_id = @me";
        await using var cmd = new NpgsqlCommand(
            $"""
            select r.id, r.user_id, u.username, u.avatar_url, r.target_type, r.target_spotify_id,
                   r.score, r.review, r.created_at, r.target_name, r.target_artist, r.target_image_url
            from public.ratings r
            join public.users u on u.id = r.user_id
            where r.review is not null and r.review <> '' and r.target_spotify_id is not null and r.deleted_at is null {scope}
            order by r.created_at desc
            limit 15
            """, conn);
        if (authorId is { } aid) cmd.Parameters.AddWithValue("me", aid);

        var list = new List<HomeReview>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new HomeReview(
                r.GetGuid(0).ToString(), r.GetGuid(1).ToString(), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetString(5),
                r.GetDecimal(6), r.GetString(7), r.GetFieldValue<DateTimeOffset>(8),
                r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10),
                r.IsDBNull(11) ? null : r.GetString(11)));
        return list;
    }

    private static Guid? Me(ClaimsPrincipal user) =>
        user.FindFirstValue("sub") is { Length: > 0 } id && Guid.TryParse(id, out var g) ? g : null;
}

public sealed record DiscoverItem(
    string TargetType, string SpotifyId, int Count, double Average,
    string? Name, string? Artist, string? ImageUrl);
public sealed record RisingWindows(
    IReadOnlyList<DiscoverItem> Day, IReadOnlyList<DiscoverItem> Week,
    IReadOnlyList<DiscoverItem> Month, IReadOnlyList<DiscoverItem> Year);
public sealed record DiscoverData(
    RisingWindows Rising,
    IReadOnlyList<HomeReview> NewReviews,
    IReadOnlyList<HomeReview> MyRecent);
