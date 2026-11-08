using System.Collections.ObjectModel;
using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Wpf.Infrastructure;
using ImanSoftware.KeylessAgent.Wpf.Mvvm;
using ImanSoftware.KeylessAgent.Wpf.Services;

namespace ImanSoftware.KeylessAgent.Wpf.ViewModels;

/// <summary>One selectable approval-mode row (plain-language label + tooltip).</summary>
public sealed record ApprovalModeOption(
    ApprovalMode Mode,
    string Label,
    string Tooltip,
    bool IsDangerous);

/// <summary>
/// View model of the approval-mode dropdown on the chat page: reads/writes the workspace's
/// keyless.json Mode (the same field the CLI uses), so each workspace keeps its own mode.
/// Selecting "Full access" shows a one-time-per-session confirmation before applying.
/// </summary>
public sealed class ApprovalModeSelectorViewModel : ObservableObject
{
    private readonly IConfigurationService _configuration;
    private readonly AgentViewModel _agent;
    private readonly IUiDispatcher _ui;
    private readonly Func<ConfirmRequest, Task<bool>> _confirm;
    private bool _suppressSelectionWrite; // programmatic selection during load
    private bool _autoWarningShownThisSession; // once per app session (one VM per window)

    /// <summary>The three selectable modes, in display order.</summary>
    public ReadOnlyCollection<ApprovalModeOption> Options { get; }

    /// <summary>Creates the selector. <paramref name="confirm"/> backs the one-time Full-access warning.</summary>
    public ApprovalModeSelectorViewModel(
        IConfigurationService configuration, AgentViewModel agent, IUiDispatcher ui,
        Func<ConfirmRequest, Task<bool>>? confirm = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _confirm = confirm ?? (_ => Task.FromResult(true));

        Options =
        [
            new(ApprovalMode.Ask,
                "Ask before every command",
                "Today's default: the model shows you every command and file change, and waits for your approval.",
                IsDangerous: false),
            new(ApprovalMode.AutoSafe,
                "Auto-run safe commands",
                "Commands like 'dotnet build', reading files, or checking git status run automatically. "
                + "Anything that could change or delete something still asks you first.",
                IsDangerous: false),
            new(ApprovalMode.Auto,
                "Full access (workspace only)",
                "Everything runs automatically, including commands that modify files. Still strictly "
                + "limited to this workspace folder — nothing outside it can be touched.",
                IsDangerous: true),
        ];

        _selectedOption = Options[0]; // default selection, never persisted from the ctor

        // Re-load this workspace's saved mode whenever the workspace selection changes.
        _agent.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AgentViewModel.WorkspaceRoot) or nameof(AgentViewModel.HasWorkspace))
                _ = ReloadAsync();
        };
    }

    /// <summary>The selected mode (persisted into the workspace's keyless.json).</summary>
    public ApprovalMode SelectedMode => SelectedOption.Mode;

    /// <summary>The currently selected row.</summary>
    public ApprovalModeOption SelectedOption
    {
        get => _selectedOption;
        set
        {
            if (SetProperty(ref _selectedOption, value))
            {
                OnPropertyChanged(nameof(SelectedTooltip));
                _ = ApplySelectionAsync(value);
            }
        }
    }

    private ApprovalModeOption _selectedOption;

    /// <summary>The tooltip/subtext of the currently selected row.</summary>
    public string SelectedTooltip => SelectedOption.Tooltip;

    /// <summary>Whether the selector failed to load/save (surfaced as a tooltip-level note).</summary>
    public string? LastError { get; private set; }

    /// <summary>Loads the workspace's saved mode into the selector (programmatic — no re-save).</summary>
    public async Task ReloadAsync()
    {
        if (!_agent.HasWorkspace)
            return;

        var options = await _configuration.LoadAsync(_agent.WorkspaceRoot);
        if (options.IsFailure || options.Data is null)
            return;

        _suppressSelectionWrite = true;
        try
        {
            SelectedOption = Options.FirstOrDefault(o => o.Mode == options.Data.Mode) ?? Options[0];
        }
        finally
        {
            _suppressSelectionWrite = false;
        }
    }

    private async Task ApplySelectionAsync(ApprovalModeOption option)
    {
        if (_suppressSelectionWrite || !_agent.HasWorkspace)
            return;

        // One-time-per-session warning before applying Full access (least safe of the three).
        if (option.Mode == ApprovalMode.Auto && !_autoWarningShownThisSession)
        {
            _autoWarningShownThisSession = true;
            var confirmed = await _confirm(new ConfirmRequest(
                "Full access (workspace only)",
                "Commands will run automatically WITHOUT asking — including ones that modify or delete files. "
                + "They are still refused if they would touch anything outside this workspace folder. "
                + "Switch back to 'Ask' or 'Auto-run safe commands' at any time."));
            if (!confirmed)
            {
                // Revert the visual selection to the previous mode WITHOUT persisting:
                // field assignment + notification only (no save, no recursion).
                _selectedOption = Options.First(o => o.Mode != ApprovalMode.Auto);
                OnPropertyChanged(nameof(SelectedOption));
                OnPropertyChanged(nameof(SelectedMode));
                OnPropertyChanged(nameof(SelectedTooltip));
                return;
            }
        }

        var outcome = await _configuration.SaveApprovalModeAsync(_agent.WorkspaceRoot, option.Mode);
        if (outcome.IsFailure)
        {
            LastError = outcome.Error?.Message;
            OnPropertyChanged(nameof(LastError));
        }
    }
}
