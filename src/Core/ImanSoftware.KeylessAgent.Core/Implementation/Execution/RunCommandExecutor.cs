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
    private readonly ICommandRiskClassifier _riskClassifier;
    private readonly ProcessRunner _runner;

    public RunCommandExecutor()
        : this(new CommandPolicy(), new CommandRiskClassifier(), new ProcessRunner())
    {
    }

    internal RunCommandExecutor(ICommandPolicy policy, ICommandRiskClassifier riskClassifier, ProcessRunner runner)
    {
        _policy = policy;
        _riskClassifier = riskClassifier;
        _runner = runner;
    }

    /// <inheritdoc />
    public CommandKind Kind => CommandKind.RunCommand;

    /// <inheritdoc />
    public async Task<CommandExecutionResult> ExecuteAsync(AgentScriptCommand command, AgentExecutionContext context)
    {
        var run = (RunCommandCommand)command;

        var assessment = _riskClassifier.Classify(run.CommandLine, context.WorkspaceRoot);
        if (assessment.IsFailure)
            return CommandExecutionResult.Fail(command, assessment.Error.Code, assessment.Error.Message);

        var policy = _policy.Validate(run.CommandLine, context.Options.Commands);
        if (policy.IsFailure)
            return CommandExecutionResult.Fail(command, policy.Error.Code, policy.Error.Message);

        if (assessment.Data.Tier == CommandRiskTier.Denied)
        {
            return CommandExecutionResult.Fail(command, ErrorCodes.CommandDenied,
                $"denied: {assessment.Data.Reason}");
        }

        if (context.DryRun)
            return NewFileExecutor.DryRunResult(command);

        var (program, arguments) = SplitCommandLine(run.CommandLine);

        // Approval policy per mode and tier.
        var autoApproved = false;
        var needsConfirmation = false;
        var tierName = assessment.Data.Tier.ToString();
        switch (context.Options.Mode)
        {
            case ApprovalMode.Ask:
                autoApproved = context.Options.Commands.AutoApprove.Contains(tierName, StringComparer.OrdinalIgnoreCase);
                needsConfirmation = !autoApproved;
                break;

            case ApprovalMode.AutoSafe:
                autoApproved = (assessment.Data.Tier is CommandRiskTier.ReadOnly or CommandRiskTier.Build) ||
                    (assessment.Data.Tier == CommandRiskTier.Execute &&
                     _policy.IsAllowlisted(run.CommandLine, context.Options.Commands));
                needsConfirmation = !autoApproved;
                break;

            case ApprovalMode.Auto:
                autoApproved = true;
                break;
        }

        if (needsConfirmation)
        {
            var confirmed = await context.Ui.ConfirmAsync(new ConfirmRequest(
                $"Run command ({tierName})",
                $"The model wants to run: {run.CommandLine} — {assessment.Data.Reason}"));
            if (!confirmed)
            {
                return new CommandExecutionResult
                {
                    Number = command.Number,
                    Kind = command.Kind,
                    Target = command.Target,
                    Status = CommandStatus.Error,
                    ErrorCode = ErrorCodes.CommandDeclined,
                    FailureReason = "declined",
                Tier = assessment.Data.Tier,
                    ErrorMessage = "The user declined this command.",
                };
            }
        }
        else
        {
            context.Ui.ShowProgress(new AgentProgressEvent(
                AgentEventType.ExecutingCommand,
                $"Running: {run.CommandLine} (auto-approved: {tierName.ToLowerInvariant()})"));
        }

        var result = await _runner.RunAsync(
            program, arguments, context.WorkspaceRoot,
            context.Options.Commands.TimeoutSeconds, context.CancellationToken);

        if (result.NotStarted)
        {
            return new CommandExecutionResult
            {
                Number = command.Number,
                Kind = command.Kind,
                Target = command.Target,
                Status = CommandStatus.Error,
                ErrorCode = ErrorCodes.CommandNotFound,
                FailureReason = "command_not_found",
                Tier = assessment.Data.Tier,
                ErrorMessage = $"'{program}' was not found: it is not installed or not on PATH.",
            };
        }

        if (result.Reason == "cancelled")
        {
            return new CommandExecutionResult
            {
                Number = command.Number,
                Kind = command.Kind,
                Target = command.Target,
                Status = CommandStatus.Error,
                ErrorCode = ErrorCodes.Cancelled,
                FailureReason = "cancelled",
                Tier = assessment.Data.Tier,
                ErrorMessage = "Command cancelled by the user.",
                Output = JoinOutput(result),
                DurationSeconds = result.DurationSeconds,
            };
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
                FailureReason = "timeout",
                Tier = assessment.Data.Tier,
                ErrorMessage = $"Command timed out after {context.Options.Commands.TimeoutSeconds}s and was killed.",
                DurationSeconds = result.DurationSeconds,
            };
        }

        var output = JoinOutput(result);

        if (result.ExitCode != 0)
        {
            var notFoundReason = IsCommandNotFound(result);
            var failureReason = notFoundReason ? "command_not_found" : "non_zero_exit";
            var message = notFoundReason
                ? $"'{program}' was not found: it is not installed or not on PATH."
                : $"Command exited with code {result.ExitCode}.";

            return new CommandExecutionResult
            {
                Number = command.Number,
                Kind = command.Kind,
                Target = command.Target,
                Status = CommandStatus.Error,
                ErrorCode = notFoundReason ? ErrorCodes.CommandNotFound : ErrorCodes.CommandFailed,
                FailureReason = failureReason,
                Tier = assessment.Data.Tier,
                ErrorMessage = message,
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
            Tier = assessment.Data.Tier,
        };

    }
    /// <summary>
    /// Whether a non-zero exit means the program itself does not exist: cmd's 9009, shell 127,
    /// or the classic "not recognized" messages.
    /// </summary>
    internal static bool IsCommandNotFound(ProcessResult result)
    {
        if (result.ExitCode is 9009 or 127)
            return true;

        var combined = (result.Output + " " + result.Error).ToLowerInvariant();
        return combined.Contains("is not recognized as an internal or external command") ||
               combined.Contains("command not found");
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
