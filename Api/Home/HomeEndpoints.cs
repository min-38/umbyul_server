using System.Security.Claims;
using Api.Common;
using Npgsql;

namespace Api.Home;

/// 홈 피드 (NON-43). 공개(옵셔널 인증) — 토큰 있으면 팔로우 피드 포함.
/// 전부 우리 DB만 조회(표시 메타데이터가 ratings에 캐시돼 있어 Spotify 호출 없음).
public static class HomeEndpoints
{
    public static void MapHomeEndpoints(this WebApplication app, string? dbConnString)
    {
        app.MapGet("/home", async (ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            var me = Me(user);
            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync(ct);

                var recent = await LoadReviewsAsync(conn, null, ct);
                var trending = await LoadTrendingAsync(conn, ct);
                var feed = me is { } uid ? await LoadReviewsAsync(conn, uid, ct) : [];

                return ApiResults.Ok("OK", new HomeData(recent, trending, feed));
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });
    }

    // followerId 지정 시 그 유저의 팔로잉으로 한정(팔로우 피드), 아니면 글로벌 최근.
    private static async Task<List<HomeReview>> LoadReviewsAsync(NpgsqlConnection conn, Guid? followerId, CancellationToken ct)
    {
        var scope = followerId is null
            ? ""
            : "and r.user_id in (select following_id from public.follows where follower_id = @me)";
        await using var cmd = new NpgsqlCommand(
            $"""
            select r.id, r.user_id, u.username, u.avatar_url, r.target_type, r.target_spotify_id,
                   r.score, r.review, r.created_at, r.target_name, r.target_artist, r.target_image_url
            from public.ratings r
            join public.users u on u.id = r.user_id
            where r.review is not null and r.review <> '' and r.target_spotify_id is not null and r.deleted_at is null {scope}
            order by r.created_at desc
            limit 12
            """, conn);
        if (followerId is { } fid) cmd.Parameters.AddWithValue("me", fid);

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

    // 최근 30일 평가 수 기준 화제의 릴리스.
    private static async Task<List<TrendingItem>> LoadTrendingAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            select r.target_type, r.target_spotify_id, count(*), round(avg(r.score), 2)::float8,
                   max(r.target_name), max(r.target_artist), max(r.target_image_url)
            from public.ratings r
            where r.created_at > now() - interval '30 days' and r.target_spotify_id is not null and r.deleted_at is null
            group by r.target_type, r.target_spotify_id
            order by count(*) desc, avg(r.score) desc
            limit 10
            """, conn);

        var list = new List<TrendingItem>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new TrendingItem(
                r.GetString(0), r.GetString(1), (int)r.GetInt64(2), r.GetDouble(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6)));
        return list;
    }

    private static Guid? Me(ClaimsPrincipal user) =>
        user.FindFirstValue("sub") is { Length: > 0 } id && Guid.TryParse(id, out var g) ? g : null;
}

public sealed record HomeReview(
    string Id, string UserId, string Username, string? AvatarUrl,
    string TargetType, string TargetSpotifyId, decimal Score, string Body, DateTimeOffset CreatedAt,
    string? Name, string? Artist, string? ImageUrl);
public sealed record TrendingItem(
    string TargetType, string SpotifyId, int Count, double Average,
    string? Name, string? Artist, string? ImageUrl);
public sealed record HomeData(
    IReadOnlyList<HomeReview> RecentReviews, IReadOnlyList<TrendingItem> Trending,
    IReadOnlyList<HomeReview> FollowFeed);
