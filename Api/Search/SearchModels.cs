namespace Api.Search;

// 통합 검색 결과. 각 카테고리는 첫 페이지 items + 전체 개수(total)로, "더 보기" 판단에 쓴다.
public record SearchResults(
    CategoryResult<TrackResult> Tracks,
    CategoryResult<AlbumResult> Albums,
    CategoryResult<ArtistResult> Artists,
    CategoryResult<UserResult> Users);

public record CategoryResult<T>(IReadOnlyList<T> Items, int Total);

// 평점/review count 는 NON-7/8 이후. 지금은 메타데이터만.
public record TrackResult(string Id, string Name, string Artist, string? AlbumName, string? ImageUrl, string? Isrc);

public record AlbumResult(string Id, string Name, string Artist, string? ImageUrl, string? ReleaseDate);

public record ArtistResult(string Id, string Name, string? ImageUrl);

public record UserResult(string Id, string Username, string? AvatarUrl);
