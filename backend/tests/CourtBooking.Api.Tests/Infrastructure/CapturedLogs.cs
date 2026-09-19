using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CourtBooking.Api.Tests.Infrastructure;

/// <summary>
/// Keeps what the API logged at Error, so a test that sees a 500 can say what caused it instead of
/// only that it happened.
/// </summary>
public sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<string> entries = new();

    public IReadOnlyCollection<string> Entries => entries;

    public string Text => string.Join("\n", entries);

    /// <summary>
    /// The database error behind the most recent failure, trimmed to the part that names it — a
    /// test that meets a 500 can then say "deadlock detected" rather than only "500".
    /// </summary>
    public string LastFailure
    {
        get
        {
            var text = Text;
            var start = text.LastIndexOf("PostgresException", StringComparison.Ordinal);
            return start < 0
                ? text[^Math.Min(text.Length, 300)..]
                : text[start..Math.Min(text.Length, start + 300)];
        }
    }

    public ILogger CreateLogger(string categoryName) => new Sink(entries);

    public void Dispose() => GC.SuppressFinalize(this);

    private sealed class Sink(ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                entries.Enqueue($"{formatter(state, exception)} {exception}");
            }
        }
    }
}
