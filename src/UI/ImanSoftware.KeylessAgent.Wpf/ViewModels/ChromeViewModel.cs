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

        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsConnecting && _agent.HasWorkspace);
        StartCommand.IsRunningChanged += (_, running) =>
        {
            IsConnecting = running.IsRunning;
            StartCommand.NotifyCanExecuteChanged();
        };

        ShowChromeCommand = new AsyncRelayCommand(ShowChromeAsync, () => !IsShowing && _agent.HasWorkspace);
        ShowChromeCommand.IsRunningChanged += (_, running) =>
        {
            IsShowing = running.IsRunning;
            ShowChromeCommand.NotifyCanExecuteChanged();
        };
    }

    /// <summary>Starts or attaches to the dedicated Chrome (disabled while connecting or without a workspace).</summary>
    public AsyncRelayCommand StartCommand { get; }

    /// <summary>Brings the offscreen/minimized Chrome window into view (disabled while showing or without a workspace).</summary>
    public AsyncRelayCommand ShowChromeCommand { get; }

    /// <summary>Whether a show attempt is running.</summary>
    public bool IsShowing
    {
        get => _isShowing;
        private set => SetProperty(ref _isShowing, value);
    }

    private bool _isShowing;

    /// <summary>Whether a connection attempt is running.</summary>
    public bool IsConnecting
    {
        get => _isConnecting;
        private set => SetProperty(ref _isConnecting, value);
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
        StatusText = $"Chrome ready ({ConnectionKind})";
        Note = "Chrome is ready. Log in to DeepSeek in that window once — the login persists in the dedicated profile. This command does not automate the page.";
    }
}
