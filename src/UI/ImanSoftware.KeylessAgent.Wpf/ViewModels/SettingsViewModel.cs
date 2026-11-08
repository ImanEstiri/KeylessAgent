using System.IO;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Wpf.Infrastructure;
using ImanSoftware.KeylessAgent.Wpf.Mvvm;
using ImanSoftware.KeylessAgent.Wpf.Services;

namespace ImanSoftware.KeylessAgent.Wpf.ViewModels;

/// <summary>
/// View model of the settings page: the loaded <c>keyless.json</c> shown read-only (grouped
/// by <see cref="SettingsGroupMapper"/>), buttons to open/reload the file, and the UI-only
/// settings persisted through <see cref="SettingsService"/>.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly IConfigurationService _configuration;
    private readonly SettingsService _settings;
    private readonly AgentViewModel _agent;
    private readonly IUiDispatcher _ui;
    private readonly Func<string, bool> _openFile;
    private readonly Action<string>? _backdropChanged;
    private bool _reduceEffects;
    private bool _dryRunDefault;
    private bool _verbose;
    private string _backdropPreference;
    private bool _isLoading;
    private string _note = string.Empty;
    private IReadOnlyList<SettingsGroup> _groups = [];

    /// <summary>Creates the settings page view model and loads the UI-only settings.</summary>
    /// <param name="configuration">The per-workspace configuration service.</param>
    /// <param name="settings">The UI-only settings service.</param>
    /// <param name="agent">Provides the selected workspace.</param>
    /// <param name="ui">Dispatcher for safe UI-thread updates.</param>
    /// <param name="openFile">Opens a file with the shell association; returns false on failure.</param>
    /// <param name="backdropChanged">Notified when the backdrop preference changes (re-applies the backdrop).</param>
    public SettingsViewModel(IConfigurationService configuration, SettingsService settings, AgentViewModel agent, IUiDispatcher ui, Func<string, bool> openFile, Action<string>? backdropChanged = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _openFile = openFile ?? throw new ArgumentNullException(nameof(openFile));
        _backdropChanged = backdropChanged;

        var saved = _settings.Load();
        _reduceEffects = saved.ReduceEffects;
        _dryRunDefault = saved.DryRunDefault;
        _sendWithEnter = saved.SendWithEnter;
        _verbose = false;
        _backdropPreference = saved.BackdropPreference;

        ReloadCommand = new AsyncRelayCommand(ReloadAsync, () => _agent.HasWorkspace && !IsLoading);
        ReloadCommand.IsRunningChanged += (_, running) =>
        {
            IsLoading = running.IsRunning;
            ReloadCommand.NotifyCanExecuteChanged();
        };
        OpenKeylessJsonCommand = new RelayCommand(OpenKeylessJson, () => _agent.HasWorkspace);
    }

    /// <summary>The backdrop choices for the combo box.</summary>
    public static IReadOnlyList<string> BackdropChoices { get; } = ["Auto", "Mica", "Acrylic", "None"];

    /// <summary>Reloads the workspace configuration from disk.</summary>
    public AsyncRelayCommand ReloadCommand { get; }

    /// <summary>Opens <c>keyless.json</c> with the system editor.</summary>
    public RelayCommand OpenKeylessJsonCommand { get; }

    /// <summary>Whether the configuration is loading.</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }
    /// <summary>A note (no workspace, load failure); empty when all is well.</summary>
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

    /// <summary>The grouped read-only configuration; empty until loaded.</summary>
    public IReadOnlyList<SettingsGroup> Groups
    {
        get => _groups;
        private set => SetProperty(ref _groups, value);
    }

    /// <summary>Disables backdrop spheres and animations (weak machines).</summary>
    public bool ReduceEffects
    {
        get => _reduceEffects;

        set
        {
            var latest = _settings.Load() with { ReduceEffects = value };
            _settings.Save(latest);
            SetProperty(ref _reduceEffects, value);
        }
    }

    /// <summary>Pre-ticks the dry-run checkbox for new turns.</summary>
    public bool DryRunDefault
    {
        get => _dryRunDefault;

        set
        {
            var latest = _settings.Load() with { DryRunDefault = value };
            _settings.Save(latest);
            SetProperty(ref _dryRunDefault, value);
        }
    }

    private bool _sendWithEnter;

    /// <summary>Sends the request with plain Enter (Shift+Enter inserts a newline).</summary>
    public bool SendWithEnter
    {
        get => _sendWithEnter;

        set
        {
            var latest = _settings.Load() with { SendWithEnter = value };
            _settings.Save(latest);
            SetProperty(ref _sendWithEnter, value);
        }
    }

    /// <summary>Enables verbose diagnostics logging for turns (session-wide, not persisted).</summary>
    public bool Verbose
    {
        get => _verbose;
        set => SetProperty(ref _verbose, value);
    }

    /// <summary>The requested system backdrop (Auto/Mica/Acrylic/None).</summary>
    public string BackdropPreference
    {
        get => _backdropPreference;

        set
        {
            var applied = SetProperty(ref _backdropPreference, value ?? "Auto");
            if (applied)
            {
                var latest = _settings.Load() with { BackdropPreference = _backdropPreference };
                _settings.Save(latest);
                _backdropChanged?.Invoke(_backdropPreference);
            }
        }
    }

    private void OpenKeylessJson()
    {
        var path = Path.Combine(_agent.WorkspaceRoot, "keyless.json");
        if (!_openFile(path))
            Note = "Could not open keyless.json.";
    }

    private async Task ReloadAsync()
    {
        if (!_agent.HasWorkspace)
        {
            Note = "Select a workspace first (Workspace page).";
            return;
        }

        Note = string.Empty;
        try
        {
            var options = await _configuration.LoadAsync(_agent.WorkspaceRoot);
            _ui.Post(() =>
            {
                if (!options.IsSuccess || options.Data is null)
                {
                    Note = options.Error?.Message ?? "Could not load keyless.json.";
                    return;
                }

                Groups = SettingsGroupMapper.Map(options.Data);
            });
        }
        catch (Exception ex)
        {
            _ui.Post(() => Note = $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
