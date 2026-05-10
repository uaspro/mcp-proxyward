using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ProxyWard.IntegrationTests;

internal sealed record CapturedLog(
    string Category,
    LogLevel Level,
    EventId EventId,
    string Message,
    IReadOnlyDictionary<string, string> State);

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLog> _entries = new();

    public IReadOnlyCollection<CapturedLog> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName) =>
        new CapturingLogger(categoryName, _entries);

    public void Dispose()
    {
    }
}

internal sealed class CapturingLogger(
    string categoryName,
    ConcurrentQueue<CapturedLog> entries) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull =>
        NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var structuredState = new Dictionary<string, string>(StringComparer.Ordinal);

        if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            foreach (var pair in pairs)
            {
                structuredState[pair.Key] = pair.Value?.ToString() ?? string.Empty;
            }
        }

        entries.Enqueue(new CapturedLog(
            categoryName,
            logLevel,
            eventId,
            formatter(state, exception),
            structuredState));
    }
}

internal sealed class NullScope : IDisposable
{
    public static readonly NullScope Instance = new();

    public void Dispose()
    {
    }
}
