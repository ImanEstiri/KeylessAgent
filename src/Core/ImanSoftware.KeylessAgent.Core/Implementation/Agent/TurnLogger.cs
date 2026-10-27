using ImanSoftware.DateTimeProvider;
using ImanSoftware.FileStorage;
using ImanSoftware.KeylessAgent.Abstractions.Models.Execution;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Core.Implementation.Configuration;
using ImanSoftware.Logging;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Agent;

/// <summary>
/// Writes the per-turn transcript as JSON lines to <c>.agent/logs/turn-&lt;n&gt;.jsonl</c>:
/// every message sent, every reply received, every parsed command and every result.
/// Never logs cookies, tokens or page HTML.
/// </summary>
internal sealed class TurnLogger
{
    private readonly IFileStorage _storage;
    private readonly IDateTimeProvider _time;

    public TurnLogger(IFileStorage storage, IDateTimeProvider time)
    {
        _storage = storage;
        _time = time;
    }

    internal string LogPath { get; private set; } = string.Empty;

    /// <summary>
    /// Starts a new log file for the turn.
    /// </summary>
    /// <param name="workspaceRoot">The workspace directory.</param>
    /// <param name="turn">The 1-based turn number.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task StartAsync(string workspaceRoot, int turn, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(workspaceRoot, ".agent", "logs");
        LogPath = Path.Combine(directory, $"turn-{turn}.jsonl");
        await _storage.CreateDirectoryAsync(directory, cancellationToken);
    }

    /// <summary>
    /// Appends one event to the log.
    /// </summary>
    /// <param name="type">The event type (message-sent, reply-received, command, result, note).</param>
    /// <param name="payload">The event payload object, serialized as JSON.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task AppendAsync(string type, object payload, CancellationToken cancellationToken = default)
    {
        if (LogPath.Length == 0)
            return;

        var entry = new
        {
            TimestampUtc = _time.Now.ToUniversalTime().ToString("O"),
            Type = type,
            Payload = payload,
        };

        var json = ConfigurationService.CreateSerializer().Serialize(entry);
        await _storage.AppendAllTextAsync(LogPath, json + Environment.NewLine, cancellationToken);
    }

    /// <summary>
    /// Appends one command result to the log.
    /// </summary>
    /// <param name="result">The executed command's result.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task AppendResultAsync(CommandExecutionResult result, CancellationToken cancellationToken = default)
        => AppendAsync("result", result, cancellationToken);
}
