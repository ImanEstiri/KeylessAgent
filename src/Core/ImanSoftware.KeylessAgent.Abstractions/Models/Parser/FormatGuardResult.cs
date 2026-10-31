namespace ImanSoftware.KeylessAgent.Abstractions.Models.Parser;

/// <summary>
/// The verdict of the response format guard: either a valid, executable script,
/// or the list of problems to send back to the model in a correction message.
/// </summary>
public sealed record FormatGuardResult
{
    /// <summary>
    /// True when the reply is valid AgentScript and may be executed.
    /// </summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// The parsed script when <see cref="IsValid"/> is true; otherwise <see langword="null"/>.
    /// </summary>
    public AgentScriptParseResult? Script { get; init; }

    /// <summary>
    /// Every problem found (grammar and limits), for the correction message.
    /// </summary>
    public IReadOnlyList<FormatProblem> Problems { get; init; } = [];

    /// <summary>
    /// Whether the reply looks truncated; the correction message then tells the model
    /// to resend in smaller pieces.
    /// </summary>
    public bool LooksTruncated { get; init; }
}
