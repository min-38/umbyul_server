using System.Security.Claims;
using System.Text.Json;
using Api.Common;
using Npgsql;

namespace Api.Feed;

/// 홈 피드 (NON-88). 공개(옵셔널 인증). 리뷰 카드 목록 + Reddit식 정렬.
///   scope = all | following(로그인 필요), sort = hot | newest | likes | ratio | rising.
///   반응(review_reactions)으로 좋아요/싫어요 집계. 전부 우리 DB만 조회.
public static class FeedEndpoints
{
    public static void MapFeedEndpoints(this WebApplication app, string? dbConnString)
    {
        app.MapGet("/feed", async (string? sort, string? scope, int? offset, int? limit, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            var me = Me(user);
            var off = Math.Max(offset ?? 0, 0);
            var lim = Math.Clamp(limit ?? 50, 1, 100);

            // 팔로잉인데 비로그인 → 빈 피드.
            if (scope == "following" && me is null) return ApiResults.Ok("OK", new FeedData([]));
            var scopeClause = scope == "following"
                ? "and r.user_id in (select following_id from public.follows where follower_id = @me)"
                : "";

            // 계산 정렬의 페이지 간 안정성 위해 tie-breaker(id) 포함.
            var orderBy = sort switch
            {
                "newest" => "created_at desc",
                "likes" => "likes desc, created_at desc",
                "ratio" => "wilson desc, likes desc",
                "rising" => "recent desc, created_at desc",
                _ => "hot desc", // 화제(기본)
            } + ", id";

            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand(
                    $"""
                    with rx as (
                      select rating_id,
                             count(*) filter (where value = 'like') likes,
                             count(*) filter (where value = 'dislike') dislikes,
                             count(*) filter (where created_at > now() - interval '24 hours') recent
                      from public.review_reactions
                      group by rating_id
                    ),
                    f as (
                      select r.id, r.user_id, u.username, u.avatar_url, r.target_type, r.target_spotify_id,
                             r.score, r.review, r.created_at, r.target_name, r.target_artist, r.target_image_url, r.target_artists, r.target_explicit,
                             coalesce(rx.likes, 0) likes, coalesce(rx.dislikes, 0) dislikes, coalesce(rx.recent, 0) recent,
                             (select re.value from public.review_reactions re where re.rating_id = r.id and re.user_id = @me limit 1) my_reaction,
                             (select count(*) from public.review_comments c where c.rating_id = r.id and c.deleted_at is null) comment_count
                      from public.ratings r
                      join public.users u on u.id = r.user_id
                      left join rx on rx.rating_id = r.id
                      where r.review is not null and length(trim(r.review)) > 0 and r.target_spotify_id is not null and r.deleted_at is null {scopeClause}
                        and not exists (select 1 from public.feed_dismissals d where d.user_id = @me and d.rating_id = r.id)
                        and not exists (select 1 from public.blocks b where (b.blocker_id = @me and b.blocked_id = r.user_id) or (b.blocker_id = r.user_id and b.blocked_id = @me))
                    )
                    select id, user_id, username, avatar_url, target_type, target_spotify_id,
                           score, review, created_at, target_name, target_artist, target_image_url, target_artists,
                           likes, dislikes, my_reaction, comment_count, target_explicit,
                           (log(greatest(abs(likes - dislikes), 1))
                             + sign(likes - dislikes) * extract(epoch from created_at) / 45000.0) hot,
                           (case when likes + dislikes = 0 then 0 else
                             ((likes::float / (likes + dislikes) + 1.9208 / (likes + dislikes))
                               - 1.96 * sqrt(((likes::float / (likes + dislikes)) * (1 - likes::float / (likes + dislikes)) + 0.9604 / (likes + dislikes)) / (likes + dislikes)))
                             / (1 + 3.8416 / (likes + dislikes))
                           end) wilson
                    from f
                    order by {orderBy}
                    limit @lim offset @off
                    """, conn);
                cmd.Parameters.AddWithValue("lim", lim);
                cmd.Parameters.AddWithValue("off", off);
                // @me 는 scopeClause · my_reaction · dismissal 필터에서 항상 참조 → 비로그인은 NULL 로 바인딩.
                cmd.Parameters.Add(new NpgsqlParameter("me", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)me ?? DBNull.Value });

                var list = new List<FeedItem>();
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                    list.Add(new FeedItem(
                        rd.GetGuid(0).ToString(), rd.GetGuid(1).ToString(), rd.GetString(2),
                        rd.IsDBNull(3) ? null : rd.GetString(3), rd.GetString(4), rd.GetString(5),
                        rd.GetDecimal(6), rd.GetString(7), rd.GetFieldValue<DateTimeOffset>(8),
                        rd.IsDBNull(9) ? null : rd.GetString(9), rd.IsDBNull(10) ? null : rd.GetString(10),
                        rd.IsDBNull(11) ? null : rd.GetString(11),
                        rd.IsDBNull(12) ? null : ParseArtists(rd.GetString(12)),
                        (int)rd.GetInt64(13), (int)rd.GetInt64(14),
                        rd.IsDBNull(15) ? null : rd.GetString(15),
                        (int)rd.GetInt64(16), rd.GetBoolean(17)));
                return ApiResults.Ok("OK", new FeedData(list));
            }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });

        // "관심 없음" — 이 리뷰를 내 피드에서 숨김(멱등). 인스타식, 취소 UI 없음.
        app.MapGroup("/me").RequireAuthorization()
           .MapPost("/feed/dismiss", async (DismissRequest req, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");
            if (user.FindFirstValue("sub") is not { Length: > 0 } sub || !Guid.TryParse(sub, out var uid))
                return ApiResults.Unauthorized("UNAUTHORIZED");
            if (!Guid.TryParse(req.RatingId, out var rid)) return ApiResults.BadRequest("INVALID_TARGET");

            try
            {
                await using var conn = new NpgsqlConnection(dbConnString);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand(
                    """
                    insert into public.feed_dismissals (user_id, rating_id)
                    values (@uid, @rid)
                    on conflict (user_id, rating_id) do nothing
                    """, conn);
                cmd.Parameters.AddWithValue("uid", uid);
                cmd.Parameters.AddWithValue("rid", rid);
                await cmd.ExecuteNonQueryAsync(ct);
                return ApiResults.Ok("OK", new { });
            }
            catch (PostgresException ex) when (ex.SqlState == "23503") { return ApiResults.BadRequest("INVALID_TARGET"); }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }
        });
    }

    public sealed record DismissRequest(string? RatingId);

    private static IReadOnlyList<ArtistRef>? ParseArtists(string json)
    {
        try { return JsonSerializer.Deserialize<List<ArtistRef>>(json); }
        catch (JsonException) { return null; }
    }

    private static Guid? Me(ClaimsPrincipal user) =>
        user.FindFirstValue("sub") is { Length: > 0 } id && Guid.TryParse(id, out var g) ? g : null;
}

public sealed record FeedItem(
    string Id, string UserId, string Username, string? AvatarUrl,
    string TargetType, string TargetSpotifyId, decimal Score, string Body, DateTimeOffset CreatedAt,
    string? Name, string? Artist, string? ImageUrl, IReadOnlyList<ArtistRef>? Artists,
    int Likes, int Dislikes, string? MyReaction, int CommentCount, bool Explicit);
public sealed record FeedData(IReadOnlyList<FeedItem> Items);
