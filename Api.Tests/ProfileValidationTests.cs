using Api.Profile;

namespace Api.Tests;

public class ProfileValidationTests
{
    // ---- IsUsername ----
    [Theory]
    [InlineData("ab")]
    [InlineData("a-b")]
    [InlineData("User123")]
    [InlineData("a-b-c")]
    public void IsUsername_valid(string u) => Assert.True(ProfileValidation.IsUsername(u));

    [Theory]
    [InlineData("a")] // 1자
    [InlineData("-ab")] // 하이픈 시작
    [InlineData("ab-")] // 하이픈 끝
    [InlineData("a--b")] // 연속 하이픈
    [InlineData("a_b")] // 언더스코어 불가
    [InlineData("한글")] // 비ASCII
    [InlineData("")]
    [InlineData(null)]
    public void IsUsername_invalid(string? u) => Assert.False(ProfileValidation.IsUsername(u));

    [Fact]
    public void IsUsername_length_boundaries()
    {
        Assert.True(ProfileValidation.IsUsername(new string('a', 30))); // 30 OK
        Assert.False(ProfileValidation.IsUsername(new string('a', 31))); // 31 too long
    }

    // ---- IsCountry ----
    [Theory]
    [InlineData("KR", true)]
    [InlineData("US", true)]
    [InlineData("kr", false)] // 소문자
    [InlineData("K", false)]
    [InlineData("KOR", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsCountry_cases(string? c, bool expected) =>
        Assert.Equal(expected, ProfileValidation.IsCountry(c));

    // ---- IsGender ----
    [Theory]
    [InlineData("male", true)]
    [InlineData("female", true)]
    [InlineData("other", true)]
    [InlineData("undisclosed", true)]
    [InlineData("x", false)]
    [InlineData("Male", false)] // 대문자
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsGender_cases(string? g, bool expected) =>
        Assert.Equal(expected, ProfileValidation.IsGender(g));

    // ---- TryParseBirth ----
    [Theory]
    [InlineData("2000-01-15")]
    [InlineData("2000-02-29")] // 윤년
    public void TryParseBirth_valid(string s) => Assert.True(ProfileValidation.TryParseBirth(s, out _));

    [Theory]
    [InlineData("2001-02-29")] // 비윤년 2/29
    [InlineData("2000-13-01")] // 13월
    [InlineData("2000-00-10")] // 0월
    [InlineData("2000/01/15")] // 형식 불일치
    [InlineData("20000115")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseBirth_invalid(string? s) => Assert.False(ProfileValidation.TryParseBirth(s, out _));

    // ---- Age ----
    [Fact]
    public void Age_on_birthday_is_exact() =>
        Assert.Equal(14, ProfileValidation.Age(new DateOnly(2010, 6, 30), new DateOnly(2024, 6, 30)));

    [Fact]
    public void Age_birthday_not_yet_passed() => // 생일 하루 전
        Assert.Equal(13, ProfileValidation.Age(new DateOnly(2010, 7, 1), new DateOnly(2024, 6, 30)));

    [Fact]
    public void Age_birthday_passed() =>
        Assert.Equal(14, ProfileValidation.Age(new DateOnly(2010, 6, 29), new DateOnly(2024, 6, 30)));

    [Theory]
    // 2/29생: 비윤년 2/28엔 아직 생일 전, 3/1엔 지남, 윤년 2/29엔 당일
    [InlineData(2023, 2, 28, 14)]
    [InlineData(2023, 3, 1, 15)]
    [InlineData(2024, 2, 29, 16)]
    public void Age_leap_birthday(int y, int m, int d, int expected) =>
        Assert.Equal(expected, ProfileValidation.Age(new DateOnly(2008, 2, 29), new DateOnly(y, m, d)));
}
