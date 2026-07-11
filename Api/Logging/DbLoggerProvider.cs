using Microsoft.Extensions.Logging;

namespace Api.Logging;

/// app_logs 싱크용 커스텀 로거 프로바이더(NON-52). 모든 카테고리 로그를 게이트(min_level) 통과 시 싱크로 넘긴다.
public sealed class DbLoggerProvider(DbLogSink sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new DbLogger(sink, categoryName);
    public void Dispose() { }
}

public sealed class DbLogger(DbLogSink sink, string category) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => sink.ShouldLog(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!sink.ShouldLog(logLevel)) return;
        // 피드백 루프 방지: 싱크 자신의 카테고리(Api.Logging.*)는 기록하지 않음.
        if (category.StartsWith("Api.Logging", StringComparison.Ordinal)) return;

        var message = formatter(state, exception);
        if (string.IsNullOrEmpty(message) && exception is null) return;

        sink.Enqueue(new AppLogEntry(
            logLevel, message, exception?.ToString(), category, eventId.Id, DateTimeOffset.UtcNow));
    }
}
