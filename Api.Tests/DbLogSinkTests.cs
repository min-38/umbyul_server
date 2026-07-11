using Api.Logging;
using Microsoft.Extensions.Logging;

namespace Api.Tests;

// NON-52: 로그 레벨 게이트 — 기본 min_level(Warning)에서 Warning 이상만 통과, None은 항상 거부.
public class DbLogSinkTests
{
    private static readonly DbLogSink Sink = new(""); // ExecuteAsync 미실행 — ShouldLog만 검증(기본 Warning)

    [Theory]
    [InlineData(LogLevel.Trace, false)]
    [InlineData(LogLevel.Debug, false)]
    [InlineData(LogLevel.Information, false)]
    [InlineData(LogLevel.Warning, true)]
    [InlineData(LogLevel.Error, true)]
    [InlineData(LogLevel.Critical, true)]
    [InlineData(LogLevel.None, false)]
    public void ShouldLog_gates_by_default_min_level(LogLevel level, bool expected) =>
        Assert.Equal(expected, Sink.ShouldLog(level));
}
