using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Models.Execution;
using ImanSoftware.KeylessAgent.Abstractions.Models.Parser;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Execution;

/// <summary>
/// Runs one external command line after the command policy checks every segment.
/// Ask mode confirms every command; AutoSafe runs allow-listed commands automatically.
/// </summary>
internal sealed class RunCommandExecutor : ICommandExecutor
{
    private readonly ICommandPolicy _policy;
    private readonly ProcessRunner _runner;

    public RunCommandExecutor()
        : this(new CommandPolicy(), new ProcessRunner())
    {
    }

    internal RunCommandExecutor(ICommandPolicy policy, ProcessRunner runner)
    {
        _policy = policy;
        _runner = runner;
    }

    /// <inheritdoc />
    public CommandKind Kind => CommandKind.RunCommand;

    /// <inheritdoc />
    public async Task<CommandExecutionResult> ExecuteAsync(AgentScriptCommand command, AgentExecutionContext context)
    {
        var run = (RunCommandCommand)command;

        var validation = _policy.Validate(run.CommandLine, context.Options.Commands);
        if (validation.IsFailure)
            return CommandExecutionResult.Fail(command, validation.Error.Code, validation.Error.Message);

        if (context.DryRun)
            return NewFileExecutor.DryRunResult(command);

        var needsConfirmation = context.Options.Mode switch
        {
            ApprovalMode.Ask => true,
            ApprovalMode.AutoSafe => !_policy.IsAllowlisted(run.CommandLine, context.Options.Commands),
            _ => false,
        };

        if (needsConfirmation)
        {
            var confirmed = await context.Ui.ConfirmAsync(new ConfirmRequest(
                "Run command",
                $"The model wants to run: {run.CommandLine}"));
            if (!confirmed)
            {
                return CommandExecutionResult.Fail(command, ErrorCodes.CommandDeclined,
                    "The user declined this command.");
            }
        }

        var (program, arguments) = SplitCommandLine(run.CommandLine);
        ProcessResult result;
        using (context.Ui.BeginWait($"running: {run.CommandLine}"))
        {
            result = await _runner.RunAsync(
                program, arguments, context.WorkspaceRoot,
                context.Options.Commands.TimeoutSeconds, context.CancellationToken);
        }

        if (result.TimedOut)
        {
            return new CommandExecutionResult
            {
                Number = command.Number,
                Kind = command.Kind,
                Target = command.Target,
                Status = CommandStatus.Error,
                ErrorCode = ErrorCodes.Timeout,
                ErrorMessage = $"Command timed out after {context.Options.Commands.TimeoutSeconds}s and was killed.",
                DurationSeconds = result.DurationSeconds,
            };
        }

        var output = JoinOutput(result);

        if (result.ExitCode != 0)
        {
            return new CommandExecutionResult
            {
                Number = command.Number,
                Kind = command.Kind,
                Target = command.Target,
                Status = CommandStatus.Error,
                ErrorCode = ErrorCodes.CommandFailed,
                ErrorMessage = $"Command exited with code {result.ExitCode}.",
                Output = output,
                ExitCode = result.ExitCode,
                DurationSeconds = result.DurationSeconds,
            };
        }

        return new CommandExecutionResult
        {
            Number = command.Number,
            Kind = command.Kind,
            Target = command.Target,
            Status = CommandStatus.Ok,
            Output = output,
            ExitCode = result.ExitCode,
            DurationSeconds = result.DurationSeconds,
        };
    }

    internal static (string Program, string Arguments) SplitCommandLine(string commandLine)
    {
        var trimmed = commandLine.Trim();
        if (trimmed.StartsWith('"'))
        {
            var closing = trimmed.IndexOf('"', 1);
            if (closing > 0)
            {
                return (trimmed[1..closing], trimmed[(closing + 1)..].TrimStart());
            }
        }

        var space = trimmed.IndexOf(' ');
        return space < 0
            ? (trimmed, string.Empty)
            : (trimmed[..space], trimmed[(space + 1)..]);
    }

    private static string JoinOutput(ProcessResult result)
    {
        var parts = new[] { result.Output.TrimEnd(), result.Error.TrimEnd() }.Where(p => p.Length > 0);
        return string.Join(Environment.NewLine + Environment.NewLine, parts);
    }
}
