using System.Text.RegularExpressions;

namespace Api.Profile;

/// 프론트(web)의 회원가입/온보딩 검증과 동일한 규칙. DB CHECK·UNIQUE 가 최종 방어선이지만,
/// 앱 계층에서 먼저 걸러 깔끔한 4xx 를 돌려준다.
public static class ProfileValidation
{
    private static readonly Regex Handle = new("^[A-Za-z0-9]+(-[A-Za-z0-9]+)*$", RegexOptions.Compiled);
    private static readonly Regex CountryCode = new("^[A-Z]{2}$", RegexOptions.Compiled);

    public static bool IsUsername(string? u) =>
        !string.IsNullOrEmpty(u) && u.Length is >= 2 and <= 30 && Handle.IsMatch(u);

    public static bool IsCountry(string? c) =>
        !string.IsNullOrEmpty(c) && CountryCode.IsMatch(c);

    public static bool IsGender(string? g) =>
        g is "male" or "female" or "other" or "undisclosed";

    // yyyy-MM-dd 파싱
    public static bool TryParseBirth(string? s, out DateOnly birth) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", out birth);

    // 기준일 기준 만 나이
    public static int Age(DateOnly birth, DateOnly on)
    {
        var age = on.Year - birth.Year;
        if (birth > on.AddYears(-age)) age--;
        return age;
    }
}
