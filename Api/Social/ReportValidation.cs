namespace Api.Social;

/// 신고 입력 검증. 순수 함수라 단위 테스트 가능. reason/target_type 은 DB CHECK 와 일치.
public static class ReportValidation
{
    public const int MaxDetailLength = 1000;

    public static bool IsTargetType(string? t) => t is "rating" or "user";

    public static bool IsReason(string? r) =>
        r is "not_music" or "inappropriate_profile" or "abuse" or "other";

    public static bool IsDetail(string? d) => d is null || d.Length <= MaxDetailLength;
}
