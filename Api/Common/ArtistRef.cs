namespace Api.Common;

/// 아티스트 참조(id + 표시 이름). 평가 저장(요청)·Discover 응답 공용. (NON-85)
public sealed record ArtistRef(string? Id, string? Name);
