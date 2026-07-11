using Microsoft.Extensions.Logging;

namespace Api.Logging;

/// 중요 비즈니스 이벤트 로깅 헬퍼(NON-52 / NON-249). 엔드포인트에서 주입받아 호출.
///  - 개발자가 '중요'로 명시한 이벤트라 min_level 게이트를 우회해 app_logs에 항상 기록.
///  - 전용 카테고리 'App' — 뷰어에서 프레임워크 로그와 구분·필터하기 쉽게.
///  - DB 미설정(싱크 미등록)이면 조용히 no-op.
public sealed class AppLog(DbLogSink? sink)
{
    public const string Category = "App";

    public void Event(LogLevel level, string message, Exception? ex = null) =>
        sink?.LogEvent(level, Category, message, ex);

    public void Info(string message) => Event(LogLevel.Information, message);
    public void Warn(string message, Exception? ex = null) => Event(LogLevel.Warning, message, ex);
    public void Error(string message, Exception? ex = null) => Event(LogLevel.Error, message, ex);
}
