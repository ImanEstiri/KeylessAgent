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
        {
            current.FocusComposerRequested += OnFocusComposerRequested;

            // Optional "Send with Enter" mode: plain Enter sends, Shift+Enter inserts a
            // newline. Ctrl+Enter always sends regardless of the setting.
            ComposerBox.PreviewKeyDown -= OnComposerKeyDown;
            ComposerBox.PreviewKeyDown += OnComposerKeyDown;
        }
    }

    private void OnComposerKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter
            || e.KeyboardDevice.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift))
            return;

        if (DataContext is AgentViewModel viewModel && viewModel.SendWithEnter)
        {
            e.Handled = true;
            viewModel.SendCommand.Execute(null);
        }
    }

    private void OnFocusComposerRequested(object? sender, EventArgs e) => ComposerBox.Focus();
}
