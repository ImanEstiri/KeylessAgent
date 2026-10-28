using System.Text.RegularExpressions;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Execution;

/// <summary>
/// Classifies command lines into risk tiers (ReadOnly / Build / Execute / Risky / Denied).
/// Parsing respects quotes; each segment (split on &amp;&amp;, ||, ;, |) is classified separately
/// and the command takes the highest tier. Unknown programs or syntax default to Risky.
/// </summary>
internal sealed partial class CommandRiskClassifier : ICommandRiskClassifier
{
    // program -> tier for known read-only and build invocations
    private static readonly Dictionary<string, CommandRiskTier> KnownPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["git"] = CommandRiskTier.ReadOnly, // write subcommands are promoted below
        ["dotnet"] = CommandRiskTier.ReadOnly,
        ["node"] = CommandRiskTier.ReadOnly,
        ["npm"] = CommandRiskTier.ReadOnly,
        ["python"] = CommandRiskTier.ReadOnly,
        ["ipconfig"] = CommandRiskTier.ReadOnly,
        ["hostname"] = CommandRiskTier.ReadOnly,
        ["whoami"] = CommandRiskTier.ReadOnly,
        ["ver"] = CommandRiskTier.ReadOnly,
        ["where"] = CommandRiskTier.ReadOnly,
        ["dir"] = CommandRiskTier.ReadOnly,
        ["ls"] = CommandRiskTier.ReadOnly,
        ["tree"] = CommandRiskTier.ReadOnly,
        ["type"] = CommandRiskTier.ReadOnly,
        ["cat"] = CommandRiskTier.ReadOnly,
        ["more"] = CommandRiskTier.ReadOnly,
        ["findstr"] = CommandRiskTier.ReadOnly,
        ["grep"] = CommandRiskTier.ReadOnly,
        ["rg"] = CommandRiskTier.ReadOnly,
        ["npx"] = CommandRiskTier.ReadOnly,
        ["echo"] = CommandRiskTier.ReadOnly,
    };

    private static readonly HashSet<string> ReadOnlyGitSubcommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "log", "diff", "show", "rev-parse", "describe", "ls-files", "version", "--version", "-v",
        "remote", "branch --list", "branch --show-current", "branch -vv", "remote -v",
    };

    private static readonly HashSet<string> BuildDotnetSubcommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "build", "restore", "clean",
    };

    private static readonly HashSet<string> ExecutePrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "dotnet", "npm", "node", "python", "npx",
    };

    private static readonly string[] DenyPrograms =
        ["format", "shutdown", "restart", "diskpart", "fdisk", "mkfs", "poweroff"];

    private static readonly string[] DeleteMovePrograms =
        ["del", "rm", "rmdir", "rd", "move", "ren", "copy", "xcopy", "robocopy"];

    [GeneratedRegex("\"[^\"]*\"|'[^']*'|[^\\s]+")]
    private static partial Regex TokenizerRegex();

    /// <inheritdoc />
    public Outcome<CommandRiskAssessment> Classify(string commandLine, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return Outcome.Successful(new CommandRiskAssessment(
                CommandRiskTier.Denied, [], "empty command line"));
        }

        var segments = SplitSegments(commandLine);
        var segmentTiers = new List<CommandRiskTier>();
        var reason = string.Empty;
        var highest = CommandRiskTier.ReadOnly;

        foreach (var segment in segments)
        {
            var (tier, why) = ClassifySegment(segment, workspaceRoot);
            segmentTiers.Add(tier);
            if (tier > highest || (tier == CommandRiskTier.Denied && highest != CommandRiskTier.Denied))
            {
                highest = tier;
                reason = why;
            }
        }

        return Outcome.Successful(new CommandRiskAssessment(highest, segmentTiers, reason));
    }

    /// <summary>
    /// Splits a command line into segments on &amp;&amp;, ||, ; and | while respecting quotes.
    /// </summary>
    internal static string[] SplitSegments(string commandLine)
    {
        var segments = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuote = false;
        char quote = default;

        foreach (var c in commandLine)
        {
            if (inQuote)
            {
                current.Append(c);
                if (c == quote)
                    inQuote = false;
                continue;
            }

            if (c is '"' or '\'')
            {
                inQuote = true;
                current.Append(c);
                continue;
            }

            if (c == ';' || c == '|')
            {
                segments.Add(current.ToString());
                current.Clear();
                continue;
            }

            if (c == '&')
            {
                // '&&' separates; a single '&' (background) stays in the segment.
                current.Append(c);
                continue;
            }

            current.Append(c);
        }

        segments.Add(current.ToString());

        var final = new List<string>();
        foreach (var segment in segments)
        {
            // split '&&' and '||' pairs
            var parts = System.Text.RegularExpressions.Regex.Split(
                segment, "&&|\\|\\|", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
            final.AddRange(parts.Select(p => p.Trim()).Where(p => p.Length > 0));
        }

        return final.ToArray();
    }

    private (CommandRiskTier Tier, string Reason) ClassifySegment(string segment, string workspaceRoot)
    {
        var tokens = Tokenize(segment);
        if (tokens.Count == 0)
            return (CommandRiskTier.ReadOnly, "empty segment");

        var program = tokens[0].Trim('"');
        var args = tokens.Skip(1).ToList();
        var joined = string.Join(" ", args);

        // Deny list first.
        if (DenyPrograms.Contains(program, StringComparer.OrdinalIgnoreCase))
            return (CommandRiskTier.Denied, $"'{program}' is denied");

        // Shell machinery the segment parser does not model: cmd escape, variable expansion,
        // history expansion, backgrounding, nested shells, here-strings. The classifier is NOT
        // a shell parser, so anything of this shape is Risky before any allow-list is consulted.
        if (IsShellMachinery(segment, program, args))
            return (CommandRiskTier.Risky, "shell escape, variable expansion, backgrounding or a nested shell");

        if (program.Equals("git", StringComparison.OrdinalIgnoreCase) && args.Count > 0)
        {
            var sub = args[0].Trim('-', '"');
            if (IsGitWriteSubcommand(args))
                return (CommandRiskTier.Risky, $"git {args[0]} modifies the repository");
        }

        // Redirection anywhere in the segment → Risky (writes files).
        if (segment.Contains('>') || segment.Contains('<'))
            return (CommandRiskTier.Risky, "redirection writes outside the agent's control");

        // Inline interpreters and command substitution → Risky.
        if ((program.Equals("python", StringComparison.OrdinalIgnoreCase) && args.Any(a => a is "-c" or "--command")) ||
            (program.Equals("node", StringComparison.OrdinalIgnoreCase) && args.Any(a => a is "-e" or "--eval")) ||
            (program is "powershell" or "pwsh" && args.Any(a =>
                a.StartsWith("-command", StringComparison.OrdinalIgnoreCase) ||
                a.StartsWith("-encodedcommand", StringComparison.OrdinalIgnoreCase) ||
                a.StartsWith("-file", StringComparison.OrdinalIgnoreCase))) ||
            segment.Contains("$(", StringComparison.Ordinal) ||
            segment.Contains('`'))
        {
            return (CommandRiskTier.Risky, "inline interpreter or command substitution");
        }

        // Package installs and network downloads → Risky.
        if ((program.Equals("npm", StringComparison.OrdinalIgnoreCase) && args.Any(a => a is "install" or "i" or "update")) ||
            (program.Equals("pip", StringComparison.OrdinalIgnoreCase) && args.Any(a => a.StartsWith("install", StringComparison.OrdinalIgnoreCase))) ||
            (program.Equals("dotnet", StringComparison.OrdinalIgnoreCase) && args.Any(a => a.Equals("add", StringComparison.OrdinalIgnoreCase))) ||
            args.Any(a => a.Contains("curl", StringComparison.OrdinalIgnoreCase) || a.Contains("wget", StringComparison.OrdinalIgnoreCase)) ||
            (program is "curl" or "wget"))
        {
            return (CommandRiskTier.Risky, "installs packages or downloads from the network");
        }

        // Delete/move/copy tools → Risky.
        if (DeleteMovePrograms.Contains(program, StringComparer.OrdinalIgnoreCase))
            return (CommandRiskTier.Risky, $"'{program}' deletes, moves or copies files");

        // cmd /c → Risky (can run mutations).
        if (program is "cmd" && args.Any(a => a is "/c" or "/C"))
            return (CommandRiskTier.Risky, "cmd /c can run mutations");

        // Workspace escape: any absolute path or '..' resolving outside the workspace.
        foreach (var token in tokens)
        {
            if (IsPathEscapingWorkspace(token, workspaceRoot))
                return (CommandRiskTier.Risky, $"path '{token}' reaches outside the workspace");
        }

        // Known programs and their subcommand tiers.
        if (KnownPrograms.ContainsKey(program))
        {
            if (program.Equals("git", StringComparison.OrdinalIgnoreCase) && args.Count > 0)
            {
                var sub = args[0].Trim('"', '-');

                // Read-only git subcommands
                if (ReadOnlyGitSubcommands.Contains(args[0].Trim('"')) || ReadOnlyGitSubcommands.Contains(sub))
                    return (CommandRiskTier.ReadOnly, "read-only git subcommand");

                // git branch is read-only when listing, risky when creating/deleting
                if (sub.Equals("branch", StringComparison.OrdinalIgnoreCase))
                {
                    var branchArgs = args.Skip(1).ToList();
                    if (branchArgs.Count == 0 || branchArgs.All(a =>
                        a.StartsWith("--", StringComparison.Ordinal) || a is "-vv" or "-a" or "-r"))
                        return (CommandRiskTier.ReadOnly, "git branch listing is read-only");
                    return (CommandRiskTier.Risky, "git branch creates/deletes branches");
                }

                return (CommandRiskTier.Risky, $"git '{args[0]}' is not a known read-only subcommand");
            }

            if (program.Equals("dotnet", StringComparison.OrdinalIgnoreCase) && args.Count > 0)
            {
                var sub = args[0].Trim('"', '-');
                if (BuildDotnetSubcommands.Contains(sub))
                    return (CommandRiskTier.Build, "workspace-scoped build tooling");
                if (ExecuteDotnetSubcommands.Contains(sub))
                    return (CommandRiskTier.Execute, "runs project code or tests");
            }

            if (ExecutePrograms.Contains(program) && program != "dotnet")
            {
                // node/npm/python: version flags, bare ls/list, and no-args are read-only.
                if (args.Count == 0 || args.All(a => a is "-v" or "--version" || a.StartsWith("-")))
                    return (CommandRiskTier.ReadOnly, "version/info flag");

                if (program.Equals("npm", StringComparison.OrdinalIgnoreCase) &&
                    args.All(a => a is "ls" or "list" or "-g" or "--global" or "--depth=0"))
                    return (CommandRiskTier.ReadOnly, "npm ls/list is read-only");

                // npx fetches and runs an arbitrary package: a bare argument is never ReadOnly
                // or Execute — the allow-list flow (AutoSafe) is the only auto path for it.
                if (program.Equals("npx", StringComparison.OrdinalIgnoreCase) &&
                    args.Any(a => !a.StartsWith("-")))
                    return (CommandRiskTier.Risky, "npx downloads and runs an arbitrary package");

                return (CommandRiskTier.Execute, $"runs project code ({program})");
            }

            return (CommandRiskTier.ReadOnly, "known read-only program");
        }

        // Unknown program → conservative Risky.
        return (CommandRiskTier.Risky, $"unknown program '{program}' (conservative default)");
    }

    private static readonly HashSet<string> ExecuteDotnetSubcommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "test", "run", "watch", "exec",
    };

    private static bool IsShellMachinery(string segment, string program, List<string> args)
    {
        // % and ^ anywhere (even quoted — cmd expands %VAR% inside double quotes), history
        // expansion, a single & (segment splitting already removed && pairs, so any remaining
        // & backgrounds the pipeline), and here-string markers.
        if (segment.Contains('^') || segment.Contains('%') ||
            segment.Contains("!!", StringComparison.Ordinal) ||
            segment.Contains('&') ||
            segment.Contains("@\"", StringComparison.Ordinal) ||
            segment.Contains("@'", StringComparison.Ordinal))
            return true;

        if (program is "start" or "powershell" or "pwsh")
            return true;

        if (program is "cmd" && args.Any(a => a is "/c" or "/C" or "/k" or "/K"))
            return true;

        return false;
    }

    private static readonly string[] CmdFlagBodies =
    [
        "c", "k", "s", "q", "b", "e", "a", "o", "t", "f", "p", "d", "u", "v",
        "x", "y", "i", "l", "g", "n", "r", "w", "z", "j", "m", "h", "all", "ir",
    ];

    /// <summary>
    /// Whether a <c>/…</c> token is a known cmd.exe-style flag. Anything else starting with
    /// <c>/</c> is treated as a (Unix-style) path and checked for workspace escape —
    /// <c>/etc/passwd</c> must not be skipped the way <c>/q</c> is.
    /// </summary>
    private static bool IsCmdStyleFlag(string token)
    {
        if (token.Length < 2 || token[0] != '/')
            return false;

        var body = token[1..];
        return CmdFlagBodies.Any(flag =>
            body.StartsWith(flag, StringComparison.OrdinalIgnoreCase) &&
            (body.Length == flag.Length || !char.IsLetter(body[flag.Length])));
    }

    private static bool IsGitWriteSubcommand(List<string> args)
    {
        if (args.Count == 0)
            return false;

        var sub = args[0].Trim('"');
        string[] write = ["add", "commit", "checkout", "switch", "reset", "clean", "stash", "merge", "rebase", "push", "config"];
        if (write.Contains(sub, StringComparer.OrdinalIgnoreCase))
            return true;

        return args.Any(a => a.Equals("-c", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPathEscapingWorkspace(string token, string workspaceRoot)
    {
        // Skip -flags and known cmd.exe-style /flags; a /token that is NOT a known flag is a
        // (Unix) path and must be checked for escape.
        if (token.StartsWith("-") || IsCmdStyleFlag(token))
            return false;
        if (!(token.Contains("\\") || token.Contains("/")))
            return false;
        if (token.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            if (Path.IsPathRooted(token))
            {
                var full = Path.GetFullPath(token);
                var root = Path.GetFullPath(workspaceRoot);
                return !full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }

            if (token.Split('/', '\\').Any(s => s == ".."))
            {
                var full = Path.GetFullPath(Path.Combine(workspaceRoot, token));
                var rootFull = Path.GetFullPath(workspaceRoot);
                return !full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            return true; // unresolvable → conservative
        }

        return false;
    }

    private static List<string> Tokenize(string segment)
    {
        var tokens = new List<string>();
        foreach (Match m in TokenizerRegex().Matches(segment))
            tokens.Add(m.Value);

        return tokens;
    }
}
