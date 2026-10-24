namespace ImanSoftware.KeylessAgent.Abstractions.Models.Parser;

/// <summary>
/// The full result of parsing one model reply: the commands when the reply was valid,
/// and every grammar problem when it was not.
/// </summary>
public sealed record AgentScriptParseResult
{
    /// <summary>
    /// The parsed commands in response order. Empty when <see cref="Problems"/> is not empty.
    /// </summary>
    public IReadOnlyList<AgentScriptCommand> Commands { get; init; } = [];

    /// <summary>
    /// Every grammar problem found, with line numbers. An empty list means the reply was valid.
    /// </summary>
    public IReadOnlyList<FormatProblem> Problems { get; init; } = [];

    /// <summary>
    /// Whether a final <c>@@eof</c> line was seen.
    /// </summary>
    public bool EofSeen { get; init; }

    /// <summary>
    /// Whether the reply had a valid start but appears cut off (no <c>@@eof</c> or an open block).
    /// </summary>
    public bool LooksTruncated { get; init; }

    /// <summary>
    /// True when <see cref="Problems"/> is empty.
    /// </summary>
    public bool IsValid => Problems.Count == 0;
}
