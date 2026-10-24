using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Models.Parser;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Abstractions.Services;

/// <summary>
/// Parses a raw model reply into AgentScript v1 commands, or reports every grammar problem with line numbers.
/// </summary>
public interface IAgentScriptParser : IService
{
    /// <summary>
    /// Parses <paramref name="reply"/> (a surrounding Markdown code fence is tolerated and stripped).
    /// </summary>
    /// <param name="reply">The raw reply text read from the chat.</param>
    /// <returns>
    /// A successful outcome carrying the parse result; the result's <see cref="AgentScriptParseResult.Problems"/>
    /// list is empty when the reply is valid AgentScript. The outcome itself only fails on unexpected errors.
    /// </returns>
    Outcome<AgentScriptParseResult> Parse(string reply);
}

/// <summary>
/// Semantic validation of a parsed reply against the configured limits and path rules.
/// </summary>
public interface IAgentScriptValidator : IService
{
    /// <summary>
    /// Checks the parsed reply and the raw text it came from against <paramref name="limits"/>.
    /// </summary>
    /// <param name="parse">The parser result (problems, when present, are passed through unchanged).</param>
    /// <param name="rawReply">The raw reply text, for the total length check.</param>
    /// <param name="limits">The configured limits.</param>
    /// <returns>Problems found; an empty list means the script passes validation.</returns>
    Outcome<IReadOnlyList<FormatProblem>> Validate(AgentScriptParseResult parse, string rawReply, LimitsOptions limits);
}

/// <summary>
/// The single gate every reply passes through before anything is executed:
/// grammar, <c>@@eof</c>, known commands, required payloads, no stray text, size limits.
/// </summary>
public interface IResponseFormatGuard : IService
{
    /// <summary>
    /// Checks one raw reply.
    /// </summary>
    /// <param name="reply">The raw reply text.</param>
    /// <param name="limits">The configured limits.</param>
    /// <returns>The verdict: a valid script, or the problems for the correction message.</returns>
    Outcome<FormatGuardResult> Check(string reply, LimitsOptions limits);
}
