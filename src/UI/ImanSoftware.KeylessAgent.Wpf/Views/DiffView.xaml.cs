using System.Windows.Controls;

namespace ImanSoftware.KeylessAgent.Wpf.Views;

/// <summary>
/// View of the diff card; all behavior lives in <c>DiffItem</c>. Rows render through
/// DataTemplates for <c>DiffLine</c> and <c>CollapsedRun</c>.
/// </summary>
public partial class DiffView : UserControl
{
    /// <summary>Creates the diff card view.</summary>
    public DiffView()
    {
        InitializeComponent();
    }
}
