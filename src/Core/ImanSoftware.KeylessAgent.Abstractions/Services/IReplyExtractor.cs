namespace ImanSoftware.KeylessAgent.Abstractions.Services;

/// <summary>
/// Extracts the final answer from the DOM of one assistant message — and only the final answer.
/// The reasoning block (<c>.ds-think-content</c>) is never returned: it is not AgentScript
/// and feeding it to the parser would break the agent.
/// </summary>
public interface IReplyExtractor : IService
{
    /// <summary>
    /// Reads the final-answer text out of a message's HTML.
    /// </summary>
    /// <param name="messageOuterHtml">The outer HTML of one assistant <c>.ds-message</c> element.</param>
    /// <returns>
    /// The final-answer text with code indentation preserved, or null when the message
    /// contains no final-answer element yet (still thinking).
    /// </returns>
    string? ExtractFinalAnswer(string messageOuterHtml);
}
