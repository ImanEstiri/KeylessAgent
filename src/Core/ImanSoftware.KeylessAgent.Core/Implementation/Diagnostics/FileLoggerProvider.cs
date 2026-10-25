using ImanSoftware.DateTimeProvider;
using ImanSoftware.FileStorage;
using Microsoft.Extensions.Logging;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Diagnostics;

/// <summary>
/// A minimal ILoggerProvider that appends structured lines to <c>.agent/logs/diagnostics.log</c>
/// through IFileStorage — used in Debug mode. Never logs cookies, tokens or full page HTML.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logPath;

    public FileLoggerProvider(string workspaceRoot, IFileStorage storage, ImanSoftware.DateTimeProvider.IDateTimeProvider time)
    {
        var directory = Path.Combine(workspaceRoot, ".agent", "logs");
        _logPath = Path.Combine(directory, "diagnostics.log");
        storage.CreateDirectoryAsync(directory).GetAwaiter().GetResult();
        Storage = storage;
        Time = time;
    }

    private IFileStorage Storage { get; }

    private ImanSoftware.DateTimeProvider.IDateTimeProvider Time { get; }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private sealed class FileLogger(FileLoggerProvider owner, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var line = $"{owner.Time.Now.ToUniversalTime():O} {logLevel} {categoryName} {formatter(state, exception)}";
            if (exception is not null)
                line += $" | {exception.Message}";

            owner.Storage.AppendAllTextAsync(owner._logPath, line + Environment.NewLine).GetAwaiter().GetResult();
        }
    }
}
