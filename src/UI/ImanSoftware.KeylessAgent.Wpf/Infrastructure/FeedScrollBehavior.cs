using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ImanSoftware.KeylessAgent.Wpf.Infrastructure;

/// <summary>
/// Attached behavior that scrolls an ItemsControl's host ScrollViewer to the end whenever new
/// items arrive (feed auto-scroll). Pure view logic — no business code in code-behind.
/// </summary>
public static class FeedScrollBehavior
{
    /// <summary>Identifies the Enabled attached property.</summary>
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(FeedScrollBehavior), new PropertyMetadata(false, OnEnabledChanged));

    /// <summary>Sets whether the ItemsControl auto-scrolls on collection changes.</summary>
    /// <param name="element">The ItemsControl.</param>
    /// <param name="value">True to enable auto-scroll.</param>
    public static void SetEnabled(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(EnabledProperty, value);
    }

    /// <summary>Gets whether the ItemsControl auto-scrolls on collection changes.</summary>
    /// <param name="element">The ItemsControl.</param>
    public static bool GetEnabled(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(EnabledProperty);
    }

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl control || !(bool)e.NewValue)
            return;

        if (control.ItemsSource is INotifyCollectionChanged changed)
            changed.CollectionChanged += (_, _) => ScrollToEnd(control);

        control.Loaded += (_, _) => ScrollToEnd(control);
    }

    private static void ScrollToEnd(ItemsControl control)
    {
        var node = (DependencyObject?)control;
        while (node is not null)
        {
            if (node is ScrollViewer viewer)
            {
                viewer.ScrollToEnd();
                return;
            }

            node = VisualTreeHelper.GetParent(node);
        }
    }
}
