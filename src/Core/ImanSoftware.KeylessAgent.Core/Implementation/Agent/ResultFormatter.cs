using System.Globalization;
using ImanSoftware.Extensions;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Models.Execution;
using ImanSoftware.KeylessAgent.Abstractions.Services;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Agent;

/// <summary>
/// Builds the <c>[KEYLESSAGENT RESULTS]</c> message: one block per result, each mapping
/// output to the command's number, name and target, plus the skipped-commands note.
/// </summary>
internal sealed class ResultFormatter : IResultFormatter
{
    /// <inheritdoc />
    public string Format(int turn, int batch, IReadOnlyList<CommandExecutionResult> results, int batchLimit, LimitsOptions limits)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"[KEYLESSAGENT RESULTS] turn={turn} batch={batch}");

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            builder.AppendLine(CultureInfo.InvariantCulture, $"--- result {i + 1}/{results.Count} | #{result.Number} {CommandName(result.Kind)} | {TrimTarget(result.Target)} ---");

            switch (result.Status)
            {
                case CommandStatus.Skipped:
                    builder.AppendLine("status: skipped");
                    builder.AppendLine(CultureInfo.InvariantCulture, $"reason: {result.ErrorMessage ?? "batch limit reached"}");
                    break;

                case CommandStatus.Error:
                    builder.AppendLine("status: error");
                    builder.AppendLine(CultureInfo.InvariantCulture, $"reason: {result.ErrorMessage ?? "unknown error"}");
                    break;

                default:
                    builder.AppendLine("status: ok");
                    break;
            }

            if (result.ExitCode.HasValue)
                builder.Append("exit code: ").Append(result.ExitCode.Value).AppendLine(result.DurationSeconds.HasValue ? $" | duration: {result.DurationSeconds.Value:F1}s" : string.Empty);

            if (!string.IsNullOrEmpty(result.Output))
            {
                var output = CapOutput(result.Output, limits.MaxCommandOutputChars);
                builder.AppendLine("<<<OUTPUT");
                builder.AppendLine(output);
                builder.AppendLine("OUTPUT>>>");
            }
        }

        var skipped = results.Count(r => r.Status == CommandStatus.Skipped);
        if (skipped > 0)
            builder.AppendLine(CultureInfo.InvariantCulture, $"--- skipped {skipped} commands: batch limit is {batchLimit} per response, resend them next ---");

        builder.AppendLine("[END RESULTS]");
        builder.Append("Continue with the next commands, or finish with @@done. Reply in AgentScript v1 only.");
        return builder.ToString();
    }

    /// <summary>
    /// Keeps the head and tail halves of the cap and marks the cut.
    /// </summary>
    internal static string CapOutput(string output, int maxChars)
    {
        if (output.Length <= maxChars)
            return output.TrimEnd('\r', '\n');

        var head = maxChars / 2;
        var tail = maxChars - head;
        var cut = output.Length - head - tail;
        return $"{output[..head].TrimEnd()}\n[... {cut} characters truncated ...]\n{output[^tail..].TrimStart()}";
    }

    private static string CommandName(CommandKind kind)
        => kind.GetDescription();

    private static string TrimTarget(string target)
        => target.NormalizeWhitespace().Truncate(80);
}
