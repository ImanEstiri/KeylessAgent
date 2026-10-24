using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Models.Execution;

namespace ImanSoftware.KeylessAgent.Abstractions.Services;

/// <summary>
/// Builds the <c>[KEYLESSAGENT RESULTS]</c> message sent back to DeepSeek after each executed batch.
/// Every result maps output to the command's number, name and target.
/// </summary>
public interface IResultFormatter : IService
{
    /// <summary>
    /// Formats one results message.
    /// </summary>
    /// <param name="turn">The 1-based user-request number within the session.</param>
    /// <param name="batch">The 1-based response number within the turn.</param>
    /// <param name="results">The executed commands' results, including skipped ones.</param>
    /// <param name="batchLimit">The per-response command limit (for the skipped note).</param>
    /// <param name="limits">The configured limits, for output capping.</param>
    /// <returns>The complete message text.</returns>
    string Format(int turn, int batch, IReadOnlyList<CommandExecutionResult> results, int batchLimit, LimitsOptions limits);
}
