using Microsoft.Extensions.Logging;

namespace NetSplit.Service;

/// <summary>
/// Minimal, dependency-free rolling-file log sink: one file per UTC day
/// under the given directory, appended to directly (no in-process
/// buffering) so a crash never loses a log line that was already written.
/// Deliberately hand-rolled rather than pulling in Serilog/NLog - this
/// product's logging volume (service lifecycle events, rule changes,
/// driver health) doesn't need a full logging framework's feature set, and
/// this class is the single, obvious place to swap in one later (or add an
/// ETW-backed ILoggerProvider alongside it) without touching any call site
/// - every component logs through the standard ILogger&lt;T&gt; abstraction,
/// never against this class directly.
/// </summary>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly object _gate = new();

    public RollingFileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    internal void WriteLine(string line)
    {
        string path = Path.Combine(_directory, $"netsplit-{DateTime.UtcNow:yyyy-MM-dd}.log");
        lock (_gate)
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    public void Dispose()
    {
    }

    private sealed class FileLogger(string category, RollingFileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            string message = formatter(state, exception);
            string line = $"{DateTimeOffset.UtcNow:O} [{logLevel,-11}] {category}: {message}";
            if (exception != null)
            {
                line += Environment.NewLine + exception;
            }

            provider.WriteLine(line);
        }
    }
}
