using ImanSoftware.Extensions;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Execution;

/// <summary>
/// Deny list nothing can pass (destructive system commands, force push, global git config,
/// pipe-into-shell downloads…) plus the configurable allow list used by <c>AutoSafe</c> mode.
/// </summary>
internal sealed class CommandPolicy : ICommandPolicy
{
    private static readonly string[] DeniedPrograms =
    [
        "format", "shutdown", "restart", "diskpart", "fdisk", "mkfs", "poweroff",
    ];

    private static readonly string[] DeniedLinePatterns =
    [
        "git config --global",
        "git config --system",
        "git push --force",
        "git push -f",
        "git branch -d",
        "git checkout -- .",
    ];

    /// <inheritdoc />
    public Outcome Validate(string commandLine, CommandsOptions options)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return Outcome.Failure(new OutcomeError(
                "The command line is empty.", ErrorCodes.CommandDenied, OutcomeErrorType.Validation));
        }

        var whole = commandLine.Trim();
        if (PipesIntoShell(whole))
        {
            return Deny(whole, "piping a download into a shell is not allowed");
        }

        foreach (var pattern in DeniedLinePatterns)
        {
            if (whole.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return Deny(whole, $"\"{pattern}\" is not allowed");
        }

        foreach (var segment in SplitSegments(whole))
        {
            var program = ExtractProgram(segment);
            if (program.Length == 0)
                continue;

            if (DeniedPrograms.Contains(program, StringComparer.OrdinalIgnoreCase))
                return Deny(segment, $"\"{program}\" is never allowed");

            if (program is "rm" or "rmdir" or "rd" && segment.Contains("/s", StringComparison.OrdinalIgnoreCase) ||
                program is "rm" && segment.Contains("-rf", StringComparison.Ordinal) &&
                segment.Contains("/ ", StringComparison.Ordinal))
            {
                return Deny(segment, "recursive deletes of root folders are not allowed");
            }

            if (program is "del" or "erase" && segment.Contains("/s", StringComparison.OrdinalIgnoreCase))
                return Deny(segment, "recursive deletes are not allowed");

            if (program is "powershell" or "pwsh" or "cmd" &&
                segment.Contains("-enc", StringComparison.OrdinalIgnoreCase))
            {
                return Deny(segment, "encoded commands are not allowed");
            }
        }

        return Outcome.Successful();
    }

    /// <inheritdoc />
    public bool IsAllowlisted(string commandLine, CommandsOptions options)
    {
        var program = ExtractProgram(commandLine.Trim());
        return options.Allowlist.Any(allowed =>
            string.Equals(allowed.Trim(), program, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Splits a command line into the segments the shell would run:
    /// on <c>&amp;&amp;</c>, <c>||</c>, <c>;</c> and <c>|</c>, respecting quotes (a pipe inside
    /// a quoted filter is not a separator).
    /// </summary>
    internal static string[] SplitSegments(string commandLine)
        => ShellSegmenter.Split(commandLine);

    private static string ExtractProgram(string segment)
    {
        var token = segment.Split(' ', 2)[0].Trim('"', '\'');
        return token;
    }

    private static bool PipesIntoShell(string commandLine)
    {
        var lower = commandLine.ToLowerInvariant();
        return (lower.Contains("curl") || lower.Contains("wget") || lower.Contains("invoke-webrequest") || lower.Contains("iwr")) &&
               (lower.Contains("| sh") || lower.Contains("|sh") || lower.Contains("| bash") || lower.Contains("|bash") ||
                lower.Contains("| powershell") || lower.Contains("|powershell") || lower.Contains("| cmd") || lower.Contains("|cmd"));
    }

    private static Outcome Deny(string segment, string reason)
        => Outcome.Failure(new OutcomeError(
            $"Command denied ({reason}): \"{segment.Truncate(120)}\".",
            ErrorCodes.CommandDenied, OutcomeErrorType.Validation));
}
