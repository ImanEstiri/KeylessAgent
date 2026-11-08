using System.Collections.ObjectModel;
using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Wpf.Services;
using ImanSoftware.KeylessAgent.Wpf.ViewModels.Feed;

namespace ImanSoftware.KeylessAgent.Wpf.ViewModels;

/// <summary>
/// Owns the ObservableCollection of feed entries shown on the agent page. All add-methods must
/// run on the UI thread — <see cref="Services.WpfUserInterface"/> posts them there via
/// <c>IUiDispatcher</c> so Core can call from any thread.
/// </summary>
public sealed class ActivityFeed
{
    /// <summary>The feed entries in arrival order; bound by the agent page.</summary>
    public ObservableCollection<IFeedItem> Items { get; } = [];

    /// <summary>Adds the user's request to the feed (top of a turn).</summary>
    /// <param name="request">The request text as sent.</param>
    public void AddUserRequest(string request) => Items.Add(new UserRequestItem(request));

    /// <summary>Adds a model message to the feed.</summary>
    /// <param name="message">The <c>say</c> text.</param>
    public void AddModelMessage(string message) => Items.Add(new ModelMessageItem(message));

    /// <summary>Adds a progress step to the feed; ExecutingCommand details are parsed into command rows.</summary>
    /// <param name="progressEvent">The progress event reported by Core.</param>
    public void AddStep(AgentProgressEvent progressEvent)
    {
        ArgumentNullException.ThrowIfNull(progressEvent);
        if (progressEvent.Type == AgentEventType.ExecutingCommand)
        {
            // Auto-approved runcommands surface as muted one-line notes (tier included) so
            // prompt-less execution still leaves a visible trace in the transcript.
            if (progressEvent.Detail is not null
                && progressEvent.Detail.Contains("(auto-approved:", StringComparison.Ordinal))
            {
                Items.Add(new AutoRanItem(
                    progressEvent.Detail.Replace("Running: ", string.Empty),
                    EventPresenter.AutoApprovedTier(progressEvent.Detail)));
                return;
            }

            if (EventPresenter.TryParseCommandDetail(progressEvent.Detail) is { } command)
            {
                Items.Add(new CommandItem(command.Number, command.Kind, command.Target));
                return;
            }
        }

        var presented = EventPresenter.Present(progressEvent);
        Items.Add(new StepItem(presented.Label, presented.Symbol, presented.Kind, progressEvent.Detail));
    }

    /// <summary>Adds a pending file change to the feed.</summary>
    /// <param name="change">The change about to be applied.</param>
    public void AddDiff(FileChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Items.Add(new DiffItem(change));
    }

    /// <summary>Adds an error entry to the feed (turn failure, cancellation, unexpected exception).</summary>
    /// <param name="message">The error text.</param>
    public void AddError(string message) => Items.Add(new ErrorItem(message));

    /// <summary>Adds the end-of-turn summary card to the feed.</summary>
    /// <param name="summary">The mapped turn summary.</param>
    public void AddSummary(TurnSummaryItem summary) => Items.Add(summary);

    /// <summary>Removes every entry (used when a new session starts).</summary>
    public void Clear() => Items.Clear();
}
