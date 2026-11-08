using System.Windows;
using ImanSoftware.KeylessAgent.Wpf.Views;

namespace ImanSoftware.KeylessAgent.Wpf.Services;

/// <summary>
/// Opens the Full-access confirmation modal on the UI thread and completes with the user's
/// decision (true = OK after ticking "I understand"). The default confirm delegate of the
/// approval-mode selector.
/// </summary>
public static class FullAccessModal
{
    public static Task<bool> ConfirmAsync()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var app = System.Windows.Application.Current;
        if (app?.Dispatcher is null)
        {
            completion.SetResult(false);
            return completion.Task;
        }

        app.Dispatcher.Invoke(() =>
        {
            try
            {
                var window = new FullAccessConfirmationWindow
                {
                    Owner = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive),
                };
                completion.SetResult(window.ShowDialog() == true);
            }
            catch
            {
                completion.SetResult(false);
            }
        });

        return completion.Task;
    }
}
