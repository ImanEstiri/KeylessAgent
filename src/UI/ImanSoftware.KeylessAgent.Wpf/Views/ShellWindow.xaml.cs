using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Core.Factory;
using ImanSoftware.KeylessAgent.Wpf.Infrastructure;
using ImanSoftware.KeylessAgent.Wpf.Services;
using ImanSoftware.KeylessAgent.Wpf.ViewModels;
using ImanSoftware.KeylessAgent.Wpf.ViewModels.Dialogs;
using Microsoft.Win32;

namespace ImanSoftware.KeylessAgent.Wpf.Views;

/// <summary>
/// The main window: custom title bar, sidebar navigation, status bar, Windows 11 backdrop,
/// the modal dialog overlay, and the composition root for all pages.
/// </summary>
public partial class ShellWindow : Window
{
    private readonly IUiDispatcher _dispatcher = new WpfDispatcher();
    private readonly DialogHostViewModel _dialogHost = new();
    private readonly ActivityFeed _feed = new();

    /// <summary>Creates the shell window: backdrop, dialog host, all page view models, status bar.</summary>
    public ShellWindow()
    {
        InitializeComponent();
        var wpfSettings = new SettingsService();
        var saved = wpfSettings.Load();
        ApplyBackdropPlan(saved.BackdropPreference, saved.ReduceEffects);

        var busy = new BusyIndicator(_dispatcher);
        var dialogs = new DialogService(_dialogHost, _dispatcher);
        DialogHost.Initialize(_dialogHost, dialogs);

        var configuration = KeylessAgentServiceFactory.CreateConfigurationService();
        Func<IAgentUserInterface> uiFactory = () => new WpfUserInterface(_dispatcher, dialogs, _feed, busy);

        var runner = new AgentRunner(_dispatcher, _feed, uiFactory, AgentLoopFactory.Create);
        var agentViewModel = new AgentViewModel(runner, _dispatcher, _feed);
        agentViewModel.Undo = new UndoViewModel(KeylessAgentServiceFactory.CreateBackupService(), dialogs, agentViewModel, _dispatcher);

        var workspaceViewModel = new WorkspaceViewModel(
            agentViewModel,
            KeylessAgentServiceFactory.CreateWorkspaceInspector(),
            new SettingsService(),
            _dispatcher,
            BrowseForFolder);

        var chromeViewModel = new ChromeViewModel(
            KeylessAgentServiceFactory.CreateBrowserConnector(), configuration, agentViewModel, uiFactory, _dispatcher);
        var doctorViewModel = new DoctorViewModel(
            KeylessAgentServiceFactory.CreateChatDiagnostics(), configuration, agentViewModel, _dispatcher, chromeViewModel);
        var settingsViewModel = new SettingsViewModel(
            configuration, wpfSettings, agentViewModel, _dispatcher, OpenFileWithShell,
            preference => ApplyBackdropPlan(preference, wpfSettings.Load().ReduceEffects));
        var aboutViewModel = new AboutViewModel(OpenFolderWithShell);

        settingsViewModel.ReloadCommand.Execute(null);

        DataContext = new ShellViewModel(pageFactory: key => key switch
        {
            PageKey.Agent => agentViewModel,
            PageKey.Workspace => workspaceViewModel,
            PageKey.Doctor => doctorViewModel,
            PageKey.Settings => settingsViewModel,
            PageKey.About => aboutViewModel,
            _ => null,
        });
        DialogOverlay.DataContext = _dialogHost;
        StatusBarText.DataContext = chromeViewModel;
    }

    /// <summary>Reacts to maximize/restore so the content is not cut off by the resize border.</summary>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        RootGrid.Margin = WindowState == WindowState.Maximized
            ? SystemParameters.WindowResizeBorderThickness
            : default;
    }

    private void ApplyBackdropPlan(string preference, bool reduceEffects)
    {
        var plan = BackdropPlanner.Decide(Environment.OSVersion.Version.Build, preference, reduceEffects);

        OrbCanvas.Visibility = plan.ShowOrbs ? Visibility.Visible : Visibility.Collapsed;
        FallbackBackground.Visibility = plan.ShowFallbackGradient ? Visibility.Visible : Visibility.Collapsed;

        if (plan.Backdrop is not BackdropKind.None)
        {
            var applied = WindowBackdrop.TryApply(this, (int)plan.Backdrop);
            if (!applied)
                FallbackBackground.Visibility = Visibility.Visible;
        }
    }

    private string? BrowseForFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a workspace folder" };
        return dialog.ShowDialog(this) is true ? dialog.FolderName : null;
    }

    private static bool OpenFileWithShell(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool OpenFolderWithShell(string folder)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void OnShellPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_dialogHost.HasCurrent)
            return;

        switch (_dialogHost.Current)
        {
            case ConfirmDialogViewModel confirm:
                if (e.Key == Key.Enter)
                    confirm.ApproveCommand.Execute(null);
                else if (e.Key == Key.Escape)
                    confirm.DeclineCommand.Execute(null);
                else
                    return;
                e.Handled = true;
                break;

            case ChoiceDialogViewModel choice:
                RouteChoiceKey(choice, e);
                break;

            case AskDialogViewModel ask:
                if (e.Key == Key.Escape)
                    ask.CancelCommand.Execute(null);
                else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
                    ask.SubmitCommand.Execute(null);
                else
                    return;
                e.Handled = true;
                break;
        }
    }

    private static void RouteChoiceKey(ChoiceDialogViewModel choice, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up:
                choice.MoveUp();
                e.Handled = true;
                break;
            case Key.Down:
                choice.MoveDown();
                e.Handled = true;
                break;
            case Key.Enter:
                choice.ConfirmCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                choice.CancelCommand.Execute(null);
                e.Handled = true;
                break;
            default:
                if (DialogDigitOf(e.Key) is { } digit)
                {
                    choice.PickDigit(digit);
                    e.Handled = true;
                }
                break;
        }
    }

    private static int? DialogDigitOf(Key key) => key switch
    {
        >= Key.D1 and <= Key.D9 => key - Key.D1 + 1,
        >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad1 + 1,
        _ => null,
    };

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
