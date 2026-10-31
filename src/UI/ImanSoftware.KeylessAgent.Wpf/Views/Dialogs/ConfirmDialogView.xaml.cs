using System.Windows.Controls;

namespace ImanSoftware.KeylessAgent.Wpf.Views.Dialogs;

/// <summary>
/// View of the confirmation dialog; all behavior lives in <c>ConfirmDialogViewModel</c>
/// (keyboard routing happens on the shell window).
/// </summary>
public partial class ConfirmDialogView : UserControl
{
    /// <summary>Creates the confirmation dialog view.</summary>
    public ConfirmDialogView()
    {
        InitializeComponent();
    }
}
