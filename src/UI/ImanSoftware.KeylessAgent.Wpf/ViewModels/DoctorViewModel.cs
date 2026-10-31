using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Wpf.Infrastructure;
using ImanSoftware.KeylessAgent.Wpf.Mvvm;
using ImanSoftware.KeylessAgent.Wpf.Services;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Wpf.ViewModels;

/// <summary>
/// View model of the doctor page: runs the live selector check through the Core
/// <see cref="IChatDiagnostics"/> (exactly like the CLI DoctorCommand) on a background task,
/// with a Cancel button, status chips and the summary rows.
/// </summary>
public sealed class DoctorViewModel : ObservableObject, IDisposable
{
    private readonly IChatDiagnostics _diagnostics;
    private readonly IConfigurationService _configuration;
    private readonly AgentViewModel _agent;
    private readonly IUiDispatcher _ui;
    private CancellationTokenSource? _cancellation;
    private bool _isRunning;
    private string _note = string.Empty;
    private string _pageUrl = string.Empty;
    private IReadOnlyList<DoctorCheckRow> _checks = [];
    private IReadOnlyList<DoctorSummaryRow> _summaries = [];

    /// <summary>Creates the doctor page view model.</summary>
    /// <param name="diagnostics">The Core chat diagnostics.</param>
    /// <param name="configuration">The per-workspace configuration service.</param>
    /// <param name="agent">Provides the selected workspace.</param>
    /// <param name="ui">Dispatcher for safe UI-thread updates.</param>
    /// <param name="chrome">The Start-Chrome section shown below the checks.</param>
    public DoctorViewModel(IChatDiagnostics diagnostics, IConfigurationService configuration, AgentViewModel agent, IUiDispatcher ui, ChromeViewModel chrome)
    {
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        Chrome = chrome ?? throw new ArgumentNullException(nameof(chrome));

        CancelCommand = new RelayCommand(Cancel, () => IsRunning);
        RunCommand = new AsyncRelayCommand(RunAsync, () => !IsRunning);
        RunCommand.IsRunningChanged += (_, running) =>
        {
            IsRunning = running.IsRunning;
            CancelCommand.NotifyCanExecuteChanged();
        };
    }

    /// <summary>The Start-Chrome section.</summary>
    public ChromeViewModel Chrome { get; }

    /// <summary>Runs the checks (disabled while running).</summary>
    public AsyncRelayCommand RunCommand { get; }

    /// <summary>Cancels the running inspection (disabled while idle).</summary>
    public RelayCommand CancelCommand { get; }

    /// <summary>Whether an inspection is running.</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set => SetProperty(ref _isRunning, value);
    }

    /// <summary>A note (no workspace, failure, cancellation, fix-hint); empty when all is well.</summary>
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

    /// <summary>The inspected chat page URL.</summary>
    public string PageUrl
    {
        get => _pageUrl;
        private set => SetProperty(ref _pageUrl, value);
    }

    /// <summary>The selector check rows.</summary>
    public IReadOnlyList<DoctorCheckRow> Checks
    {
        get => _checks;
        private set => SetProperty(ref _checks, value);
    }

    /// <summary>The summary rows.</summary>
    public IReadOnlyList<DoctorSummaryRow> Summaries
    {
        get => _summaries;
        private set => SetProperty(ref _summaries, value);
    }

    private async Task RunAsync()
    {
        if (_agent.WorkspaceRoot.Trim().Length == 0)
        {
            Note = "Select a workspace first (Workspace page).";
            return;
        }

        var workspace = _agent.WorkspaceRoot;
        Note = string.Empty;
        PageUrl = string.Empty;
        Checks = [];
        Summaries = [];

        var options = await _configuration.LoadAsync(workspace);
        if (options.IsFailure || options.Data is null)
        {
            Note = options.Error?.Message ?? "Could not load keyless.json.";
            return;
        }

        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        try
        {
            var outcome = await _diagnostics.InspectAsync(workspace, options.Data.Browser, null, false, _cancellation.Token);
            _ui.Post(() => Apply(outcome));
        }
        catch (OperationCanceledException)
        {
            _ui.Post(() =>
            {
                IsRunning = false;
                Note = "Cancelled.";
            });
        }
        catch (Exception ex)
        {
            _ui.Post(() =>
            {
                IsRunning = false;
                Note = $"{ex.GetType().Name}: {ex.Message}";
            });
        }
    }

    private void Apply(Outcome<ChatDiagnosticsReport> outcome)
    {
        IsRunning = false;
        if (!outcome.IsSuccess || outcome.Data is null)
        {
            Note = outcome.Error?.Message ?? "The doctor run failed.";
            return;
        }

        var report = outcome.Data;
        PageUrl = report.PageUrl;
        Checks = DoctorRowMapper.MapChecks(report.Checks);
        Summaries = DoctorRowMapper.MapSummary(report);
        Note = report.AllRequiredMatched
            ? string.Empty
            : "Fix the NOT FOUND selectors in selectors.json (see docs/browser-automation.md) and re-run.";
    }

    private void Cancel() => _cancellation?.Cancel();

    /// <summary>Disposes the current inspection's cancellation source. View models live for the app's lifetime.</summary>
    public void Dispose() => _cancellation?.Dispose();
}
