using ImanSoftware.KeylessAgent.Wpf.Mvvm;

namespace ImanSoftware.KeylessAgent.Wpf.ViewModels;

/// <summary>
/// One entry of the shell sidebar: the page it opens, its display title and its
/// selected state as tracked by <see cref="ShellViewModel"/>.
/// </summary>
public sealed class NavigationItemViewModel : ObservableObject
{
    private bool _isSelected;

    /// <summary>Creates a sidebar entry.</summary>
    /// <param name="key">The page this entry navigates to.</param>
    /// <param name="title">The label shown in the sidebar.</param>
    public NavigationItemViewModel(PageKey key, string title)
    {
        Key = key;
        Title = title;
    }

    /// <summary>The page this entry opens.</summary>
    public PageKey Key { get; }

    /// <summary>The label shown in the sidebar.</summary>
    public string Title { get; }

    /// <summary>Whether this entry is the active page (drives the highlight style).</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
