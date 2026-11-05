using System.Globalization;
using ImanSoftware.Extensions;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Models.Execution;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Core.Implementation.Execution;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Agent;

/// <summary>
/// Builds the <c>[KEYLESSAGENT RESULTS]</c> message: one block per result, each mapping
/// output to the command's number, name and target, plus the skipped-commands note.
/// </summary>
internal sealed class ResultFormatter : IResultFormatter
{
    /// <inheritdoc />
    public string Format(int turn, int batch, IReadOnlyList<CommandExecutionResult> results, int batchLimit, LimitsOptions limits, string workspaceRoot)
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

            var verified = VerifyOnDisk(result, workspaceRoot);
            if (verified is not null)
                builder.AppendLine(verified);

            if (!string.IsNullOrEmpty(result.Output))
            {
                // Caps count characters of the normalized text: CRLF files must not double-count.
                var output = CapOutput(LineEndings.NormalizeToLf(result.Output), limits.MaxCommandOutputChars);
                builder.AppendLine("<<<OUTPUT");
                builder.AppendLine(output);
                builder.AppendLine("OUTPUT>>>");
            }
        }

        // The "resend them next" hint only applies to batch-limit skips: a command skipped
        // after a failure or after @@done must NOT be resent by the model.
        var batchLimitSkips = results.Count(r => r.Status == CommandStatus.Skipped &&
            r.ErrorMessage is not null && r.ErrorMessage.StartsWith("batch limit", StringComparison.Ordinal));
        if (batchLimitSkips > 0)
            builder.AppendLine(CultureInfo.InvariantCulture, $"--- skipped {batchLimitSkips} commands: batch limit is {batchLimit} per response, resend them next ---");

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


    /// <summary>
    /// The compact "verified:" line the model can trust after a file-changing command:
    /// path, byte count, line count, an 8-hex SHA-256 prefix and the first/last 60
    /// characters (newlines escaped). Never includes the full content; always under
    /// 300 characters. Null when the file is not on disk (dry-run, failure).
    /// </summary>
    internal static string? VerifyOnDisk(CommandExecutionResult result, string workspaceRoot)
    {
        if (result.Status != CommandStatus.Ok)
            return null;

        if (result.Kind == CommandKind.Mkdir)
            return Directory.Exists(Path.Combine(workspaceRoot, result.Target))
                ? "verified: dir exists"
                : null;

        if (result.Kind is not (CommandKind.NewFile or CommandKind.UpdateFile or CommandKind.EditFile or CommandKind.MoveFile))
            return null;

        // movefile's Target is the OLD path; the destination travels in the output
        // ("Moved <from> -> <to>."). Other kinds verify the target itself.
        var relativePath = result.Kind == CommandKind.MoveFile
            ? ParseMovedTo(result.Output)
            : result.Target;
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        var fullPath = Path.Combine(workspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
            return null;

        var info = new FileInfo(fullPath);
        using var stream = File.OpenRead(fullPath);
        var shaBytes = System.Security.Cryptography.SHA256.HashData(stream);
        var sha = Convert.ToHexString(shaBytes)[..8];

        var content = LineEndings.NormalizeToLf(File.ReadAllText(fullPath));
        var newLine = new string(Convert.ToChar(10), 1);
        var lines = content.Length == 0
            ? 0
            : content.Count(c => c == Convert.ToChar(10)) + (content.EndsWith(newLine, StringComparison.Ordinal) ? 0 : 1);

        var first = Snippet(content, first: true);
        var last = Snippet(content, first: false);
        var text = $"verified: path={relativePath.Truncate(60)} bytes={info.Length} lines={lines} sha={sha} first=\"{first}\" last=\"{last}\"";
        return text.Length <= 300 ? text : text[..299] + '"';
    }

    /// <summary>The first/last 60 characters of the content, newlines shown as the two-character escape.</summary>
    private static string Snippet(string content, bool first)
    {
        var lf = Convert.ToChar(10);
        var escape = string.Concat(Convert.ToChar(92), 'n');
        var text = content.TrimEnd(lf).Replace(new string(lf, 1), escape);
        if (text.Length == 0)
            return string.Empty;
        return first
            ? text.Length <= 60 ? text : text[..60]
            : text.Length <= 60 ? text : text[^60..];
    }

    /// <summary>Extracts the destination from a movefile result's "Moved <from> -> <to>." output.</summary>
    private static string? ParseMovedTo(string? output)
    {
        if (string.IsNullOrEmpty(output))
            return null;

        var marker = " -> ";
        var start = output.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return null;

        var end = output.IndexOf('.', start + marker.Length);
        return end < 0 ? null : output[(start + marker.Length)..end].Trim();
    }

    private static string CommandName(CommandKind kind)
        => kind.GetDescription();

    private static string TrimTarget(string target)
        => target.NormalizeWhitespace().Truncate(80);
}
