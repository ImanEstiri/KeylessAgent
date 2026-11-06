using System.Windows;
using System.Windows.Controls;
using ImanSoftware.KeylessAgent.Wpf.ViewModels;

namespace ImanSoftware.KeylessAgent.Wpf.Views;

/// <summary>
/// View of the doctor page; all behavior lives in <c>DoctorViewModel</c> and
/// <c>ChromeViewModel</c>. Loading the page starts the Chrome-state poll; unloading stops it.
/// </summary>
public partial class DoctorView : UserControl
{
    /// <summary>Creates the doctor page view.</summary>
    public DoctorView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is ChromeViewModel chrome)
                _ = chrome.RefreshChromeStateAsync();
            StartStatePoll();
        };
        Unloaded += (_, _) => StopStatePoll();
    }

    private void StartStatePoll()
    {
        if (DataContext is ChromeViewModel chrome)
            chrome.StartStatePoll();
    }

    private void StopStatePoll()
    {
        if (DataContext is ChromeViewModel chrome)
            chrome.StopStatePoll();
    }
}
