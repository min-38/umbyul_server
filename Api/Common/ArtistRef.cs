using System.Text.Json;

namespace Api.Common;

/// 아티스트 참조(id + 표시 이름). 평가 저장(요청)·Discover 응답 공용. (NON-85)
public sealed record ArtistRef(string? Id, string? Name)
{
    /// jsonb 문자열 → 목록. 파싱 실패/빈값이면 null. (DUP-1: Feed/Chart/Discover 공용 헬퍼)
    public static IReadOnlyList<ArtistRef>? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<List<ArtistRef>>(json); }
        catch (JsonException) { return null; }
    }
}
