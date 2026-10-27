using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Models.Execution;
using ImanSoftware.KeylessAgent.Abstractions.Models.Parser;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Core.Implementation.Git;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Execution;

/// <summary>
/// Stages everything, commits with the model's message, and pushes the agent branch
/// (<c>git push -u origin &lt;branch&gt;</c>, never force). Push failures are returned to the model.
/// </summary>
internal sealed class CommitExecutor : ICommandExecutor
{
    private readonly IGitService _git;
    private readonly IBranchGuard _branchGuard;

    public CommitExecutor()
        : this(new GitService(), new BranchGuard())
    {
    }

    internal CommitExecutor(IGitService git, IBranchGuard branchGuard)
    {
        _git = git;
        _branchGuard = branchGuard;
    }

    /// <inheritdoc />
    public CommandKind Kind => CommandKind.Commit;

    /// <inheritdoc />
    public async Task<CommandExecutionResult> ExecuteAsync(AgentScriptCommand command, AgentExecutionContext context)
    {
        var commit = (CommitCommand)command;

        var guard = await _branchGuard.EnsureModifiableAsync(context.WorkspaceRoot, context.Options.Git);
        if (guard.IsFailure)
            return CommandExecutionResult.Fail(command, guard.Error.Code, guard.Error.Message);

        if (context.DryRun)
            return NewFileExecutor.DryRunResult(command);

        var stage = await _git.StageAllAsync(context.WorkspaceRoot);
        if (stage.IsFailure)
            return CommandExecutionResult.Fail(command, stage.Error.Code, stage.Error.Message);

        var commitResult = await _git.CommitAsync(context.WorkspaceRoot, commit.Message);
        if (commitResult.IsFailure)
            return CommandExecutionResult.Fail(command, commitResult.Error.Code, commitResult.Error.Message);

        var info = await _git.GetRepositoryInfoAsync(context.WorkspaceRoot);
        if (info.IsFailure)
            return CommandExecutionResult.Fail(command, info.Error.Code, info.Error.Message);

        var hasOrigin = await _git.HasOriginRemoteAsync(context.WorkspaceRoot);
        if (hasOrigin.IsFailure || !hasOrigin.Data)
        {
            // No 'origin' remote: committing locally is the whole job; the push is skipped with
            // a notice (the result tells the model, too) instead of failing.
            return new CommandExecutionResult
            {
                Number = command.Number,
                Kind = command.Kind,
                Target = command.Target,
                Status = CommandStatus.Ok,
                Output = "Committed locally. No 'origin' remote; committed locally (skipped push).",
            };
        }

        var push = await _git.PushBranchAsync(context.WorkspaceRoot, info.Data.CurrentBranch);
        if (push.IsFailure)
        {
            return CommandExecutionResult.Fail(command, push.Error.Code,
                $"Committed locally, but push failed: {push.Error.Message}");
        }

        return new CommandExecutionResult
        {
            Number = command.Number,
            Kind = command.Kind,
            Target = command.Target,
            Status = CommandStatus.Ok,
            Output = $"Committed and pushed branch {info.Data.CurrentBranch}.",
        };
    }
}
