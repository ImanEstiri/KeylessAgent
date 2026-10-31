using System.Windows;
using System.Windows.Controls;
using ImanSoftware.KeylessAgent.Wpf.ViewModels;

namespace ImanSoftware.KeylessAgent.Wpf.Views;

/// <summary>
/// View of the agent page (feed, composer, changes panel); all behavior lives in
/// <c>AgentViewModel</c>. Feed items render through their per-type DataTemplates; the view only
/// forwards the Ctrl+L composer-focus request to the composer box.
/// </summary>
public partial class AgentView : UserControl
{
    /// <summary>Creates the agent page view and wires the composer-focus request.</summary>
    public AgentView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is AgentViewModel previous)
            previous.FocusComposerRequested -= OnFocusComposerRequested;

        if (e.NewValue is AgentViewModel current)
            current.FocusComposerRequested += OnFocusComposerRequested;
    }

    private void OnFocusComposerRequested(object? sender, EventArgs e) => ComposerBox.Focus();
}
