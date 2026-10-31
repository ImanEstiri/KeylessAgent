using System.Windows.Controls;

namespace ImanSoftware.KeylessAgent.Wpf.Views.Dialogs;

/// <summary>
/// View of the choice menu; all behavior lives in <c>ChoiceDialogViewModel</c>
/// (keyboard routing happens on the shell window).
/// </summary>
public partial class ChoiceDialogView : UserControl
{
    /// <summary>Creates the choice dialog view.</summary>
    public ChoiceDialogView()
    {
        InitializeComponent();
    }
}
