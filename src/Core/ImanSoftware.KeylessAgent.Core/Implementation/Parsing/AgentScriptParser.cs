using System.Text.RegularExpressions;
using ImanSoftware.Extensions;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Models.Parser;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Parsing;

/// <summary>
/// Line-based state-machine parser for AgentScript v1.
/// Not a regex over the whole reply: blocks, payload sections and the <c>@@eof</c> terminator are
/// recognized one line at a time, so file content can safely contain marker-like text mid-line.
/// </summary>
internal sealed partial class AgentScriptParser : IAgentScriptParser
{
    private const string EofKeyword = "@@eof";
    private const string EndKeyword = "@@end";

    private static readonly Dictionary<string, CommandKind> CommandsByKeyword =
        Enum.GetValues<CommandKind>().ToDictionary(k => k.GetDescription(), k => k, StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"^<<<([A-Za-z][A-Za-z0-9]*)(?::(\S+))?$")]
    private static partial Regex PayloadStartRegex();

    /// <inheritdoc />
    public Outcome<AgentScriptParseResult> Parse(string reply)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(reply))
            {
                return Outcome.Successful(new AgentScriptParseResult
                {
                    Problems = [new FormatProblem(null, ErrorCodes.ReplyEmpty, "The reply is empty.")],
                });
            }

            var lines = SplitLines(reply);
            lines = StripSurroundingCodeFence(lines);

            var problems = new List<FormatProblem>();
            var commands = new List<AgentScriptCommand>();
            var eofSeen = false;
            var truncated = false;
            var commandCounter = 0;

            var state = ParseState.TopLevel;
            BlockContext? block = null;
            SectionContext? section = null;

            for (var index = 0; index < lines.Count; index++)
            {
                var line = lines[index];
                var lineNumber = index + 1;
                var trimmed = line.Trim();

                switch (state)
                {
                    case ParseState.TopLevel:
                        if (trimmed.Length == 0)
                            break;

                        if (eofSeen)
                        {
                            problems.Add(new FormatProblem(lineNumber, ErrorCodes.ParseTextAfterEof,
                                $"Nothing is allowed after {EofKeyword}, found \"{Preview(trimmed)}\"."));
                            break;
                        }

                        if (trimmed.StartsWith("<<", StringComparison.Ordinal))
                        {
                            problems.Add(new FormatProblem(lineNumber, ErrorCodes.ParseStrayText,
                                $"Payload marker outside a block: \"{Preview(trimmed)}\"."));
                            break;
                        }

                        if (!trimmed.StartsWith("@@", StringComparison.Ordinal))
                        {
                            problems.Add(new FormatProblem(lineNumber, ErrorCodes.ParseStrayText,
                                $"Text outside a command block: \"{Preview(trimmed)}\"."));
                            break;
                        }

                        if (trimmed.Equals(EofKeyword, StringComparison.OrdinalIgnoreCase))
                        {
                            eofSeen = true;
                            break;
                        }

                        var keyword = trimmed[2..].Split(' ', 2)[0];
                        if (!CommandsByKeyword.TryGetValue(keyword, out var kind))
                        {
                            problems.Add(new FormatProblem(lineNumber, ErrorCodes.ParseUnknownCommand,
                                $"Unknown command \"@@{keyword}\"."));
                            break;
                        }

                        commandCounter++;
                        var argument = trimmed[2..].Split(' ', 2).Length > 1
                            ? trimmed[2..].Split(' ', 2)[1].Trim()
                            : string.Empty;

                        block = new BlockContext(kind, argument, lineNumber, commandCounter);
                        state = ParseState.InBlock;
                        break;

                    case ParseState.InBlock:
                        if (trimmed.Length == 0)
                            break;

                        if (trimmed.Equals(EndKeyword, StringComparison.OrdinalIgnoreCase))
                        {
                            BuildCommand(block!, problems, commands);
                            block = null;
                            state = ParseState.TopLevel;
                            break;
                        }

                        if (trimmed.StartsWith("<<<", StringComparison.Ordinal))
                        {
                            // Lenient one-line payload: <<<NAME text NAME>>> for non-CONTENT
                            // sections. Normalized to the multi-line form.
                            var oneLineSection = TryMatchOneLinePayload(trimmed);
                            if (oneLineSection is not null)
                            {
                                var (oneName, oneText) = oneLineSection.Value;
                                if (block!.Sections.ContainsKey(oneName))
                                {
                                    problems.Add(new FormatProblem(lineNumber, ErrorCodes.ParseUnexpectedPayload,
                                        $"Payload section \"{oneName}\" appears twice in one block."));
                                    break;
                                }

                                block.Sections[oneName] = oneText;
                                state = ParseState.InBlock;
                                break;
                            }

                            var match = PayloadStartRegex().Match(trimmed);
                            if (!match.Success)
                            {
                                problems.Add(new FormatProblem(lineNumber, ErrorCodes.ParseBadMarker,
                                    $"Malformed payload start marker: \"{Preview(trimmed)}\"."));
                                break;
                            }

                            var name = match.Groups[1].Value;
                            var nonce = match.Groups[2].Success ? match.Groups[2].Value : null;
                            if (!SectionNames.IsKnown(name))
                            {
                                problems.Add(new FormatProblem(lineNumber, ErrorCodes.ParseBadMarker,
                                    $"Unknown payload section \"{name}\" (expected MSG, CONTENT, FIND, REPLACE or CMD)."));
                                break;
                            }

                            if (block!.Sections.ContainsKey(name.ToUpperInvariant()))
                            {
                                problems.Add(new FormatProblem(lineNumber, ErrorCodes.ParseUnexpectedPayload,
                                    $"Payload section \"{name}\" appears twice in one block."));
                                break;
                            }

                            // Lenient one-line payload: <<<NAME text NAME>>> for non-CONTENT
                            // sections. Normalized to the multi-line form.
                            if (!name.Equals("CONTENT", StringComparison.OrdinalIgnoreCase))
                            {
                                var openLen = trimmed.IndexOf(name, 3, StringComparison.OrdinalIgnoreCase) + name.Length;
                                var closeMarker = name.ToUpperInvariant() + ">>>";
                                var closeIdx = trimmed.IndexOf(closeMarker, openLen, StringComparison.Ordinal);
                                if (closeIdx >= 0)
                                {
                                    block!.Sections[name.ToUpperInvariant()] = trimmed[openLen..closeIdx].Trim();
                                    state = ParseState.InBlock;
                                    break;
                                }
                            }

                            section = new SectionContext(name.ToUpperInvariant(), nonce);
                            state = ParseState.InPayload;
                            break;
                        }

                        problems.Add(new FormatProblem(lineNumber, ErrorCodes.ParseStrayText,
                            $"Text inside a block but outside a payload section: \"{Preview(trimmed)}\"."));
                        break;

                    case ParseState.InPayload:
                        var expectedClose = section!.CloseMarker;
                        if (trimmed.Equals(expectedClose, StringComparison.Ordinal))
                        {
                            block!.Sections[section.Name] = string.Join("\n", section.Lines);
                            section = null;
                            state = ParseState.InBlock;
                            break;
                        }


                        // Lenient one-line payload: <<<NAME text NAME>>> for non-CONTENT
                        // sections (MSG/FIND/REPLACE/CMD/TO). Normalized to multi-line.
                        if (section.Lines.Count == 0 && !section.Name.Equals("CONTENT", StringComparison.Ordinal))
                        {
                            var oneLine = MatchOneLinePayload(trimmed, section.Name);
                            if (oneLine is not null)
                            {
                                block!.Sections[section.Name] = oneLine;
                                section = null;
                                state = ParseState.InBlock;
                                break;
                            }
                        }
                        section.Lines.Add(line);
                        break;
                }
            }

            if (state != ParseState.TopLevel)
            {
                problems.Add(new FormatProblem(block!.StartLine, ErrorCodes.ParseUnterminatedBlock,
                    $"Block \"{block.Kind.GetDescription()}\" opened on line {block.StartLine} is never closed with {EndKeyword}."));
                truncated = true;
            }

            if (!eofSeen)
            {
                problems.Add(new FormatProblem(null, ErrorCodes.ParseMissingEof,
                    $"Missing \"{EofKeyword}\" at the end (the response looks truncated)."));
            }

            truncated |= !eofSeen;

            return Outcome.Successful(new AgentScriptParseResult
            {
                Commands = problems.Count == 0 ? commands : [],
                Problems = problems,
                EofSeen = eofSeen,
                LooksTruncated = truncated,
            });
        }
        catch (Exception ex)
        {
            return Outcome.Failure<AgentScriptParseResult>(new OutcomeError(ex));
        }
    }

    /// <summary>
    /// Converts a finished block into its command record, adding problems for missing
    /// arguments, missing payloads and unexpected payloads.
    /// </summary>
    private static void BuildCommand(BlockContext block, List<FormatProblem> problems, List<AgentScriptCommand> commands)
    {
        var problemCountBefore = problems.Count;
        string[] required = block.Kind switch
        {
            CommandKind.Say or CommandKind.Ask or CommandKind.Commit or CommandKind.Done => [SectionNames.Msg],
            CommandKind.NewFile or CommandKind.UpdateFile => [SectionNames.Content],
            CommandKind.EditFile => [SectionNames.Find, SectionNames.Replace],
            CommandKind.RunCommand => [SectionNames.Cmd],
            CommandKind.MoveFile => [SectionNames.To],
            _ => [],
        };

        foreach (var name in required)
        {
            if (!block.Sections.TryGetValue(name, out var value) || string.IsNullOrEmpty(value))
            {
                problems.Add(new FormatProblem(block.StartLine, ErrorCodes.ParseMissingPayload,
                    $"Command \"{block.Kind.GetDescription()}\" is missing its required <<<{name} payload."));
            }
        }

        foreach (var extra in block.Sections.Keys.Where(k => !required.Contains(k)))
        {
            problems.Add(new FormatProblem(block.StartLine, ErrorCodes.ParseUnexpectedPayload,
                $"Command \"{block.Kind.GetDescription()}\" does not accept a <<<{extra} payload."));
        }

        var needsArgument = block.Kind is CommandKind.NewFile or CommandKind.UpdateFile
            or CommandKind.EditFile or CommandKind.DeleteFile or CommandKind.ReadFile or CommandKind.MoveFile;

        if (needsArgument && string.IsNullOrWhiteSpace(block.Argument))
        {
            problems.Add(new FormatProblem(block.StartLine, ErrorCodes.ParseMissingArgument,
                $"Command \"{block.Kind.GetDescription()}\" needs a path after the keyword."));
        }

        if (problems.Count > problemCountBefore)
        {
            // The block is malformed; nothing to add — numbering continues with the next block.
            return;
        }

        var payload = block.Sections;
        string? onError = null;
        var argText = block.Argument;
        if (block.Kind == CommandKind.RunCommand && argText.StartsWith("on_error=", StringComparison.OrdinalIgnoreCase))
        {
            var spaceIdx = argText.IndexOf(' ');
            var optionPart = spaceIdx < 0 ? argText : argText[..spaceIdx];
            var value = optionPart["on_error=".Length..].Trim().ToLowerInvariant();
            if (value is not ("continue" or "stop"))
            {
                problems.Add(new FormatProblem(block.StartLine, ErrorCodes.ParseBadOption,
                    $"Unknown on_error value '{value}' (expected continue or stop)."));
                return;
            }

            onError = value;
            argText = spaceIdx < 0 ? string.Empty : argText[(spaceIdx + 1)..].Trim();
        }

        AgentScriptCommand command = block.Kind switch
        {
            CommandKind.Say => new SayCommand(Payload(payload, SectionNames.Msg)),
            CommandKind.NewFile => new NewFileCommand(block.Argument, Payload(payload, SectionNames.Content)),
            CommandKind.UpdateFile => new UpdateFileCommand(block.Argument, Payload(payload, SectionNames.Content)),
            CommandKind.EditFile => new EditFileCommand(block.Argument,
                Payload(payload, SectionNames.Find), Payload(payload, SectionNames.Replace)),
            CommandKind.DeleteFile => new DeleteFileCommand(block.Argument),
            CommandKind.ReadFile => new ReadFileCommand(block.Argument),
            CommandKind.RunCommand => new RunCommandCommand(Payload(payload, SectionNames.Cmd), onError),
            CommandKind.Ask => new AskCommand(Payload(payload, SectionNames.Msg)),
            CommandKind.Commit => new CommitCommand(Payload(payload, SectionNames.Msg)),
            CommandKind.Done => new DoneCommand(Payload(payload, SectionNames.Msg)),
            CommandKind.Next => new NextCommand(Payload(payload, SectionNames.Msg)),
            CommandKind.MoveFile => new MoveFileCommand(block.Argument, Payload(payload, SectionNames.To)),
            _ => throw new NotSupportedException($"Unhandled command kind {block.Kind}."),
        };

        commands.Add(command with { Number = block.Number });
    }

    private static string Payload(Dictionary<string, string> sections, string name)
        => sections.GetValueOrDefault(name, string.Empty);

    private static string Preview(string text)
        => text.Truncate(60);

    /// <summary>
    /// Matches a one-line payload: &lt;&lt;&lt;NAME text NAME&gt;&gt;&gt; for MSG/FIND/REPLACE/CMD/TO
    /// (never CONTENT). Returns the section name and inner text, or null.
    /// </summary>
    private static (string Name, string Text)? TryMatchOneLinePayload(string trimmed)
    {
        if (!trimmed.StartsWith("<<<", StringComparison.Ordinal))
            return null;

        foreach (var sectionName in new[] { "MSG", "FIND", "REPLACE", "CMD", "TO" })
        {
            var openMarker = "<<<" + sectionName;
            if (!trimmed.StartsWith(openMarker, StringComparison.Ordinal))
                continue;

            var rest = trimmed[openMarker.Length..];
            var closeMarker = sectionName + ">>>";
            var closeIdx = rest.IndexOf(closeMarker, StringComparison.Ordinal);
            if (closeIdx < 0)
                continue;

            var innerText = rest[..closeIdx].Trim();
            return (sectionName, innerText);
        }

        return null;
    }

    /// <summary>
    /// Matches the one-line payload form for non-CONTENT sections (MSG/FIND/REPLACE/CMD/TO).
    /// Returns the inner text, or null when the line is not a one-line payload.
    /// </summary>
    private static string? MatchOneLinePayload(string trimmed, string name)
    {
        var openMarker = "<<<" + name;
        var closeMarker = name + ">>>";
        if (!trimmed.StartsWith(openMarker, StringComparison.Ordinal))
            return null;

        var rest = trimmed[openMarker.Length..];
        var closeIndex = rest.IndexOf(closeMarker, StringComparison.Ordinal);
        if (closeIndex < 0)
            return null;

        return rest[..closeIndex].Trim();
    }

    /// <summary>
    /// Splits text into logical lines, accepting CRLF, LF and CR endings.
    /// </summary>
    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return lines;
    }

    /// <summary>
    /// Strips one surrounding Markdown code fence — only when the first non-blank line is an
    /// opening fence and the LAST non-blank line is a closing fence of the same or greater
    /// length. Fences inside CONTENT payloads are never touched (only the outer pair is removed).
    /// </summary>
    private static List<string> StripSurroundingCodeFence(List<string> lines)
    {
        var first = 0;
        while (first < lines.Count && lines[first].Trim().Length == 0) first++;
        var last = lines.Count - 1;
        while (last >= 0 && lines[last].Trim().Length == 0) last--;

        if (first >= last)
            return lines;

        var opener = lines[first].TrimStart();
        if (!opener.StartsWith("```", StringComparison.Ordinal))
            return lines;

        var openerFence = opener.StartsWith("````", StringComparison.Ordinal) ? "````" : "```";
        var closer = lines[last].Trim();
        var minClosing = openerFence.Length; // closing fence must be same or greater length
        if (closer.Length < minClosing || !closer.All(c => c == '`'))
            return lines;

        var stripped = lines.Where((_, i) => i != first && i != last).ToList();
        return stripped;
    }

    private enum ParseState
    {
        TopLevel,
        InBlock,
        InPayload,
    }

    private static class SectionNames
    {
        public const string Msg = "MSG";
        public const string Content = "CONTENT";
        public const string Find = "FIND";
        public const string Replace = "REPLACE";
        public const string Cmd = "CMD";
        public const string To = "TO";

        public static bool IsKnown(string name)
            => name.ToUpperInvariant() is Msg or Content or Find or Replace or Cmd or To;
    }

    private sealed record BlockContext(CommandKind Kind, string Argument, int StartLine, int Number)
    {
        public Dictionary<string, string> Sections { get; } = [];
    }

    private sealed record SectionContext(string Name, string? Nonce)
    {
        public List<string> Lines { get; } = [];

        public string CloseMarker => Nonce is null ? $"{Name}>>>" : $"{Name}:{Nonce}>>>";
    }
}
