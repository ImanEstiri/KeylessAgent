using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Models.Parser;

namespace ImanSoftware.KeylessAgent.Abstractions.Models.Execution;

/// <summary>
/// The outcome of one executed AgentScript command, reported back to the model in the results message.
/// </summary>
public sealed record CommandExecutionResult
{
    /// <summary>
    /// The position of the command in its response (matches the <c>#N</c> in result headers).
    /// </summary>
    public int Number { get; init; }

    /// <summary>
    /// Which command this was.
    /// </summary>
    public CommandKind Kind { get; init; }

    /// <summary>
    /// Path, command line, or message excerpt identifying the target.
    /// </summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>
    /// Ok, Error (failed / blocked / declined) or Skipped (batch limit reached).
    /// </summary>
    public CommandStatus Status { get; init; }

    /// <summary>
    /// The stable <c>KA_</c> code on failure; null otherwise.
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Human-readable failure reason; null otherwise.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Output for the model: file content for <c>readfile</c>, the user's answer for <c>ask</c>,
    /// process output for <c>runcommand</c>. Capped by <c>Limits.MaxCommandOutputChars</c> when formatted.
    /// </summary>
    public string? Output { get; init; }

    /// <summary>
    /// Process exit code for <c>runcommand</c>; null otherwise.
    /// </summary>
    public int? ExitCode { get; init; }

    /// <summary>
    /// Wall-clock duration in seconds for <c>runcommand</c>; null otherwise.
    /// </summary>
    public double? DurationSeconds { get; init; }

    /// <summary>
    /// Builds a successful result.
    /// </summary>
    public static CommandExecutionResult Ok(AgentScriptCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return new() { Number = command.Number, Kind = command.Kind, Target = command.Target, Status = CommandStatus.Ok };
    }

    /// <summary>
    /// Builds a failed result from an outcome error.
    /// </summary>
    public static CommandExecutionResult Fail(AgentScriptCommand command, string code, string message)
    {
        ArgumentNullException.ThrowIfNull(command);
        return new() { Number = command.Number, Kind = command.Kind, Target = command.Target, Status = CommandStatus.Error, ErrorCode = code, ErrorMessage = message };
    }
}
