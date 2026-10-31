using System.Reflection;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Agent;

/// <summary>
/// Builds the system prompt from the embedded <c>system-prompt.md</c>, injecting the
/// configured limits through <c>{{PLACEHOLDER}}</c> tokens.
/// </summary>
internal static class SystemPromptBuilder
{
    private const string EmbeddedResourceName = "ImanSoftware.KeylessAgent.Core.Resources.system-prompt.md";

    /// <summary>
    /// Builds the system prompt for the given configuration.
    /// </summary>
    /// <param name="options">The effective configuration.</param>
    /// <returns>The prompt text, or a failure when the embedded resource is missing.</returns>
    public static Outcome<string> Build(KeylessOptions options)
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

        return Outcome.Successful(template
            .Replace("{{MAX_COMMANDS_PER_RESPONSE}}", options.Limits.MaxCommandsPerResponse.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{MAX_RESPONSE_CHARS}}", options.Limits.MaxResponseChars.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{MAX_FILE_CONTENT_CHARS}}", options.Limits.MaxFileContentChars.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{MAX_COMMAND_OUTPUT_CHARS}}", options.Limits.MaxCommandOutputChars.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{MAX_ITERATIONS_PER_TURN}}", options.Limits.MaxIterationsPerTurn.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{BRANCH_PREFIX}}", options.Git.BranchPrefix, StringComparison.Ordinal)
            .Replace("{{PROTECTED_BRANCH_PATTERNS}}", string.Join(", ", options.Git.ProtectedBranchPatterns), StringComparison.Ordinal));
    }
}
