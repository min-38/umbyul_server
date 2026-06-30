using System.Text.Json;
using Api.Common;
using Api.Detail;
using Api.Spotify;
using Npgsql;

namespace Api.Profile;

/// 공개 유저 프로필 (NON-24). 비로그인 열람. 유저 정보 + 작성 리뷰 목록.
/// 리뷰 대상 이름/이미지는 target_spotify_id 를 Spotify 배치 조회로 라이브 해석(콘텐츠 비영구).
public sealed record ProfileReview(
    string Id, string TargetType, string? SpotifyId, decimal Score, string? Body,
    DateTimeOffset CreatedAt, int LikeCount, string? Name, string? Artist, string? ImageUrl);

public sealed record UserProfile(
    string Username, string? AvatarUrl, DateTimeOffset JoinedAt,
    int ReviewCount, int TotalLikes, IReadOnlyList<ProfileReview> Reviews);

public static class PublicProfileEndpoints
{
    private record Row(string Id, string Tt, string? Sid, decimal Score, string? Body, DateTimeOffset Created, int Likes);

    public static void MapPublicProfileEndpoints(this WebApplication app, string? dbConnString)
    {
        app.MapGet("/users/{username}", async (string username, SpotifyClient spotify, CancellationToken ct) =>
        {
            if (dbConnString is null) return ApiResults.ServiceUnavailable("DB_NOT_CONFIGURED");

            await using var conn = new NpgsqlConnection(dbConnString);
            try { await conn.OpenAsync(ct); }
            catch (NpgsqlException) { return ApiResults.ServiceUnavailable("DB_UNAVAILABLE"); }

            // 유저
            Guid uid;
            string uname;
            string? avatar;
            DateTimeOffset joined;
            await using (var ucmd = new NpgsqlCommand(
                "select id, username, avatar_url, created_at from public.users where lower(username) = lower(@u)", conn))
            {
                ucmd.Parameters.AddWithValue("u", username);
                await using var ur = await ucmd.ExecuteReaderAsync(ct);
                if (!await ur.ReadAsync(ct)) return ApiResults.NotFound("USER_NOT_FOUND");
                uid = ur.GetGuid(0);
                uname = ur.GetString(1);
                avatar = ur.IsDBNull(2) ? null : ur.GetString(2);
                joined = ur.GetFieldValue<DateTimeOffset>(3);
            }

            // 작성 리뷰 + 받은 좋아요 수. 마이그레이션(0006/0007) 미적용 등 실패 시 리뷰 없이 프로필만.
            var rows = new List<Row>();
            try
            {
                await using var rcmd = new NpgsqlCommand(
                    """
                    select r.id, r.target_type, r.target_spotify_id, r.score, r.review, r.created_at,
                           count(re.id) filter (where re.value = 'like') as likes
                    from public.ratings r
                    left join public.review_reactions re on re.rating_id = r.id
                    where r.user_id = @uid
                    group by r.id
                    order by r.created_at desc
                    """, conn);
                rcmd.Parameters.AddWithValue("uid", uid);
                await using var rr = await rcmd.ExecuteReaderAsync(ct);
                while (await rr.ReadAsync(ct))
                    rows.Add(new Row(
                        rr.GetGuid(0).ToString(), rr.GetString(1),
                        rr.IsDBNull(2) ? null : rr.GetString(2), rr.GetDecimal(3),
                        rr.IsDBNull(4) ? null : rr.GetString(4), rr.GetFieldValue<DateTimeOffset>(5),
                        (int)rr.GetInt64(6)));
            }
            catch (NpgsqlException)
            {
                rows.Clear();
            }

            var totalLikes = rows.Sum(x => x.Likes);
            var display = await ResolveAsync(spotify, rows, ct);

            var reviews = rows.Select(x =>
            {
                display.TryGetValue(x.Sid ?? "", out var d);
                return new ProfileReview(x.Id, x.Tt, x.Sid, x.Score, x.Body, x.Created, x.Likes, d.Name, d.Artist, d.Image);
            }).ToList();

            return ApiResults.Ok("OK", new UserProfile(uname, avatar, joined, rows.Count, totalLikes, reviews));
        });
    }

    private record Resolved(string? Id, string? Name, string? Artist, string? Image);

    // target_spotify_id → 이름/아티스트/이미지.
    // 배치(/tracks?ids=)는 앱 토큰으로 403 → 단건 조회를 동시성 8로 제한해 병렬.
    private static async Task<Dictionary<string, (string? Name, string? Artist, string? Image)>> ResolveAsync(
        SpotifyClient spotify, List<Row> rows, CancellationToken ct)
    {
        var map = new Dictionary<string, (string?, string?, string?)>();
        if (!spotify.Configured) return map;

        var targets = rows
            .Where(r => !string.IsNullOrEmpty(r.Sid))
            .Select(r => (Type: r.Tt, Id: r.Sid!))
            .Distinct()
            .ToList();
        if (targets.Count == 0) return map;

        using var sem = new SemaphoreSlim(8);
        var tasks = targets.Select(async t =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var json = await spotify.GetAsync(t.Type == "track" ? $"tracks/{t.Id}" : $"albums/{t.Id}", ct);
                if (json is null) return new Resolved(null, null, null, null);
                using var doc = JsonDocument.Parse(json);
                if (t.Type == "track")
                {
                    var tr = DetailParse.Track(doc.RootElement);
                    return new Resolved(tr.SpotifyId, tr.Name, tr.Artists.Count > 0 ? tr.Artists[0].Name : null, tr.Album?.ImageUrl);
                }
                var al = DetailParse.Album(doc.RootElement);
                return new Resolved(al.SpotifyId, al.Name, al.Artists.Count > 0 ? al.Artists[0].Name : null, al.ImageUrl);
            }
            catch (HttpRequestException)
            {
                return new Resolved(null, null, null, null);
            }
            finally
            {
                sem.Release();
            }
        });

        foreach (var r in await Task.WhenAll(tasks))
            if (r.Id is not null) map[r.Id] = (r.Name, r.Artist, r.Image);

        return map;
    }
}
