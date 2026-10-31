using System.Collections.ObjectModel;
using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
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

    /// <summary>Adds a model message to the feed.</summary>
    /// <param name="message">The <c>say</c> text.</param>
    public void AddModelMessage(string message) => Items.Add(new ModelMessageItem(message));

    /// <summary>Adds a progress step to the feed.</summary>
    /// <param name="progressEvent">The progress event reported by Core.</param>
    public void AddStep(AgentProgressEvent progressEvent)
    {
        ArgumentNullException.ThrowIfNull(progressEvent);
        Items.Add(new StepItem(progressEvent.Type, progressEvent.Detail));
    }

    /// <summary>Adds a pending file change to the feed.</summary>
    /// <param name="change">The change about to be applied.</param>
    public void AddDiff(FileChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Items.Add(new DiffItem(change));
    }

    /// <summary>Removes every entry (used when a new session starts).</summary>
    public void Clear() => Items.Clear();
}
