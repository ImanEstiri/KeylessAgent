using ImanSoftware.DateTimeProvider;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Models.Execution;
using ImanSoftware.KeylessAgent.Abstractions.Models.Parser;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Core.Implementation.Configuration;
using ImanSoftware.KeylessAgent.Core.Implementation.Workspace;
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
    private readonly IWorkspaceInspector _workspaceInspector;
    private readonly IChangeTracker _changeTracker;
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
            new WorkspaceInspector(),
            new ChangeTracker(),
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
        IWorkspaceInspector workspaceInspector,
        IChangeTracker changeTracker,
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
        _workspaceInspector = workspaceInspector;
        _changeTracker = changeTracker;
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
        string? lastDirtyChoice = null;
        AgentSession? savedSession = null;
        var noGitModeUsed = false;
        var agentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

            // --- Step 2: Workspace capabilities (probed ONCE per session, reused everywhere) ---
            var capabilitiesOutcome = await _workspaceInspector.InspectAsync(workspaceRoot, options.Git, cancellationToken);
            if (capabilitiesOutcome.IsFailure)
                return Failed(capabilitiesOutcome.Error.Message, dryRun);
            var capabilities = capabilitiesOutcome.Data;
            var noGitMode = capabilities.NoGitMode;

            if (noGitMode)
            {
                // NO-GIT MODE: one warning, no prompts, no branch protection, no commits, no push.
                ui.Say("[!] Git is not installed. There is no branch protection, no commits and no git history, " +
                       "so changes cannot be reverted with git. File changes made through AgentScript file commands " +
                       "are still backed up in .agent/backups (keyless undo); changes made by commands are not.");
                noGitModeUsed = true;
            }
            else if (!capabilities.IsRepository)
            {
                // --- Step 2b: automatic repository initialization ---
                if (!options.Git.AutoInit)
                    return Failed("The workspace is not a git repository (Git.AutoInit is disabled). Run 'git init' first.", dryRun, ErrorCodes.NotAGitRepository);

                ui.ShowProgress(new AgentProgressEvent(AgentEventType.SessionStarting, "Not a git repository; initializing one…"));
                if (!dryRun)
                {
                    var init = await _git.InitRepositoryAsync(workspaceRoot);
                    if (init.IsFailure)
                        return Failed($"git init failed: {init.Error.Message}", dryRun, ErrorCodes.GitInitFailed);

                    var gitignore = await _git.EnsureDotnetGitignoreAsync(workspaceRoot);
                    if (gitignore.IsFailure)
                        ui.ShowProgress(new AgentProgressEvent(AgentEventType.SessionStarting, $"Could not create .gitignore: {gitignore.Error.Message}"));

                    var commit = await _git.CreateInitialCommitAsync(workspaceRoot);
                    if (commit.IsFailure)
                        return Failed($"Initial commit failed: {commit.Error.Message}", dryRun, ErrorCodes.GitInitFailed);
                }

                ui.ShowProgress(new AgentProgressEvent(
                    AgentEventType.SessionStarting,
                    "Initialized a git repository (main) with a .gitignore and an initial commit."));

                var refreshed = await _git.GetRepositoryInfoAsync(workspaceRoot);
                if (refreshed.IsFailure || !refreshed.Data.IsRepository)
                    return Failed("Repository initialization did not produce a usable git repository.", dryRun, ErrorCodes.GitInitFailed);
            }

            if (!noGitMode)
                await _git.EnsureAgentPathsExcludedAsync(workspaceRoot);

            // --- Step 2c: Environment probe (parallel, 3s timeout each) ---
            var environmentProbe = new EnvironmentProbe();
            var env = await environmentProbe.ProbeAsync(workspaceRoot, cancellationToken);
            var envBlock = env.IsSuccess ? env.Data.Block : "[ENVIRONMENT unavailable]";
            var envSummary = env.IsSuccess ? env.Data.Summary : "";

            // --- Step 3+4: Git preconditions (menus) ---
            branch = noGitMode ? string.Empty : capabilities.CurrentBranch;
            var isProtected = !noGitMode && _branchGuard.IsProtected(branch, options.Git);
            var isAgentBranch = !noGitMode && capabilities.IsAgentBranch;

            // Change tracking: baseline before the turn.
            IReadOnlyList<ChangedPath> turnChanges = [];
            object? changeBaseline = null;
            if (!dryRun)
                changeBaseline = (await _changeTracker.CaptureAsync(workspaceRoot, !noGitMode, cancellationToken)).Data;

            var repo = await _git.GetRepositoryInfoAsync(workspaceRoot);
            var repoDirty = !noGitMode && repo.IsSuccess && repo.Data.IsRepository && (repo.Data.IsDirty);
            var loadedSession = await _sessions.LoadAsync(workspaceRoot);
            var agentChangedPaths = loadedSession is { IsSuccess: true } && loadedSession.Data.AgentChangedPaths.Count > 0
                ? loadedSession.Data.AgentChangedPaths
                : null;

            // Skip the dirty menu when every dirty path was changed by the agent itself AND we
            // are already on an agent branch.
            var skipDirtyMenu = repoDirty && isAgentBranch && agentChangedPaths is not null &&
                (await _changeTracker.DiffAsync(workspaceRoot, changeBaseline!, true, cancellationToken))
                    is { IsSuccess: true } diffResult
                && diffResult.Data.All(c => agentChangedPaths.Contains(c.Path, StringComparer.OrdinalIgnoreCase));


            if (!noGitMode && repoDirty && !skipDirtyMenu)
            {
                // Dirty tree menu: numbered, no typing. "carry" is only offered when we are NOT
                // already on an agent branch; the default is "carry" off-branch, "here" on-branch.
                var dirtyOptions = new List<ChoiceOption>();
                if (!isAgentBranch)
                    dirtyOptions.Add(new("carry", "Carry my uncommitted changes to a new agent branch"));
                dirtyOptions.Add(new("stash", "Stash my changes first (git stash, restore later yourself)"));
                dirtyOptions.Add(new("here", $"Continue on the current branch ('{branch}') with my changes in place"));
                dirtyOptions.Add(new("cancel", "Cancel"));

                var dirtyMenu = new ChoiceRequest(
                    "The working tree has uncommitted changes. What should I do?",
                    dirtyOptions,
                    DefaultId: isAgentBranch ? "here" : "carry");

                var dirtyChoice = await ui.ChooseAsync(dirtyMenu, cancellationToken);
                lastDirtyChoice = dirtyChoice.SelectedId;
                if (dirtyChoice.Cancelled || dirtyChoice.SelectedId == "cancel")
                    return Failed("Cancelled by the user (dirty working tree).", dryRun);

                if (dirtyChoice.SelectedId == "stash")
                {
                    var stash = await _git.StashAsync(workspaceRoot);
                    if (stash.IsFailure)
                        return Failed($"git stash failed: {stash.Error.Message}", dryRun);
                    ui.ShowProgress(new AgentProgressEvent(AgentEventType.SessionStarting, "Changes stashed (restore later with 'git stash pop')."));
                }
            }

            if (isProtected)
            {
                // Protected branch: no choice — the agent branch is created automatically.
                var agentBranch = _branchNames.Build(request, options.Git);
                ui.ShowProgress(new AgentProgressEvent(
                    AgentEventType.SessionStarting,
                    $"Branch '{branch}' is protected; creating agent branch {agentBranch}" + (repoDirty ? " (carrying your uncommitted changes)." : ".")));
                var created = dryRun
                    ? Outcome.Successful()
                    : await _git.CreateAndSwitchBranchAsync(workspaceRoot, agentBranch);
                if (created.IsFailure)
                    return Failed($"Cannot create agent branch: {created.Error.Message}", dryRun);
                branch = agentBranch;
            }
            else if (repoDirty)
            {
                // The dirty menu already decided: carry → new agent branch, here → stay.
                if (lastDirtyChoice == "carry")
                {
                    var agentBranch = _branchNames.Build(request, options.Git);
                    var created = dryRun
                        ? Outcome.Successful()
                        : await _git.CreateAndSwitchBranchAsync(workspaceRoot, agentBranch);
                    if (created.IsFailure)
                        return Failed($"Cannot create agent branch: {created.Error.Message}", dryRun);
                    branch = agentBranch;
                }
            }
            else if (!noGitMode && !dryRun)
            {
                // Clean tree on a non-protected branch: one menu (new / stay / cancel).
                var branchMenu = new ChoiceRequest(
                    $"You are on '{branch}'. Which branch should I work on?",
                    new List<ChoiceOption>
                    {
                        new("new", $"Create a new agent branch from '{branch}' (recommended)"),
                        new("stay", $"Continue on '{branch}'"),
                        new("cancel", "Cancel"),
                    },
                    DefaultId: "new");

                var branchChoice = await ui.ChooseAsync(branchMenu, cancellationToken);
                if (branchChoice.Cancelled || branchChoice.SelectedId == "cancel")
                    return Failed("Cancelled by the user (branch choice).", dryRun);

                if (branchChoice.SelectedId == "new")
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

            // Message 1 of a new chat: the AgentScript rules ALONE, with the instruction that
            // they apply to the whole conversation. Mixing them into the request made the model
            // skim the grammar and invent formats ("@@command", JSON blocks).
            var sendSystemPrompt = session is null || !session.SystemPromptSent;
            if (sendSystemPrompt)
            {
                var prompt = SystemPromptBuilder.Build(options);
                if (prompt.IsFailure)
                    return Failed(prompt.Error.Message, dryRun);

                if (noGitModeUsed)
                    prompt = Outcome.Successful(prompt.Data +
                        "\n\nIMPORTANT: git is NOT installed in this workspace. There is no version control: " +
                        "do not use @@commit and do not run any git commands (they will fail with command_not_found). " +
                        "File changes are backed up by the program automatically.");

                ui.ShowProgress(new AgentProgressEvent(AgentEventType.SendingMessage, "Sending the AgentScript rules (first message of this chat)"));
                if (!dryRun)
                {
                    Outcome sentPrompt;
                    using (ui.BeginWait("Sending the AgentScript rules to DeepSeek"))
                    {
                        sentPrompt = await chatSession.SendMessageAsync(prompt.Data, [], cancellationToken);
                    }

                    if (sentPrompt.IsFailure)
                        return Failed(sentPrompt.Error.Message, dryRun);

                    savedSession = new AgentSession
                    {
                        ChatUrl = chatSession.ChatUrl,
                        Branch = branch,
                        Turn = turn,
                        SystemPromptSent = true,
                        CreatedAtUtc = session?.CreatedAtUtc ?? _time.Now.ToUniversalTime().ToString("O"),
                    };
                    await _sessions.SaveAsync(workspaceRoot, savedSession);
                    await logger.AppendAsync("system-prompt-sent", new { Turn = turn }, cancellationToken);
                }
            }

            // Message 2: the user's request + the workspace snapshot attachments.
            ui.ShowProgress(new AgentProgressEvent(AgentEventType.SendingMessage, "Sending the request"));
            await logger.AppendAsync("message-sent", new { Turn = turn, Batch = 0, Attachments = attachments.Select(a => a.Name).ToList() }, cancellationToken);
            if (!dryRun)
            {
                Outcome sent;
                using (ui.BeginWait("Sending the request to DeepSeek"))
                {
                    sent = await chatSession.SendMessageAsync(message, attachments, cancellationToken);
                }

                if (sent.IsFailure)
                    return Failed(sent.Error.Message, dryRun);
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

                // Visible trail: what the model actually returned (truncated here; the full
                // text is in .agent/logs/turn-N.jsonl). If the browser dies mid-turn, the user
                // still sees every reply and whether it was usable.
                const int MaxConsoleReplyChars = 2000;
                var visibleReply = reply.Length <= MaxConsoleReplyChars
                    ? reply
                    : reply[..MaxConsoleReplyChars] + $"… ({reply.Length - MaxConsoleReplyChars} more characters; full text in .agent/logs)";
                ui.Say($"[raw reply]\n{visibleReply}");
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

                // --- BatchFailureMode: a failed command may stop the remaining commands ---
                var failedIndex = executed.FindIndex(r => r.Status == CommandStatus.Error);
                if (failedIndex >= 0 && options.Commands.BatchFailureMode != "Continue")
                {
                    var failedCommand = executed[failedIndex];
                    var isReadOnlyFailure = failedCommand.Kind == CommandKind.RunCommand &&
                        failedCommand.FailureReason is "command_not_found";
                    var stop = options.Commands.BatchFailureMode == "StopOnAnyFailure" || !isReadOnlyFailure;
                    if (stop)
                    {
                        foreach (var pending in script.Commands.Where(c => c.Number > failedCommand.Number && c.Kind != CommandKind.Say))
                        {
                            if (executed.Any(e => e.Number == pending.Number))
                                continue;
                            executed.Add(new CommandExecutionResult
                            {
                                Number = pending.Number,
                                Kind = pending.Kind,
                                Target = pending.Target,
                                Status = CommandStatus.Skipped,
                                ErrorMessage = $"an earlier command failed (#{failedCommand.Number})",
                            });
                        }
                    }
                }

                if (completedByDone)
                {
                // Update the agent-changed path set (file commands + runcommand side effects):
                // persisted in session.json so the next turn can skip the dirty menu when all
                // dirty paths are agent-made.
                if (!dryRun && changeBaseline is not null)
                {
                    var changed = (await _changeTracker.DiffAsync(workspaceRoot, changeBaseline, !noGitModeUsed, cancellationToken)).Data;
                    if (changed is not null)
                        agentPaths.UnionWith(changed.Select(c => c.Path));
                }
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
            NoGitMode = noGitModeUsed,
            CancellationToken = cancellationToken,
        };

        AgentTurnResult Failed(string message, bool dry, string? errorCode = null)
        {
            ui.ShowProgress(new AgentProgressEvent(AgentEventType.TurnFinished, message));
            return new AgentTurnResult { ErrorMessage = message, DryRun = dry, Turn = 0, ErrorCode = errorCode };
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
