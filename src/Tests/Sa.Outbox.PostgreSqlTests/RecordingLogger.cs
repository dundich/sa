using Microsoft.Extensions.Logging;

namespace Sa.Outbox.PostgreSqlTests;

/// <summary>
/// A logger that keeps what it was told, so a test can assert on a warning the library
/// emitted instead of on its private state.
/// </summary>
/// <remarks>
/// The outbox commands take <c>ILogger&lt;T&gt;?</c> and fall back to
/// <see cref="Microsoft.Extensions.Logging.Abstractions.NullLogger{T}"/>, so a test that
/// cares about a warning has to register one itself. Matching on message text rather than on
/// <see cref="EventId"/> keeps the assertion about the claim being made — "the shortfall was
/// announced" — rather than about which number it was given.
/// </remarks>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly Lock _sync = new();
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries
    {
        get { lock (_sync) return _entries.ToArray(); }
    }

    /// <summary>How many entries at <paramref name="level"/> contain <paramref name="fragment"/>.</summary>
    public int Count(LogLevel level, string fragment)
    {
        lock (_sync)
        {
            var count = 0;
            foreach (var entry in _entries)
            {
                if (entry.Level == level && entry.Message.Contains(fragment, StringComparison.Ordinal)) count++;
            }

            return count;
        }
    }

    public bool Contains(LogLevel level, string fragment) => Count(level, fragment) > 0;

    /// <summary>How many entries are currently held, so a test can assert on a suffix.</summary>
    public int Mark
    {
        get { lock (_sync) return _entries.Count; }
    }

    /// <summary>Entries recorded since <paramref name="mark"/>.</summary>
    public IReadOnlyList<LogEntry> Since(int mark)
    {
        lock (_sync) return _entries.Skip(mark).ToArray();
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);

        lock (_sync)
        {
            _entries.Add(new LogEntry(logLevel, message, exception));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
            // A scope this logger never reads: nothing to unwind.
        }
    }
}

internal readonly record struct LogEntry(LogLevel Level, string Message, Exception? Exception);
