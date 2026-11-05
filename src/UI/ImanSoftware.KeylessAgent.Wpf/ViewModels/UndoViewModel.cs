using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Wpf.Infrastructure;
using ImanSoftware.KeylessAgent.Wpf.Mvvm;
using ImanSoftware.KeylessAgent.Wpf.Services;

namespace ImanSoftware.KeylessAgent.Wpf.ViewModels;

/// <summary>
/// View model of the Undo section in the agent page's Changes panel: asks for confirmation
/// (guide text), then restores the newest backup batch through the Core
/// <see cref="IBackupService"/> and lists the restored files. A declined confirmation calls
/// nothing.
/// </summary>
public sealed class UndoViewModel : ObservableObject
{
    private readonly IBackupService _backup;
    private readonly IDialogService _dialogs;
    private readonly AgentViewModel _agent;
    private readonly IUiDispatcher _ui;
    private bool _isRestoring;
    private string _note = string.Empty;
    private IReadOnlyList<string> _restoredFiles = [];

    /// <summary>Creates the undo section view model.</summary>
    /// <param name="backup">The Core backup service.</param>
    /// <param name="dialogs">The queued dialog service (confirmation).</param>
    /// <param name="agent">Provides the selected workspace.</param>
    /// <param name="ui">Dispatcher for safe UI-thread updates.</param>
    public UndoViewModel(IBackupService backup, IDialogService dialogs, AgentViewModel agent, IUiDispatcher ui)
    {
        _backup = backup ?? throw new ArgumentNullException(nameof(backup));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));

        UndoCommand = new AsyncRelayCommand(UndoAsync, () => _agent.HasWorkspace && !IsRestoring);
        UndoCommand.IsRunningChanged += (_, running) =>
        {
            IsRestoring = running.IsRunning;
            UndoCommand.NotifyCanExecuteChanged();
        };
    }

    /// <summary>Restores the newest backup batch after confirmation.</summary>
    public AsyncRelayCommand UndoCommand { get; }

    /// <summary>Whether a restore is running.</summary>
    public bool IsRestoring
    {
        get => _isRestoring;
        private set => SetProperty(ref _isRestoring, value);
    }

    /// <summary>The files restored by the last undo.</summary>
    public IReadOnlyList<string> RestoredFiles
    {
        get => _restoredFiles;

        private set
        {
            if (SetProperty(ref _restoredFiles, value))
                OnPropertyChanged(nameof(HasRestoredFiles));
        }
    }

    /// <summary>Whether there are restored files to list.</summary>
    public bool HasRestoredFiles => _restoredFiles.Count > 0;

    /// <summary>A note (result or failure); empty initially.</summary>
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

    private async Task UndoAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync(new ConfirmRequest(
            "Undo latest backups?",
            "This restores the latest backups. Changes made by commands cannot be undone."));
        if (!confirmed)
            return;

        Note = string.Empty;
        try
        {
            var outcome = await _backup.UndoLatestAsync(_agent.WorkspaceRoot);
            _ui.Post(() =>
            {
                if (outcome.IsSuccess && outcome.Data is not null)
                {
                    RestoredFiles = outcome.Data;
                    Note = $"Restored {outcome.Data.Count} file(s) from the newest backup batch.";
                }
                else
                {
                    Note = outcome.Error?.Message ?? "The undo failed.";
                }
            });
        }
        catch (Exception ex)
        {
            _ui.Post(() => Note = $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
