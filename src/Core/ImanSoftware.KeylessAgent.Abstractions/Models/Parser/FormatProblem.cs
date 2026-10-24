namespace ImanSoftware.KeylessAgent.Abstractions.Models.Parser;

/// <summary>
/// One problem found in a model reply, reported to the model in the format-correction message.
/// </summary>
/// <param name="Line">The 1-based line the problem was detected on, or <see langword="null"/> when not tied to a line.</param>
/// <param name="Code">The stable <c>KA_</c> error code (see <c>docs/agentscript-spec.md</c>).</param>
/// <param name="Message">Human-readable description of the problem.</param>
public sealed record FormatProblem(int? Line, string Code, string Message)
{
    /// <summary>
    /// Returns the problem formatted for the correction message, e.g. <c>line 14: unknown command "@@createfile"</c>.
    /// </summary>
    public string ToDisplayString()
        => Line is null ? Message : $"line {Line}: {Message}";
}
