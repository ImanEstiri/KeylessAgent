using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Core.Implementation.Browser;
using ImanSoftware.KeylessAgent.Wpf.Infrastructure;
using ImanSoftware.KeylessAgent.Wpf.Mvvm;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Wpf.ViewModels;

/// <summary>
/// View model of the Start-Chrome section: does exactly what <c>keyless chrome</c> does —
/// starts (or attaches to) the dedicated Chrome via the Core <see cref="IBrowserConnector"/>
/// and reports the connection kind for the status bar. Never automates the page.
/// </summary>
public sealed class ChromeViewModel : ObservableObject
{
    private readonly IBrowserConnector _connector;
    private readonly IConfigurationService _configuration;
    private readonly IChromeWindowController _windowController;
    private readonly AgentViewModel _agent;
    private readonly Func<IAgentUserInterface> _uiFactory;
    private readonly IUiDispatcher _ui;
    private bool _isConnecting;
    private bool _isChromeActive;
    private bool _isForeignChrome;
    private string _note = string.Empty;
    private string _profile = string.Empty;
    private string _connectionKind = string.Empty;
    private string _statusText = "Ready";

    /// <summary>Creates the Chrome section view model.</summary>
    /// <param name="connector">The Core browser connector.</param>
    /// <param name="configuration">The per-workspace configuration service.</param>
    /// <param name="windowController">Brings the Chrome window into view on demand.</param>
    /// <param name="agent">Provides the selected workspace.</param>
    /// <param name="uiFactory">Creates the IAgentUserInterface that receives the connector's progress.</param>
    /// <param name="ui">Dispatcher for safe UI-thread updates.</param>
    public ChromeViewModel(IBrowserConnector connector, IConfigurationService configuration, IChromeWindowController windowController, AgentViewModel agent, Func<IAgentUserInterface> uiFactory, IUiDispatcher ui)
    {
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _windowController = windowController ?? throw new ArgumentNullException(nameof(windowController));
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _uiFactory = uiFactory ?? throw new ArgumentNullException(nameof(uiFactory));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));

        StartCommand = new AsyncRelayCommand(StartAsync, CanStart);
        StartCommand.IsRunningChanged += (_, running) =>
        {
            IsConnecting = running.IsRunning;
            StartCommand.NotifyCanExecuteChanged();
        };

        ShowChromeCommand = new AsyncRelayCommand(ShowChromeAsync, CanUseWindow);
        ShowChromeCommand.IsRunningChanged += (_, running) =>
        {
            IsShowing = running.IsRunning;
            ShowChromeCommand.NotifyCanExecuteChanged();
        };

        HideChromeCommand = new AsyncRelayCommand(HideChromeAsync, CanUseWindow);
        HideChromeCommand.IsRunningChanged += (_, running) =>
        {
            IsHiding = running.IsRunning;
            HideChromeCommand.NotifyCanExecuteChanged();
        };

        CloseChromeCommand = new AsyncRelayCommand(CloseChromeAsync, CanUseWindow);
        CloseChromeCommand.IsRunningChanged += (_, running) =>
        {
            IsClosing = running.IsRunning;
            CloseChromeCommand.NotifyCanExecuteChanged();
        };

        // Re-probe the Chrome state whenever the workspace selection changes.
        _agent.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AgentViewModel.WorkspaceRoot) or nameof(AgentViewModel.HasWorkspace))
                _ = RefreshChromeStateAsync();
        };
    }

    /// <summary>Whether ANY window/connect/close action is running (all four commands cross-disable).</summary>
    private bool IsBusy => IsConnecting || IsShowing || IsHiding || IsClosing;

    private bool CanStart() => !IsBusy && _agent.HasWorkspace && !IsChromeActive && !IsForeignChrome;
    private bool CanUseWindow() => !IsBusy && _agent.HasWorkspace && IsChromeActive;

    /// <summary>Starts or attaches to the dedicated Chrome (disabled while busy or without a workspace).</summary>
    public AsyncRelayCommand StartCommand { get; }

    /// <summary>Brings the offscreen Chrome window into view (disabled while busy or without a workspace).</summary>
    public AsyncRelayCommand ShowChromeCommand { get; }

    /// <summary>Moves the Chrome window back offscreen (disabled while busy or without a workspace).</summary>
    public AsyncRelayCommand HideChromeCommand { get; }

    /// <summary>Closes the KeylessAgent Chrome entirely (disabled while busy or without a workspace).</summary>
    public AsyncRelayCommand CloseChromeCommand { get; }

    /// <summary>Whether a show attempt is running.</summary>
    public bool IsShowing
    {
        get => _isShowing;
        private set
        {
            if (SetProperty(ref _isShowing, value))
                NotifyAllCommands();
        }
    }

    private bool _isShowing;

    /// <summary>Whether OUR Chrome (port + profile) is currently running.</summary>
    public bool IsChromeActive
    {
        get => _isChromeActive;
        private set
        {
            if (SetProperty(ref _isChromeActive, value))
                NotifyAllCommands();
        }
    }

    /// <summary>Whether the debug port is held by a Chrome WITHOUT the KeylessAgent profile.</summary>
    public bool IsForeignChrome
    {
        get => _isForeignChrome;
        private set
        {
            if (SetProperty(ref _isForeignChrome, value))
                NotifyAllCommands();
        }
    }

    /// <summary>Whether a hide attempt is running.</summary>
    public bool IsHiding
    {
        get => _isHiding;
        private set
        {
            if (SetProperty(ref _isHiding, value))
                NotifyAllCommands();
        }
    }

    private bool _isHiding;

    /// <summary>Whether a close attempt is running.</summary>
    public bool IsClosing
    {
        get => _isClosing;
        private set
        {
            if (SetProperty(ref _isClosing, value))
                NotifyAllCommands();
        }
    }

    private bool _isClosing;

    /// <summary>Whether a connection attempt is running.</summary>
    public bool IsConnecting
    {
        get => _isConnecting;
        private set
        {
            if (SetProperty(ref _isConnecting, value))
                NotifyAllCommands();
        }
    }

    private System.Timers.Timer? _statePollTimer;
    private int _stateProbeGeneration;
private int _probeInFlight;

    /// <summary>
    /// Re-probes the Chrome state off the UI thread. Called on workspace change, when the
    /// Doctor page opens, after every action completes, and on a ~2 s poll while the page
    /// is visible. Best-effort. Overlapping probes are allowed, but a probe's result is
    /// only applied when it is still the LATEST one (a stale probe that started before a
    /// close/show/hide must never overwrite the post-action state).
    /// </summary>
    public async Task RefreshChromeStateAsync()
    {
        if (!_agent.HasWorkspace)
            return;

        // Debounce: the 2s poll must not start a second probe while the previous one is still
// running (a slow probe would otherwise be superseded mid-flight and the UI would flap
// while a turn is streaming). The generation guard stays the source of truth for stale
// writes; this only stops needless overlapping probes.
        if (Interlocked.CompareExchange(ref _probeInFlight, 1, 0) != 0)
            return;

        var generation = Interlocked.Increment(ref _stateProbeGeneration);
        try
        {
            var options = await _configuration.LoadAsync(_agent.WorkspaceRoot);
            if (options.IsFailure || options.Data is null)
            {
                if (generation == Volatile.Read(ref _stateProbeGeneration))
                    _ui.Post(() => { IsChromeActive = false; IsForeignChrome = false; });
                return;
            }

            var state = await _windowController.GetStateAsync(options.Data.Browser);
            if (generation != Volatile.Read(ref _stateProbeGeneration))
                return; // a newer probe superseded this one — never apply a stale state

            System.Console.WriteLine($"[probe] gen={generation} active={state.Active} posting");
            _ui.Post(() =>
            {
                System.Console.WriteLine($"[apply] active={state.Active}");
                IsChromeActive = state.Active;
                IsForeignChrome = state.DebugPortOpen && !state.OwnedByAgent;
                if (IsForeignChrome)
                    StatusText = "Port in use by a Chrome that is not the agent's profile";
                else if (IsChromeActive && StatusText == "Ready")
                    StatusText = "Chrome running";
                else if (!IsChromeActive && StatusText == "Chrome running")
                    StatusText = "Chrome not running";
            });
        }
        catch
        {
            // State probing is best-effort; failures leave the last known state.
        }
        finally
        {
            Volatile.Write(ref _probeInFlight, 0);
        }
    }

    /// <summary>Starts the ~2 s state poll (Doctor page visible). Idempotent.</summary>
    public void StartStatePoll()
    {
        if (_statePollTimer is not null)
            return;
        _statePollTimer = new System.Timers.Timer(2000) { AutoReset = true };
        _statePollTimer.Elapsed += (_, _) => _ = RefreshChromeStateAsync();
        _statePollTimer.Start();
    }

    /// <summary>Stops the ~2 s state poll (Doctor page hidden).</summary>
    public void StopStatePoll()
    {
        _statePollTimer?.Stop();
        _statePollTimer?.Dispose();
        _statePollTimer = null;
    }

    private void NotifyAllCommands()
    {
        StartCommand.NotifyCanExecuteChanged();
        ShowChromeCommand.NotifyCanExecuteChanged();
        HideChromeCommand.NotifyCanExecuteChanged();
        CloseChromeCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The status text also shown in the shell status bar ("Ready", "Starting Chrome…", "Chrome ready (…)").</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>How the connection was established ("AttachedToRunningChrome", "AutoLaunchedChrome", "PlaywrightLaunched").</summary>
    public string ConnectionKind
    {
        get => _connectionKind;
        private set => SetProperty(ref _connectionKind, value);
    }

    /// <summary>The dedicated profile directory in use.</summary>
    public string Profile
    {
        get => _profile;
        private set => SetProperty(ref _profile, value);
    }

    /// <summary>A note (failure, next steps); empty when idle.</summary>
    public string Note
    {
        get => _note;

        private set
        {
            if (SetProperty(ref _note, value))
                OnPropertyChanged(nameof(HasNote));
        }
    }

    /// <summary>Whether a note is shown.</summary>
    public bool HasNote => _note.Length > 0;

    private async Task StartAsync()
    {
        if (!_agent.HasWorkspace)
        {
            Note = "Select a workspace first (Workspace page).";
            return;
        }

        Note = string.Empty;
        var options = await _configuration.LoadAsync(_agent.WorkspaceRoot);
        if (options.IsFailure || options.Data is null)
        {
            Note = options.Error?.Message ?? "Could not load keyless.json.";
            return;
        }

        StatusText = "Starting Chrome…";
        try
        {
            var connection = await _connector.ConnectAsync(options.Data.Browser, _uiFactory());
            _ui.Post(() => Apply(connection));
        }
        catch (OperationCanceledException)
        {
            _ui.Post(() =>
            {
                IsConnecting = false;
                StatusText = "Ready";
                Note = "Cancelled.";
            });
        }
        catch (Exception ex)
        {
            _ui.Post(() =>
            {
                IsConnecting = false;
                StatusText = "Ready";
                Note = $"{ex.GetType().Name}: {ex.Message}";
            });
        }
    }

    private async Task ShowChromeAsync()
    {
        if (!_agent.HasWorkspace)
        {
            Note = "Select a workspace first (Workspace page).";
            return;
        }

        Note = string.Empty;
        var options = await _configuration.LoadAsync(_agent.WorkspaceRoot);
        if (options.IsFailure || options.Data is null)
        {
            Note = options.Error?.Message ?? "Could not load keyless.json.";
            return;
        }

        StatusText = "Showing Chrome…";
        try
        {
            var outcome = await _windowController.ShowAsync(options.Data.Browser, 0);
            _ui.Post(() =>
            {
                IsShowing = false;
                if (outcome.IsFailure)
                {
                    StatusText = "Ready";
                    Note = outcome.Error?.Message ?? "The window could not be shown.";
                    return;
                }

                StatusText = "Chrome visible";
                Note = "Chrome window shown. Run 'keyless hide' or move it offscreen again when done.";
            });
        }
        catch (OperationCanceledException)
        {
            _ui.Post(() =>
            {
                IsShowing = false;
                StatusText = "Ready";
                Note = "Cancelled.";
            });
        }
        catch (Exception ex)
        {
            _ui.Post(() =>
            {
                IsShowing = false;
                StatusText = "Ready";
                Note = $"{ex.GetType().Name}: {ex.Message}";
            });
        }
    }

    private async Task HideChromeAsync()
    {
        if (!_agent.HasWorkspace)
        {
            Note = "Select a workspace first (Workspace page).";
            return;
        }

        Note = string.Empty;
        var options = await _configuration.LoadAsync(_agent.WorkspaceRoot);
        if (options.IsFailure || options.Data is null)
        {
            Note = options.Error?.Message ?? "Could not load keyless.json.";
            return;
        }

        StatusText = "Hiding Chrome…";
        try
        {
            var outcome = await _windowController.HideAsync(options.Data.Browser);
            _ui.Post(() =>
            {
                IsHiding = false;
                if (outcome.IsFailure)
                {
                    StatusText = "Ready";
                    Note = outcome.Error?.Message ?? "The window could not be hidden.";
                    return;
                }

                StatusText = "Chrome offscreen";
                Note = "Chrome window moved offscreen. The Show Chrome button brings it back.";
            });
        }
        catch (OperationCanceledException)
        {
            _ui.Post(() =>
            {
                IsHiding = false;
                StatusText = "Ready";
                Note = "Cancelled.";
            });
        }
        catch (Exception ex)
        {
            _ui.Post(() =>
            {
                IsHiding = false;
                StatusText = "Ready";
                Note = $"{ex.GetType().Name}: {ex.Message}";
            });
        }
    }

    private async Task CloseChromeAsync()
    {
        if (!_agent.HasWorkspace)
        {
            Note = "Select a workspace first (Workspace page).";
            return;
        }

        Note = string.Empty;
        var options = await _configuration.LoadAsync(_agent.WorkspaceRoot);
        if (options.IsFailure || options.Data is null)
        {
            Note = options.Error?.Message ?? "Could not load keyless.json.";
            return;
        }

        StatusText = "Closing Chrome…";
        try
        {
            var outcome = await _windowController.CloseAsync(options.Data.Browser);
            _ui.Post(() =>
            {
                IsClosing = false;
                if (outcome.IsFailure)
                {
                    StatusText = "Ready";
                    Note = outcome.Error?.Message ?? "Chrome could not be closed.";
                    return;
                }
                // No stale "Chrome ready/visible" status may survive a real close.
                ConnectionKind = string.Empty;
                Profile = string.Empty;
                IsChromeActive = false;
                IsForeignChrome = false;
                System.Console.WriteLine("[close-handler] IsChromeActive=false");
                StatusText = "Ready";
                Note = "Chrome closed. Use Start Chrome to open it again.";
            });
        }
        catch (OperationCanceledException)
        {
            _ui.Post(() =>
            {
                IsClosing = false;
                StatusText = "Ready";
                Note = "Cancelled.";
            });
        }
        catch (Exception ex)
        {
            _ui.Post(() =>
            {
                IsClosing = false;
                StatusText = "Ready";
                Note = $"{ex.GetType().Name}: {ex.Message}";
            });
        }
    }

    private void Apply(Outcome<(BrowserAttachment Attachment, object Page)> connection)
    {
        IsConnecting = false;
        if (!connection.IsSuccess || connection.Data.Attachment is null)
        {
            StatusText = "Ready";
            Note = connection.Error?.Message ?? "The browser connection failed.";
            return;
        }

        ConnectionKind = connection.Data.Attachment.Kind.ToString();
        Profile = connection.Data.Attachment.UserDataDir;
        // Deterministic post-action state: we JUST started/attached to our Chrome.
        IsChromeActive = true;
        IsForeignChrome = false;
        StatusText = $"Chrome ready ({ConnectionKind})";
        Note = "Chrome is ready. Log in to DeepSeek in that window once — the login persists in the dedicated profile. This command does not automate the page.";
    }
}
