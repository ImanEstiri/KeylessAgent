using System.Threading.Channels;
using ImanSoftware.FileStorage;
using Microsoft.Extensions.Logging;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Diagnostics;

/// <summary>
/// A minimal ILoggerProvider that appends structured lines to <c>.agent/logs/diagnostics.log</c>
/// — used in Debug/verbose mode. ILoggerProvider and ILogger.Log are synchronous by contract,
/// so nothing blocks: <see cref="FileLogger.Log"/> only enqueues into a channel and one
/// background task performs all storage I/O through <c>IFileStorage</c>.
/// Never logs cookies, tokens or full page HTML.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly Channel<string> _channel;
    private readonly Task _writerTask;

    public FileLoggerProvider(string workspaceRoot, IFileStorage storage)
    {
        var logPath = Path.Combine(workspaceRoot, ".agent", "logs", "diagnostics.log");
        _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });

        _writerTask = Task.Run(() => WriteLoopAsync(storage, logPath, _channel));
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
        // Complete the channel: the writer drains whatever is queued and then finishes.
        _channel.Writer.TryComplete();
        try
        {
            _writerTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Writer failures are diagnostics noise; never break shutdown.
        }
    }

    private static async Task WriteLoopAsync(IFileStorage storage, string logPath, Channel<string> channel)
    {
        try
        {
            var created = await storage.CreateDirectoryAsync(Path.GetDirectoryName(logPath)!);
            if (created.IsFailure)
                return;

            await foreach (var line in channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var append = await storage.AppendAllTextAsync(logPath, line + Environment.NewLine);
                if (append.IsFailure)
                    return; // storage unusable: drop diagnostics instead of spinning
            }
        }
        catch
        {
            // Diagnostics must never crash the process.
        }
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

            var line = $"{DateTime.UtcNow:O} {logLevel} {categoryName} {formatter(state, exception)}";
            if (exception is not null)
                line += $" | {exception.Message}";

            owner._channel.Writer.TryWrite(line);
        }
    }
}
