namespace Api.Detail;

/// 상세 페이지 응답 모델. Spotify 라이브 조회 결과 + 우리 평점/리뷰를 합친다.
/// 영구 카탈로그를 만들지 않음(기획안 §5 컴플라이언스): 매 요청 Spotify에서 조회.
public sealed record ArtistRef(string Id, string Name);

public sealed record AlbumRef(string Id, string Name, string? ImageUrl);

public sealed record TrackRef(string Id, string Name, int DurationMs, int TrackNumber);

public sealed record RatingSummary(double? Average, int Count);

public sealed record ReviewItem(
    string Id,
    string UserId,
    string Username,
    string? AvatarUrl,
    decimal Score,
    string? Body,
    DateTimeOffset CreatedAt,
    int LikeCount,
    int DislikeCount,
    string? MyReaction); // "like" | "dislike" | null (로그인 시 내 반응)

// 장르: Spotify 앱 토큰으론 아티스트 genres가 안 내려와 제외(2024 메타데이터 축소, 실측 확인).
// 레이블: 앨범 label 직접 필드도 없어 copyrights(℗/©) 텍스트로 대체.
// TargetId = 평점 키(ISRC/UPC, 없으면 spotify_id). 클라가 평가 등록(NON-7) 시 그대로 사용.
public sealed record TrackDetail(
    string SpotifyId,
    string Name,
    string SpotifyUrl,
    IReadOnlyList<ArtistRef> Artists,
    AlbumRef? Album,
    string? Isrc,
    string TargetId,
    int DurationMs,
    string? ReleaseDate,
    string? Copyright,
    RatingSummary Rating,
    IReadOnlyList<ReviewItem> Reviews);

public sealed record AlbumDetail(
    string SpotifyId,
    string Name,
    string SpotifyUrl,
    IReadOnlyList<ArtistRef> Artists,
    string? ImageUrl,
    string? Upc,
    string TargetId,
    string? ReleaseDate,
    string? Copyright,
    int TotalTracks,
    IReadOnlyList<TrackRef> Tracks,
    RatingSummary Rating,
    IReadOnlyList<ReviewItem> Reviews);
