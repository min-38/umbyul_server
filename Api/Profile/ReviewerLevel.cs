namespace Api.Profile;

/// 리뷰어 레벨/경험치(NON-153). 화폐가 아닌 진행도(XP) — 신뢰 신호.
/// XP = 10*리뷰 + 1*받은따봉 + 15*'픽 당일' 리뷰. (상수는 여기서만 튜닝)
/// 곡선은 지수형(앞 빠르게·뒤 가파르게) → 고레벨 희소. 새 테이블 없이 집계로 계산.
public static class ReviewerLevel
{
    private const int ReviewXp = 10;
    private const int LikeXp = 1;
    private const int DailyPickBonusXp = 15;

    // 누적 임계값 threshold(L) = round(B*(G^(L-1)-1)/(G-1)). L1=0, L2=30, L3=78, L4=155 …
    private const double Base = 30.0;   // B
    private const double Growth = 1.6;  // G
    private const int MaxLevel = 200;   // 폭주 방지 상한(현실적으로 도달 불가)

    public static int Xp(int reviewCount, int totalLikes, int dailyPickReviews) =>
        ReviewXp * Math.Max(0, reviewCount)
        + LikeXp * Math.Max(0, totalLikes)
        + DailyPickBonusXp * Math.Max(0, dailyPickReviews);

    /// 레벨 L 도달에 필요한 누적 XP. L<=1 → 0.
    public static int Threshold(int level) =>
        level <= 1 ? 0 : (int)Math.Round(Base * (Math.Pow(Growth, level - 1) - 1) / (Growth - 1));

    /// 누적 XP → 레벨(1부터). Threshold와 항상 일관되도록 반올림된 임계값으로 판정.
    public static int LevelFor(int xp)
    {
        if (xp <= 0) return 1;
        int level = 1;
        while (level < MaxLevel && Threshold(level + 1) <= xp) level++;
        return level;
    }

    /// (Level, Xp, XpIntoLevel, XpForLevel). 진행바 = XpIntoLevel / XpForLevel.
    /// 레벨업 시 XpIntoLevel은 0부터 다시 시작(누적 Xp는 유지).
    public static (int Level, int Xp, int XpIntoLevel, int XpForLevel) Compute(
        int reviewCount, int totalLikes, int dailyPickReviews)
    {
        var xp = Xp(reviewCount, totalLikes, dailyPickReviews);
        var level = LevelFor(xp);
        var cur = Threshold(level);
        var next = Threshold(level + 1);
        return (level, xp, xp - cur, next - cur);
    }
}
