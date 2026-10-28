using ImanSoftware.Extensions;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Models.Parser;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Parsing;

/// <summary>
/// Semantic validation of a parsed reply: size limits and path shape.
/// Grammar problems from the parser are passed through unchanged so the guard sees one merged list.
/// </summary>
internal sealed class AgentScriptValidator : IAgentScriptValidator
{
    /// <inheritdoc />
    public Outcome<IReadOnlyList<FormatProblem>> Validate(AgentScriptParseResult parse, string rawReply, LimitsOptions limits)
    {
        try
        {
            var problems = new List<FormatProblem>(parse.Problems);

            if (rawReply.Length > limits.MaxResponseChars)
            {
                problems.Add(new FormatProblem(null, ErrorCodes.LimitResponseChars,
                    $"The response is {rawReply.Length} characters; the maximum is {limits.MaxResponseChars}. Split the work across several responses."));
            }

            // Control-directive rules: exactly one of @@next / @@done per reply.
            // These apply to parse-valid replies too, so they run before the early return.
            var hasDone = parse.Commands.Any(c => c is DoneCommand);
            var hasNext = parse.Commands.Any(c => c is NextCommand);
            var hasSay = parse.Commands.Any(c => c is SayCommand);
            var hasOther = parse.Commands.Any(c => c is not (SayCommand or DoneCommand or NextCommand));

            if (hasDone && hasNext)
                problems.Add(new FormatProblem(null, ErrorCodes.ParseConflictingControl,
                    "Both @@next and @@done in one reply — use exactly one control directive."));

            if (hasSay && !hasDone && !hasNext && !hasOther)
                problems.Add(new FormatProblem(null, ErrorCodes.ParseSayOnly,
                    "A reply with only @@say gives me nothing to do — add commands, @@next, or @@done."));

            if (parse.Problems.Count > 0)
                return Outcome.Successful((IReadOnlyList<FormatProblem>)problems);

            foreach (var command in parse.Commands)
            {
                switch (command)
                {
                    case NewFileCommand newFile:
                        CheckPath(newFile.Path, command, problems);
                        CheckContentLength(newFile.Content, limits, newFile.Path, problems);
                        break;

                    case UpdateFileCommand updateFile:
                        CheckPath(updateFile.Path, command, problems);
                        CheckContentLength(updateFile.Content, limits, updateFile.Path, problems);
                        break;

                    case EditFileCommand editFile:
                        CheckPath(editFile.Path, command, problems);
                        CheckContentLength(editFile.Find, limits, editFile.Path, problems);
                        CheckContentLength(editFile.Replace, limits, editFile.Path, problems);
                        break;

                    case DeleteFileCommand deleteFile:
                        CheckPath(deleteFile.Path, command, problems);
                        break;

                    case ReadFileCommand readFile:
                        CheckPath(readFile.Path, command, problems);
                        break;

                    case MoveFileCommand moveFile:
                        CheckPath(moveFile.Path, command, problems);
                        CheckPath(moveFile.ToPath, command, problems);
                        break;
                }
            }

            return Outcome.Successful((IReadOnlyList<FormatProblem>)problems);
        }
        catch (Exception ex)
        {
            return Outcome.Failure<IReadOnlyList<FormatProblem>>(new OutcomeError(ex));
        }
    }

    /// <summary>
    /// Paths must be workspace-relative, use forward slashes, contain no <c>..</c> segment and no backslash.
    /// </summary>
    private static void CheckPath(string path, AgentScriptCommand command, List<FormatProblem> problems)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path is ['/', ..] ||
            System.IO.Path.IsPathRooted(path) ||
            path.Contains('\\', StringComparison.Ordinal) ||
            path.Split('/').Any(segment => segment is ".." or "."))
        {
            problems.Add(new FormatProblem(null, ErrorCodes.ParseBadPath,
                $"Command #{command.Number} ({command.Kind.GetDescription()}) has an invalid path \"{path.Truncate(80)}\": use a workspace-relative path with forward slashes and no \"..\"."));
        }
    }

    private static void CheckContentLength(string content, LimitsOptions limits, string target, List<FormatProblem> problems)
    {
        if (content.Length > limits.MaxFileContentChars)
        {
            problems.Add(new FormatProblem(null, ErrorCodes.LimitFileContentChars,
                $"Payload for \"{target.Truncate(60)}\" is {content.Length} characters; the maximum per command is {limits.MaxFileContentChars}. Split the file across several editfile commands."));
        }
    }
}
