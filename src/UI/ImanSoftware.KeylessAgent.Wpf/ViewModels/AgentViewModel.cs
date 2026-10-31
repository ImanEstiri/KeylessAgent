using System.Collections.ObjectModel;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Wpf.Infrastructure;
using ImanSoftware.KeylessAgent.Wpf.Mvvm;
using ImanSoftware.KeylessAgent.Wpf.Services;
using ImanSoftware.KeylessAgent.Wpf.ViewModels.Feed;

namespace ImanSoftware.KeylessAgent.Wpf.ViewModels;

/// <summary>
/// View model of the agent page: request composer, send/stop commands, the activity feed and
/// the changed-files panel. A turn runs through <see cref="AgentRunner"/> on a background task;
/// stopping cancels the token and cancels pending dialogs.
/// </summary>
public sealed class AgentViewModel : ObservableObject, IDisposable
{
    private readonly AgentRunner _runner;
    private readonly IUiDispatcher _ui;
    private readonly ActivityFeed _feed;
    private CancellationTokenSource? _turnCancellation;
    private string _requestText = string.Empty;
    private string _workspaceRoot = string.Empty;
    private bool _dryRun;
    private bool _verbose;
    private bool _isRunning;
    private TurnSummaryItem? _lastSummary;

    /// <summary>Creates the agent page view model.</summary>
    /// <param name="runner">The turn runner.</param>
    /// <param name="ui">Dispatcher used for safe UI-thread updates.</param>
    /// <param name="feed">The shared activity feed.</param>
    public AgentViewModel(AgentRunner runner, IUiDispatcher ui, ActivityFeed feed)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));

        StopCommand = new RelayCommand(Stop, () => IsRunning);
        SendCommand = new AsyncRelayCommand(SendAsync, CanSend, onError: PostError);
        SendCommand.IsRunningChanged += (_, running) =>
        {
            IsRunning = running.IsRunning;
            StopCommand.NotifyCanExecuteChanged();
        };
        ChangedFiles = new ObservableCollection<ChangedPath>();
    }

    /// <summary>Disposes the current turn's cancellation source. View models live for the app's lifetime.</summary>
    public void Dispose() => _turnCancellation?.Dispose();

    /// <summary>The feed shown in the activity list.</summary>
    public ActivityFeed Feed => _feed;

    /// <summary>The files changed by the last completed turn (right-hand panel).</summary>
    public ObservableCollection<ChangedPath> ChangedFiles { get; }

    /// <summary>The composer text; Ctrl+Enter sends it.</summary>
    public string RequestText
    {
        get => _requestText;

        set
        {
            if (SetProperty(ref _requestText, value ?? string.Empty))
                SendCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>The selected workspace root; empty until the workspace page sets it.</summary>
    public string WorkspaceRoot
    {
        get => _workspaceRoot;

        set
        {
            if (SetProperty(ref _workspaceRoot, value ?? string.Empty))
                SendCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Pre-ticks the dry-run toggle (nothing is executed).</summary>
    public bool DryRun
    {
        get => _dryRun;
        set => SetProperty(ref _dryRun, value);
    }

    /// <summary>Enables verbose diagnostics logging for the next turn.</summary>
    public bool Verbose
    {
        get => _verbose;
        set => SetProperty(ref _verbose, value);
    }

    /// <summary>Whether a turn is currently running (drives the Stop button and busy state).</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set => SetProperty(ref _isRunning, value);
    }

    /// <summary>The summary of the last completed turn (right-hand card); null before the first turn.</summary>
    public TurnSummaryItem? LastSummary
    {
        get => _lastSummary;
        private set => SetProperty(ref _lastSummary, value);
    }

    /// <summary>Sends the composer text as one agent turn (disabled when empty, running, or no workspace).</summary>
    public AsyncRelayCommand SendCommand { get; }

    /// <summary>Cancels the running turn and every pending dialog (disabled when not running).</summary>
    public RelayCommand StopCommand { get; }

    private bool CanSend()
        => !IsRunning && RequestText.Trim().Length > 0 && WorkspaceRoot.Trim().Length > 0;

    private async Task SendAsync()
    {
        var request = RequestText.Trim();
        if (request.Length == 0 || WorkspaceRoot.Trim().Length == 0)
            return;

        RequestText = string.Empty;
        _feed.AddUserRequest(request);
        ChangedFiles.Clear();

        _turnCancellation?.Dispose();
        _turnCancellation = new CancellationTokenSource();
        try
        {
            var summary = await _runner.RunTurnAsync(WorkspaceRoot, request, DryRun, Verbose, _turnCancellation.Token);
            if (summary is not null)
            {
                LastSummary = summary;
                foreach (var changed in summary.ChangedPaths)
                    ChangedFiles.Add(changed);
            }
        }
        finally
        {
            // The turn ended (completed, failed, cancelled or stopped): every diff card collapses.
            foreach (var diff in _feed.Items.OfType<Feed.DiffItem>())
                diff.Collapse();
        }
    }

    private void Stop()
    {
        _turnCancellation?.Cancel();
        DialogHost.CancelAll();
    }

    private void PostError(Exception exception) => _ui.Post(() => _feed.AddError(exception.Message));
}
