using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Wpf.Infrastructure;
using ImanSoftware.KeylessAgent.Wpf.ViewModels;
using ImanSoftware.KeylessAgent.Wpf.ViewModels.Feed;

namespace ImanSoftware.KeylessAgent.Wpf.Services;

/// <summary>
/// The only place that calls the agent loop. Creates the loop through the injected factory,
/// runs the turn on a fresh <see cref="WpfUserInterface"/>, maps the result to a
/// <see cref="TurnSummaryItem"/>, and turns cancellation and exceptions into feed errors —
/// the app never crashes because of a failing turn.
/// </summary>
public sealed class AgentRunner
{
    private readonly IUiDispatcher _ui;
    private readonly ActivityFeed _feed;
    private readonly Func<IAgentUserInterface> _uiFactory;
    private readonly Func<string, bool, IAgentLoop> _loopFactory;

    /// <summary>Creates the turn runner.</summary>
    /// <param name="ui">Dispatcher for posting feed updates onto the UI thread.</param>
    /// <param name="feed">The shared activity feed the turn writes into.</param>
    /// <param name="uiFactory">Creates the IAgentUserInterface for the turn (real or fake).</param>
    /// <param name="loopFactory">Creates the loop for a workspace: (workspaceRoot, verbose) → loop.</param>
    public AgentRunner(IUiDispatcher ui, ActivityFeed feed, Func<IAgentUserInterface> uiFactory, Func<string, bool, IAgentLoop> loopFactory)
    {
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _uiFactory = uiFactory ?? throw new ArgumentNullException(nameof(uiFactory));
        _loopFactory = loopFactory ?? throw new ArgumentNullException(nameof(loopFactory));
    }

    /// <summary>Runs one turn and posts the summary (or the error/cancellation) to the feed.</summary>
    /// <param name="workspaceRoot">The workspace directory.</param>
    /// <param name="request">The user's request.</param>
    /// <param name="dryRun">Dry-run mode (nothing is executed).</param>
    /// <param name="verbose">Verbose diagnostics logging.</param>
    /// <param name="cancellationToken">Cancels the turn.</param>
    /// <returns>The mapped summary; null when the turn failed, was cancelled, or ended with an error.</returns>
    public async Task<TurnSummaryItem?> RunTurnAsync(
        string workspaceRoot, string request, bool dryRun, bool verbose, CancellationToken cancellationToken,
        ImanSoftware.KeylessAgent.Abstractions.Models.Enums.AgentPace? paceOverride = null)
    {
        var ui = _uiFactory();
        try
        {
            var loop = _loopFactory(workspaceRoot, verbose);
            var result = await loop.RunAsync(workspaceRoot, request, ui, dryRun, cancellationToken, paceOverride: paceOverride);

            if (result.ErrorMessage is not null)
            {
                PostError(result.ErrorMessage);
                return null;
            }

            var summary = new TurnSummaryItem(
                result.Turn,
                result.Branch,
                [.. result.Commits],
                result.Summary,
                result.CompletedByDone,
                result.StoppedReason,
                result.DryRun,
                result.ChangeSet,
                result.AutoApprovedCommands,
                result.ConfirmedCommands,
                result.DiffStat);
            _ui.Post(() => _feed.AddSummary(summary));
            return summary;
        }
        catch (OperationCanceledException)
        {
            PostError("Cancelled");
            return null;
        }
        catch (Exception ex)
        {
            PostError($"{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private void PostError(string message) => _ui.Post(() => _feed.AddError(message));
}
