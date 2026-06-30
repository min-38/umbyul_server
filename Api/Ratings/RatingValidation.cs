namespace Api.Ratings;

/// 평점 등록 입력 검증. 순수 함수라 단위 테스트 가능. DB CHECK(0.5단위)와 일치시킨다.
public static class RatingValidation
{
    public const int MaxReviewLength = 5000;

    public static bool IsScore(decimal score) =>
        score >= 0.5m && score <= 5.0m && (score * 2) == Math.Floor(score * 2); // 0.5 단위

    public static bool IsTargetType(string? t) => t is "track" or "album";

    public static bool IsReview(string? review) => review is null || review.Length <= MaxReviewLength;
}
