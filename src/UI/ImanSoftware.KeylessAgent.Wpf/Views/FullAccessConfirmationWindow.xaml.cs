using System.Windows;
using ImanSoftware.KeylessAgent.Wpf.Services;

namespace ImanSoftware.KeylessAgent.Wpf.Views;

/// <summary>
/// The Full-access confirmation modal: a warning, an "I understand" tick that enables OK,
/// and Cancel. Shown EVERY time the user picks the Full access approval mode.
/// </summary>
public partial class FullAccessConfirmationWindow : Window
{
    public FullAccessConfirmationWindow()
    {
        InitializeComponent();
        Owner = System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        UnderstandCheckBox.Checked += (_, _) => OkButton.IsEnabled = true;
        UnderstandCheckBox.Unchecked += (_, _) => OkButton.IsEnabled = false;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
