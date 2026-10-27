using ImanSoftware.DateTimeProvider;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Models.Execution;
using ImanSoftware.KeylessAgent.Abstractions.Models.Parser;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Core.Implementation.Configuration;
using ImanSoftware.KeylessAgent.Core.Implementation.Execution;
using ImanSoftware.KeylessAgent.Core.Implementation.Parsing;
using ImanSoftware.KeylessAgent.Core.Implementation.Git;
using ImanSoftware.KeylessAgent.Core.Implementation.Snapshot;
using ImanSoftware.Logging;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Agent;

/// <summary>
/// The orchestrator: one call = one user request (one turn). A sequence of small services with
/// numbered steps, each returning an outcome. The snapshot bat files always run at the end,
/// even when the turn fails or the user cancels.
/// </summary>
internal sealed class AgentLoop : IAgentLoop
{
    private readonly IConfigurationService _configuration;
    private readonly IGitService _git;
    private readonly IBranchGuard _branchGuard;
    private readonly ISnapshotService _snapshot;
    private readonly IChatProvider _chat;
    private readonly IResponseFormatGuard _guard;
    private readonly IWorkspaceCommandExecutor _executor;
    private readonly IResultFormatter _resultFormatter;
    private readonly ISessionStore _sessions;
    private readonly BranchNameBuilder _branchNames;
    private readonly IDateTimeProvider _time;
    private readonly ILoggerAdapter<AgentLoop> _log;

    public AgentLoop(IChatProvider chat, ILoggerAdapter<AgentLoop>? log = null)
        : this(
            chat,
            new ConfigurationService(),
            new GitService(),
            new BranchGuard(),
            new SnapshotService(),
            new ResponseFormatGuard(),
            new WorkspaceCommandExecutor(),
            new ResultFormatter(),
            new SessionStore(),
            new BranchNameBuilder(new DefaultDateTimeProvider()),
            new DefaultDateTimeProvider(),
            log)
    {
    }

    internal AgentLoop(
        IChatProvider chat,
        IConfigurationService configuration,
        IGitService git,
        IBranchGuard branchGuard,
        ISnapshotService snapshot,
        IResponseFormatGuard guard,
        IWorkspaceCommandExecutor executor,
        IResultFormatter resultFormatter,
        ISessionStore sessions,
        BranchNameBuilder branchNames,
        IDateTimeProvider time,
        ILoggerAdapter<AgentLoop>? log)
    {
        _chat = chat;
        _configuration = configuration;
        _git = git;
        _branchGuard = branchGuard;
        _snapshot = snapshot;
        _guard = guard;
        _executor = executor;
        _resultFormatter = resultFormatter;
        _sessions = sessions;
        _branchNames = branchNames;
        _time = time;
        _log = log ?? new LoggerAdapter<AgentLoop>(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
    }

    /// <inheritdoc />
    public async Task<AgentTurnResult> RunAsync(
        string workspaceRoot,
        string request,
        IAgentUserInterface ui,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var logger = new TurnLogger(ImanSoftware.FileStorage.ImanFileStorageFactory.CreateStorage(), _time);
        KeylessOptions options = new();
        string branch = string.Empty;
        int turn = 1;
        var completedByDone = false;
        string? summary = null;
        string? stoppedReason = null;
        string? errorMessage = null;
        AgentSession? savedSession = null;

        try
        {
            // --- Step 1: Load and validate configuration ---
            ui.ShowProgress(new AgentProgressEvent(AgentEventType.SessionStarting, "Loading keyless.json"));
            var configOutcome = await _configuration.LoadAsync(workspaceRoot);
            if (configOutcome.IsFailure)
                return Failed(configOutcome.Error.Message, dryRun);
            options = configOutcome.Data;

            if (options.Mode is ApprovalMode.AutoSafe or ApprovalMode.Auto)
            {
                // Programs like dotnet/node/python execute arbitrary code; the allow list is
                // convenience, not a security boundary. Shown once per turn in these modes.
                ui.Say("[!] AutoSafe is not a security boundary: allow-listed programs (dotnet, node, python, ...) can execute arbitrary code on this machine.");
            }

            // --- Step 2: Git preconditions ---
            var repoOutcome = await _git.GetRepositoryInfoAsync(workspaceRoot);
            if (repoOutcome.IsFailure)
                return Failed(repoOutcome.Error.Message, dryRun);
            var repo = repoOutcome.Data;
            if (!repo.IsRepository)
                return Failed("The workspace is not a git repository. Initialize one first (git init).", dryRun);

            await _git.EnsureAgentPathsExcludedAsync(workspaceRoot);

            // --- Step 3: Dirty working tree ---
            if (repo.IsDirty)
            {
                var answer = await ui.AskAsync(
                    "The working tree has uncommitted changes. Type 'continue' to work on top of them, 'stash' to stash them first, or 'cancel':");
                var choice = answer.Trim().ToLowerInvariant();
                if (choice is "" or "cancel" or "c")
                    return Failed("Cancelled by the user (dirty working tree).", dryRun);
                if (choice is "stash" or "s")
                {
                    var stash = await _git.StashAsync(workspaceRoot);
                    if (stash.IsFailure)
                        return Failed($"git stash failed: {stash.Error.Message}", dryRun);
                }
            }

            // --- Step 4: Branch setup ---
            branch = repo.CurrentBranch;
            if (_branchGuard.IsProtected(branch, options.Git))
            {
                var agentBranch = _branchNames.Build(request, options.Git);
                ui.ShowProgress(new AgentProgressEvent(AgentEventType.SessionStarting, $"Creating agent branch {agentBranch}"));
                var created = dryRun
                    ? Outcome.Successful()
                    : await _git.CreateAndSwitchBranchAsync(workspaceRoot, agentBranch);
                if (created.IsFailure)
                    return Failed($"Cannot create agent branch: {created.Error.Message}", dryRun);
                branch = agentBranch;
            }
            else if (!dryRun)
            {
                var answer = await ui.AskAsync(
                    $"You are on '{branch}' (not protected). Press Enter to create a new agent branch, or type 'keep' to continue on '{branch}':");
                if (answer.Trim().Equals("keep", StringComparison.OrdinalIgnoreCase))
                {
                    // Stay on the current branch.
                }
                else
                {
                    var agentBranch = _branchNames.Build(request, options.Git);
                    var created = await _git.CreateAndSwitchBranchAsync(workspaceRoot, agentBranch);
                    if (created.IsFailure)
                        return Failed($"Cannot create agent branch: {created.Error.Message}", dryRun);
                    branch = agentBranch;
                }
            }

            // --- Step 5: Session state (resume or new chat) ---
            var sessionOutcome = await _sessions.LoadAsync(workspaceRoot);
            var session = sessionOutcome.IsSuccess ? sessionOutcome.Data : null;
            turn = (session?.Turn ?? 0) + 1;
            await logger.StartAsync(workspaceRoot, turn, cancellationToken);
            await logger.AppendAsync("turn-started", new { Turn = turn, Request = request, Branch = branch, DryRun = dryRun }, cancellationToken);

            ui.ShowProgress(new AgentProgressEvent(AgentEventType.ConnectingToBrowser, "Connecting to the chat"));
            var chatOutcome = await _chat.StartSessionAsync(
                new Abstractions.Models.Chat.ChatSessionOptions
                {
                    Browser = options.Browser,
                    WorkspaceRoot = workspaceRoot,
                    ResumeChatUrl = session?.ChatUrl,
                }, cancellationToken);
            if (chatOutcome.IsFailure)
                return Failed(chatOutcome.Error.Message, dryRun);

            await using var chatSession = chatOutcome.Data;
            savedSession = new AgentSession
            {
                ChatUrl = chatSession.ChatUrl,
                Branch = branch,
                Turn = turn,
                SystemPromptSent = session?.SystemPromptSent ?? false,
                CreatedAtUtc = session?.CreatedAtUtc ?? _time.Now.ToUniversalTime().ToString("O"),
            };
            await _sessions.SaveAsync(workspaceRoot, savedSession);

            // --- Step 6: Snapshot refresh when stale ---
            var staleOutcome = await _snapshot.IsStaleAsync(workspaceRoot);
            if (staleOutcome.IsSuccess && staleOutcome.Data)
                await RefreshSnapshotAsync(ui, workspaceRoot, logger);

            // --- Step 7: Send the request (system prompt on the first message of a new chat) ---
            var attachmentsOutcome = await _snapshot.BuildAttachmentsAsync(workspaceRoot, options);
            var attachments = attachmentsOutcome.IsSuccess ? attachmentsOutcome.Data : [];
            if (attachmentsOutcome.IsFailure)
                ui.ShowProgress(new AgentProgressEvent(AgentEventType.UploadingAttachments,
                    $"Snapshot unavailable: {attachmentsOutcome.Error.Message}"));

            var message = request;
            var sendSystemPrompt = session is null || !session.SystemPromptSent;
            if (sendSystemPrompt)
            {
                var prompt = SystemPromptBuilder.Build(options);
                if (prompt.IsFailure)
                    return Failed(prompt.Error.Message, dryRun);
                message = prompt.Data + "\n\n@@say\n<<<MSG\nSystem prompt delivered. Waiting for the user's request.\nMSG>>>\n@@end\n@@eof\n\n[USER REQUEST]\n" + request;
            }

            ui.ShowProgress(new AgentProgressEvent(AgentEventType.SendingMessage, "Sending the request"));
            await logger.AppendAsync("message-sent", new { Turn = turn, Batch = 0, SystemPrompt = sendSystemPrompt, Attachments = attachments.Select(a => a.Name).ToList() }, cancellationToken);
            if (!dryRun)
            {
                Outcome sent;
                using (ui.BeginWait("Sending the request to DeepSeek"))
                {
                    sent = await chatSession.SendMessageAsync(message, attachments, cancellationToken);
                }

                if (sent.IsFailure)
                    return Failed(sent.Error.Message, dryRun);
                savedSession = new AgentSession
                {
                    ChatUrl = chatSession.ChatUrl,
                    Branch = branch,
                    Turn = turn,
                    SystemPromptSent = true,
                    CreatedAtUtc = session?.CreatedAtUtc ?? _time.Now.ToUniversalTime().ToString("O"),
                };
                await _sessions.SaveAsync(workspaceRoot, savedSession);
            }

            // --- Step 8: The conversation loop ---
            var formatRetries = 0;
            var batch = 0;
            while (batch < options.Limits.MaxIterationsPerTurn)
            {
                batch++;
                cancellationToken.ThrowIfCancellationRequested();

                ui.ShowProgress(new AgentProgressEvent(AgentEventType.WaitingForReply, $"Waiting for reply {batch}"));
                Outcome<string> replyOutcome;
                using (ui.BeginWait($"Waiting for reply {batch}"))
                {
                    replyOutcome = await chatSession.WaitForReplyAsync(cancellationToken);
                }
                if (replyOutcome.IsFailure)
                {
                    stoppedReason = $"no reply: {replyOutcome.Error.Message}";
                    ui.ShowProgress(new AgentProgressEvent(AgentEventType.TurnFinished, stoppedReason));
                    break;
                }

                var reply = replyOutcome.Data;
                await logger.AppendAsync("reply-received", new { Turn = turn, Batch = batch, Reply = reply }, cancellationToken);

                ui.ShowProgress(new AgentProgressEvent(AgentEventType.ValidatingReply, "Validating AgentScript"));
                var verdictOutcome = _guard.Check(reply, options.Limits);
                if (verdictOutcome.IsFailure)
                {
                    stoppedReason = $"guard failure: {verdictOutcome.Error.Message}";
                    break;
                }

                var verdict = verdictOutcome.Data;
                if (!verdict.IsValid)
                {
                    if (formatRetries >= options.Limits.MaxFormatRetries)
                    {
                        stoppedReason = "format retries exhausted; the last raw reply is below";
                        ui.Say("[FORMAT RETRIES EXHAUSTED] The model kept sending invalid AgentScript. Last raw reply:\n" + reply);
                        break;
                    }

                    formatRetries++;
                    ui.ShowProgress(new AgentProgressEvent(AgentEventType.RequestingResend,
                        $"Invalid reply ({verdict.Problems.Count} problems); asking for a resend ({formatRetries}/{options.Limits.MaxFormatRetries})"));
                    var correction = CorrectionMessageBuilder.Build(verdict.Problems, verdict.LooksTruncated);
                    if (correction.IsFailure)
                    {
                        stoppedReason = correction.Error.Message;
                        break;
                    }

                    await logger.AppendAsync("correction-sent", new { Turn = turn, Problems = verdict.Problems.Select(p => p.ToDisplayString()).ToList() }, cancellationToken);
                    if (!dryRun)
                    {
                        Outcome resent;
                        using (ui.BeginWait("Sending the format correction"))
                        {
                            resent = await chatSession.SendMessageAsync(correction.Data, [], cancellationToken);
                        }

                        if (resent.IsFailure)
                        {
                            stoppedReason = $"cannot send correction: {resent.Error.Message}";
                            break;
                        }
                    }

                    // The resend replaces the rejected response within the same batch slot.
                    batch--;
                    continue;
                }

                formatRetries = 0;
                var script = verdict.Script!;

                // --- Step 8b: Execute (say always; others up to the batch limit; rest skipped) ---
                var executed = new List<CommandExecutionResult>();
                var usedSlots = 0;
                foreach (var command in script.Commands)
                {
                    if (command.Kind == CommandKind.Say)
                    {
                        var sayResult = await _executor.ExecuteAsync(command, Context());
                        executed.Add(sayResult);
                        continue;
                    }

                    if (usedSlots >= options.Limits.MaxCommandsPerResponse)
                    {
                        executed.Add(new CommandExecutionResult
                        {
                            Number = command.Number,
                            Kind = command.Kind,
                            Target = command.Target,
                            Status = CommandStatus.Skipped,
                            ErrorMessage = "batch limit reached",
                        });
                        continue;
                    }

                    usedSlots++;
                    ui.ShowProgress(new AgentProgressEvent(AgentEventType.ExecutingCommand,
                        $"#{command.Number} {command.Kind} {command.Target}"));
                    var result = await _executor.ExecuteAsync(command, Context());
                    executed.Add(result);
                    await logger.AppendResultAsync(result, cancellationToken);

                    if (command.Kind == CommandKind.Done)
                    {
                        completedByDone = true;
                        summary = ((DoneCommand)command).Summary;
                    }
                }

                if (completedByDone)
                {
                    await logger.AppendAsync("turn-done", new { Turn = turn, Summary = summary }, cancellationToken);
                    break;
                }

                if (batch >= options.Limits.MaxIterationsPerTurn)
                {
                    stoppedReason = $"iteration limit reached ({options.Limits.MaxIterationsPerTurn} responses)";
                    break;
                }

                // --- Step 8c: Send the per-command results back ---
                var feedbackAttachments = options.Snapshot.AttachOnFeedback
                    ? (await _snapshot.BuildAttachmentsAsync(workspaceRoot, options)).Data ?? []
                    : [];

                var resultsMessage = _resultFormatter.Format(turn, batch, executed, options.Limits.MaxCommandsPerResponse, options.Limits);
                ui.ShowProgress(new AgentProgressEvent(AgentEventType.SendingResults, "Sending results"));
                await logger.AppendAsync("results-sent", new { Turn = turn, Batch = batch, Results = executed.Count }, cancellationToken);
                if (!dryRun)
                {
                    Outcome sent;
                    using (ui.BeginWait("Sending the results to DeepSeek"))
                    {
                        sent = await chatSession.SendMessageAsync(resultsMessage, feedbackAttachments, cancellationToken);
                    }

                    if (sent.IsFailure)
                    {
                        stoppedReason = $"cannot send results: {sent.Error.Message}";
                        break;
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            stoppedReason = "cancelled by the user";
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            stoppedReason = "internal error";
            _log.LogError(ex, "Agent turn failed");
        }
        finally
        {
            // --- Step 9: Always refresh the snapshot so the next request starts fresh ---
            await RefreshSnapshotAsync(ui, workspaceRoot, logger);
            ui.ShowProgress(new AgentProgressEvent(AgentEventType.TurnFinished, stoppedReason ?? "turn finished"));

            if (errorMessage is null && savedSession is not null)
                await _sessions.SaveAsync(workspaceRoot, savedSession);
        }

        var commits = new List<string>();
        if (errorMessage is null && !dryRun && branch.Length > 0)
        {
            var log = await _git.ListCommitsAsync(workspaceRoot, 20);
            if (log.IsSuccess)
                commits = log.Data.ToList();
        }

        return new AgentTurnResult
        {
            Branch = branch,
            Turn = turn,
            Commits = commits,
            Summary = summary,
            CompletedByDone = completedByDone,
            StoppedReason = stoppedReason,
            ErrorMessage = errorMessage,
            DryRun = dryRun,
        };

        AgentExecutionContext Context() => new()
        {
            WorkspaceRoot = workspaceRoot,
            Options = options,
            DryRun = dryRun,
            Ui = ui,
            CancellationToken = cancellationToken,
        };

        AgentTurnResult Failed(string message, bool dry)
        {
            ui.ShowProgress(new AgentProgressEvent(AgentEventType.TurnFinished, message));
            return new AgentTurnResult { ErrorMessage = message, DryRun = dry, Turn = 0 };
        }
    }

    private async Task RefreshSnapshotAsync(IAgentUserInterface ui, string workspaceRoot, TurnLogger logger)
    {
        ui.ShowProgress(new AgentProgressEvent(AgentEventType.RefreshingSnapshot, "Refreshing workspace snapshot"));
        Outcome refreshed;
        using (ui.BeginWait("Running tree-view.bat and code-dump.bat"))
        {
            refreshed = await _snapshot.RefreshAsync(workspaceRoot);
        }
        if (refreshed.IsFailure)
        {
            ui.ShowProgress(new AgentProgressEvent(AgentEventType.RefreshingSnapshot,
                $"Snapshot refresh failed: {refreshed.Error.Message}"));
            await logger.AppendAsync("snapshot-failed", new { refreshed.Error.Message }, CancellationToken.None);
            return;
        }

        await logger.AppendAsync("snapshot-refreshed", new { }, CancellationToken.None);
    }
}
