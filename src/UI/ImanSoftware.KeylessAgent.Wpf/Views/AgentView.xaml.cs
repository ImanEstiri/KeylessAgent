using System.Windows.Controls;

namespace ImanSoftware.KeylessAgent.Wpf.Views;

/// <summary>
/// View of the agent page (feed, composer, changes panel); all behavior lives in
/// <c>AgentViewModel</c>. Feed items render through their per-type DataTemplates.
/// </summary>
public partial class AgentView : UserControl
{
    /// <summary>Creates the agent page view.</summary>
    public AgentView()
    {
        InitializeComponent();
    }
}
