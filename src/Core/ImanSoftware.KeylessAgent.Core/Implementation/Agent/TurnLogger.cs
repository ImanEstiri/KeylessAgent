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
    public void Start(string workspaceRoot, int turn)
    {
        var directory = Path.Combine(workspaceRoot, ".agent", "logs");
        LogPath = Path.Combine(directory, $"turn-{turn}.jsonl");
        _storage.CreateDirectoryAsync(directory).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Appends one event to the log.
    /// </summary>
    /// <param name="type">The event type (message-sent, reply-received, command, result, note).</param>
    /// <param name="payload">The event payload object, serialized as JSON.</param>
    public void Append(string type, object payload)
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
        _storage.AppendAllTextAsync(LogPath, json + Environment.NewLine).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Appends one command result to the log.
    /// </summary>
    /// <param name="result">The executed command's result.</param>
    public void AppendResult(CommandExecutionResult result)
        => Append("result", result);
}
