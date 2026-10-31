using System.Collections.ObjectModel;
using System.IO;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Wpf.Infrastructure;
using ImanSoftware.KeylessAgent.Wpf.Mvvm;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Wpf.ViewModels;

/// <summary>One row of the workspace capability list.</summary>
/// <param name="Label">What was probed.</param>
/// <param name="Value">The probe result ("yes"/"no" or the branch name).</param>
/// <param name="Ok">Whether the capability is available (chip color).</param>
public sealed record CapabilityRow(string Label, string Value, bool Ok);

/// <summary>One recent-workspace card (name, path, newest badge, open/remove commands).</summary>
public sealed class RecentWorkspaceViewModel
{
    /// <summary>Creates a recent-workspace card.</summary>
    /// <param name="owner">The workspace view model handling selection and removal.</param>
    /// <param name="path">The workspace folder.</param>
    /// <param name="isNewest">Whether this is the most recently used entry.</param>
    public RecentWorkspaceViewModel(WorkspaceViewModel owner, string path, bool isNewest)
    {
        Path = path;
        IsNewest = isNewest;
        OpenCommand = new RelayCommand(() => owner.Select(Path));
        RemoveCommand = new RelayCommand(() => owner.RemoveRecent(this));
    }

    /// <summary>The workspace folder.</summary>
    public string Path { get; }

    /// <summary>Whether this is the most recently used entry ("last used" badge).</summary>
    public bool IsNewest { get; }

    /// <summary>The folder name shown as the card title.</summary>
    public string Name
        => Path.TrimEnd('/', '\\').Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? Path;

    /// <summary>Selects this workspace.</summary>
    public RelayCommand OpenCommand { get; }

    /// <summary>Removes this card from the recent list (and the settings file).</summary>
    public RelayCommand RemoveCommand { get; }
}

/// <summary>
/// View model of the workspace page: folder browsing, recent-workspace cards backed by
/// <see cref="SettingsService"/>, and capability inspection through the Core
/// <see cref="IWorkspaceInspector"/> on a background task. Inspection failure shows a friendly
/// note — it never surfaces an exception.
/// </summary>
public sealed class WorkspaceViewModel : ObservableObject
{
    private readonly AgentViewModel _agent;
    private readonly IWorkspaceInspector _inspector;
    private readonly SettingsService _settings;
    private readonly IUiDispatcher _ui;
    private readonly Func<string?> _browseFolder;
    private readonly GitOptions _gitOptions = new();
    private string _selectedPath = string.Empty;
    private string _note = string.Empty;
    private bool _isInspecting;
    private IReadOnlyList<CapabilityRow> _capabilities = [];

    /// <summary>Creates the workspace page view model and restores the last workspace.</summary>
    /// <param name="agent">The agent view model receiving the selection.</param>
    /// <param name="inspector">The Core workspace inspector.</param>
    /// <param name="settings">The settings service (recent-workspace persistence).</param>
    /// <param name="ui">Dispatcher for safe UI-thread updates.</param>
    /// <param name="browseFolder">Opens the folder picker; returns the chosen path or null.</param>
    public WorkspaceViewModel(AgentViewModel agent, IWorkspaceInspector inspector, SettingsService settings, IUiDispatcher ui, Func<string?> browseFolder)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _browseFolder = browseFolder ?? throw new ArgumentNullException(nameof(browseFolder));

        BrowseCommand = new RelayCommand(Browse);
        var saved = _settings.Load();
        RefreshRecents(saved);
        if (saved.LastWorkspace.Length > 0)
            Select(saved.LastWorkspace, remember: false);
    }

    /// <summary>Opens the folder picker.</summary>
    public RelayCommand BrowseCommand { get; }

    /// <summary>The recent-workspace cards, newest first.</summary>
    public ObservableCollection<RecentWorkspaceViewModel> Recents { get; private set; } = [];

    /// <summary>Whether any recent workspaces exist (empty-state text).</summary>
    public bool HasRecents => Recents.Count > 0;

    /// <summary>The currently selected workspace folder.</summary>
    public string SelectedPath
    {
        get => _selectedPath;

        private set
        {
            if (SetProperty(ref _selectedPath, value))
                OnPropertyChanged(nameof(HasSelection));
        }
    }

    /// <summary>Whether a folder is currently selected (empty state).</summary>
    public bool HasSelection => _selectedPath.Length > 0;

    /// <summary>A friendly note (invalid folder, inspection problems, no-git mode); empty when all is well.</summary>
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

    /// <summary>Whether the capability inspection is still running.</summary>
    public bool IsInspecting
    {
        get => _isInspecting;
        private set => SetProperty(ref _isInspecting, value);
    }

    /// <summary>The mapped capability rows; empty until an inspection finished.</summary>
    public IReadOnlyList<CapabilityRow> Capabilities
    {
        get => _capabilities;
        private set => SetProperty(ref _capabilities, value);
    }

    /// <summary>Selects a workspace folder (used by tests and the recent cards).</summary>
    /// <param name="path">The folder to select.</param>
    public void Select(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        Select(path, remember: true);
    }

    /// <summary>Removes a card from the recent list and persists the change.</summary>
    /// <param name="item">The card to remove.</param>
    public void RemoveRecent(RecentWorkspaceViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var latest = _settings.Load();
        var remaining = latest.RecentWorkspaces
            .Where(p => !string.Equals(p, item.Path, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var updated = latest with { RecentWorkspaces = remaining, LastWorkspace = remaining.FirstOrDefault() ?? string.Empty };
        _settings.Save(updated);
        RefreshRecents(updated);
    }

    private void Browse()
    {
        var path = _browseFolder();
        if (!string.IsNullOrWhiteSpace(path))
            Select(path, remember: true);
    }

    private void Select(string path, bool remember)
    {
        var trimmed = path.Trim().TrimEnd('\\', '/');
        if (trimmed.Length == 0)
            return;

        if (!Directory.Exists(trimmed))
        {
            Note = $"Folder not found: {trimmed}";
            return;
        }

        SelectedPath = trimmed;
        Note = string.Empty;
        Capabilities = [];
        IsInspecting = true;
        _agent.WorkspaceRoot = trimmed;
        _agent.BranchName = null;

        if (remember)
        {
            var latest = SettingsService.WithRecentWorkspace(_settings.Load(), trimmed);
            _settings.Save(latest);
            RefreshRecents(latest);
        }

        _ = InspectAsync(trimmed);
    }

    private async Task InspectAsync(string path)
    {
        Outcome<WorkspaceCapabilities> outcome;
        try
        {
            outcome = await Task.Run(async () => await _inspector.InspectAsync(path, _gitOptions));
        }
        catch (Exception)
        {
            ShowInspectionFailure(path);
            return;
        }

        _ui.Post(() =>
        {
            if (!string.Equals(SelectedPath, path, StringComparison.OrdinalIgnoreCase))
                return; // the user picked another workspace while this one was inspected

            IsInspecting = false;
            if (!outcome.IsSuccess || outcome.Data is null)
            {
                ShowInspectionFailure(path);
                return;
            }

            var capabilities = outcome.Data;
            Note = capabilities.NoGitMode
                ? "git was not found on this machine — file commands still run (with backups), but there is no branch protection or change tracking."
                : string.Empty;
            _agent.BranchName = capabilities.CurrentBranch;
            Capabilities =
            [
                new CapabilityRow("Git installed", capabilities.GitInstalled ? "yes" : "no", capabilities.GitInstalled),
                new CapabilityRow("Git repository", capabilities.IsRepository ? "yes" : "no", capabilities.IsRepository),
                new CapabilityRow("Current branch", capabilities.CurrentBranch, capabilities.GitInstalled && capabilities.IsRepository),
                new CapabilityRow("Agent branch", capabilities.IsAgentBranch ? "yes" : "no", capabilities.IsAgentBranch),
            ];
        });
    }

    private void ShowInspectionFailure(string path)
    {
        _ui.Post(() =>
        {
            if (!string.Equals(SelectedPath, path, StringComparison.OrdinalIgnoreCase))
                return;

            IsInspecting = false;
            Note = "Could not inspect this workspace — file commands still work, but branch information is unavailable.";
        });
    }

    private void RefreshRecents(WpfSettings settings)
    {
        Recents = new ObservableCollection<RecentWorkspaceViewModel>(
            settings.RecentWorkspaces.Select((path, index) => new RecentWorkspaceViewModel(this, path, index == 0)));
        OnPropertyChanged(nameof(Recents));
        OnPropertyChanged(nameof(HasRecents));
    }
}
