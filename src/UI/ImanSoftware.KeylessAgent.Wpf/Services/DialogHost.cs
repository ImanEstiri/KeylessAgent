using ImanSoftware.KeylessAgent.Wpf.ViewModels;

namespace ImanSoftware.KeylessAgent.Wpf.Services;

/// <summary>
/// Static accessor for the dialog infrastructure so any component (e.g. the agent Stop button
/// in a later phase) can cancel pending dialogs without wiring references through every
/// view model. Initialized once at window construction.
/// </summary>
public static class DialogHost
{
    private static DialogHostViewModel? _host;
    private static IDialogService? _dialogs;

    /// <summary>The view model of the currently shown dialog; null before initialization or when no dialog is open.</summary>
    public static object? Current => _host?.Current;

    /// <summary>
    /// Registers the shared dialog host and service. Call once at application start-up
    /// (later calls replace the previous registrations).
    /// </summary>
    /// <param name="host">The shell's dialog host view model.</param>
    /// <param name="dialogs">The dialog service that shows dialogs in it.</param>
    public static void Initialize(DialogHostViewModel host, IDialogService dialogs)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
    }

    /// <summary>
    /// Completes every pending dialog as cancelled (confirm → false, choice → cancelled,
    /// ask → empty). Called when the user stops a running turn.
    /// </summary>
    public static void CancelAll() => _dialogs?.CancelAll();
}
