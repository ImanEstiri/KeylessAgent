using System.Reflection;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Parser;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Agent;

/// <summary>
/// Builds the format-correction message from the embedded <c>format-correction.txt</c> template.
/// </summary>
internal static class CorrectionMessageBuilder
{
    private const string EmbeddedResourceName = "ImanSoftware.KeylessAgent.Core.Resources.format-correction.txt";

    /// <summary>
    /// Fills the template with the guard's problems (and truncation advice when the reply looked cut off).
    /// </summary>
    /// <param name="problems">Every problem found in the rejected reply.</param>
    /// <param name="looksTruncated">Whether the reply appears cut off.</param>
    /// <returns>The correction message to send back to the model.</returns>
    public static Outcome<string> Build(IReadOnlyList<FormatProblem> problems, bool looksTruncated)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName);
        if (stream is null)
        {
            return Outcome.Failure<string>(new OutcomeError(
                $"Embedded resource {EmbeddedResourceName} is missing.", ErrorCodes.Internal, OutcomeErrorType.Problem));
        }

        using var reader = new StreamReader(stream);
        var template = reader.ReadToEnd();

        var problemLines = string.Join("\n", problems.Select(p => $"- {p.ToDisplayString()}"));
        var truncationAdvice = looksTruncated
            ? "Your response looks truncated. Resend it in SMALLER pieces (fewer commands per response)."
            : string.Empty;

        return Outcome.Successful(template
            .Replace("{problems}", problemLines, StringComparison.Ordinal)
            .Replace("{truncationAdvice}", truncationAdvice, StringComparison.Ordinal)
            .TrimEnd());
    }
}
