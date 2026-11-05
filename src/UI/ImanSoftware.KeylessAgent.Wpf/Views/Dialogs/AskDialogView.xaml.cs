using System.Windows.Controls;

namespace ImanSoftware.KeylessAgent.Wpf.Views.Dialogs;

/// <summary>
/// View of the ask dialog; all behavior lives in <c>AskDialogViewModel</c>
/// (keyboard routing happens on the shell window).
/// </summary>
public partial class AskDialogView : UserControl
{
    /// <summary>Creates the ask dialog view and focuses the answer box when it appears.</summary>
    public AskDialogView()
    {
        InitializeComponent();
        Loaded += (_, _) => AnswerBox.Focus();
    }
}
