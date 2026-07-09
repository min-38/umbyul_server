namespace Api.Search;

// 통합 검색 결과. 각 카테고리는 첫 페이지 items + 전체 개수(total)로, "더 보기" 판단에 쓴다.
public record SearchResults(
    CategoryResult<TrackResult> Tracks,
    CategoryResult<AlbumResult> Albums,
    CategoryResult<ArtistResult> Artists,
    CategoryResult<UserResult> Users);

public record CategoryResult<T>(IReadOnlyList<T> Items, int Total);

// 평점 집계(평균·개수)는 검색 결과에도 노출(NON-8). Spotify 메타 + 우리 DB 집계.
public record ArtistLite(string Id, string Name);
public record TrackResult(string Id, string Name, string Artist, IReadOnlyList<ArtistLite> Artists, string? AlbumId, string? AlbumName, string? ImageUrl, string? Isrc, bool Explicit, Api.Detail.RatingSummary? Rating = null);

public record AlbumResult(string Id, string Name, string Artist, string? ImageUrl, string? ReleaseDate, Api.Detail.RatingSummary? Rating = null);

public record ArtistResult(string Id, string Name, string? ImageUrl);

public record UserResult(string Id, string Username, string? AvatarUrl);
