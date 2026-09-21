using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CourtBooking.Api.Tests.Infrastructure;

/// <summary>
/// The product events the API wrote (PRD 8), by name: the first word of each line written to
/// the one category AppEvents uses. The name is part of the template by design, so it is also
/// the first word of what is rendered.
/// </summary>
public sealed class CapturedEvents : ILoggerProvider
{
    public const string Category = "CourtBooking.Events";

    private readonly ConcurrentQueue<string> names = new();

    public IReadOnlyCollection<string> Names => names;

    public ILogger CreateLogger(string categoryName) =>
        categoryName == Category ? new Sink(names) : NullSink.Instance;

    public void Dispose() => GC.SuppressFinalize(this);

    private sealed class Sink(ConcurrentQueue<string> names) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception);
            var end = text.IndexOf(' ', StringComparison.Ordinal);
            names.Enqueue(end < 0 ? text : text[..end]);
        }
    }

    private sealed class NullSink : ILogger
    {
        public static readonly NullSink Instance = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }
}
