using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Wpf.Infrastructure;
using ImanSoftware.KeylessAgent.Wpf.ViewModels;

namespace ImanSoftware.KeylessAgent.Wpf.Services;

/// <summary>
/// The WPF implementation of <see cref="IAgentUserInterface"/>. Feed updates are posted to the
/// UI thread so Core can call from any thread; dialogs delegate to <see cref="IDialogService"/>;
/// waits go through the nested <see cref="BusyIndicator"/> stack.
/// </summary>
internal sealed class WpfUserInterface : IAgentUserInterface
{
    private readonly IUiDispatcher _ui;
    private readonly IDialogService _dialogs;
    private readonly ActivityFeed _feed;
    private readonly BusyIndicator _busy;

    /// <summary>Creates the agent UI.</summary>
    /// <param name="ui">Dispatcher that marshals feed updates onto the UI thread.</param>
    /// <param name="dialogs">Queued modal dialog service.</param>
    /// <param name="feed">The activity feed that receives Say/progress/diff entries.</param>
    /// <param name="busy">The nested wait stack that receives BeginWait scopes.</param>
    public WpfUserInterface(IUiDispatcher ui, IDialogService dialogs, ActivityFeed feed, BusyIndicator busy)
    {
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _busy = busy ?? throw new ArgumentNullException(nameof(busy));
    }

    /// <summary>The feed this UI writes to (bound by the agent page).</summary>
    public ActivityFeed Feed => _feed;

    /// <summary>The busy stack this UI writes to (bound by the status bar).</summary>
    public BusyIndicator Busy => _busy;

    /// <inheritdoc />
    public void Say(string message) => _ui.Post(() => _feed.AddModelMessage(message));

    /// <inheritdoc />
    public void ShowProgress(AgentProgressEvent progressEvent) => _ui.Post(() => _feed.AddStep(progressEvent));

    /// <inheritdoc />
    public void ShowDiff(FileChange change) => _ui.Post(() => _feed.AddDiff(change));

    /// <inheritdoc />
    public Task<bool> ConfirmAsync(ConfirmRequest request) => _dialogs.ConfirmAsync(request);

    /// <inheritdoc />
    public Task<string> AskAsync(string question) => _dialogs.AskAsync(question);

    /// <inheritdoc />
    public Task<ChoiceResult> ChooseAsync(ChoiceRequest request, CancellationToken cancellationToken = default)
        => _dialogs.ChooseAsync(request, cancellationToken);

    /// <inheritdoc />
    public IDisposable BeginWait(string message) => _busy.Push(message);
}
