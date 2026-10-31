using ImanSoftware.KeylessAgent.Wpf.Mvvm;

namespace ImanSoftware.KeylessAgent.Wpf.ViewModels;

/// <summary>
/// View model of the shell window: owns the five sidebar entries and the current page.
/// Pages are resolved through an optional factory so later phases can register real page
/// view models; without a factory, navigation only moves the sidebar selection.
/// </summary>
public sealed class ShellViewModel : ObservableObject
{
    private readonly Func<PageKey, object?>? _pageFactory;
    private object? _currentPage;
    private PageKey _currentPageKey;
    private NavigationItemViewModel _selectedItem;

    /// <summary>
    /// Creates the shell view model.
    /// </summary>
    /// <param name="pageFactory">
    /// Maps a page key to its page view model; may return null for pages that do not exist yet.
    /// </param>
    public ShellViewModel(Func<PageKey, object?>? pageFactory = null)
    {
        _pageFactory = pageFactory;
        NavItems =
        [
            new NavigationItemViewModel(PageKey.Agent, "Agent"),
            new NavigationItemViewModel(PageKey.Workspace, "Workspace"),
            new NavigationItemViewModel(PageKey.Doctor, "Doctor"),
            new NavigationItemViewModel(PageKey.Settings, "Settings"),
            new NavigationItemViewModel(PageKey.About, "About"),
        ];
        _selectedItem = NavItems[0];
        _selectedItem.IsSelected = true;
        NavigateCommand = new RelayCommand<PageKey>(Navigate);
    }

    /// <summary>The sidebar entries in display order; the Agent page is selected initially.</summary>
    public IReadOnlyList<NavigationItemViewModel> NavItems { get; }

    /// <summary>Navigates to the page key passed as command parameter.</summary>
    public RelayCommand<PageKey> NavigateCommand { get; }

    /// <summary>The key of the page that is currently shown.</summary>
    public PageKey CurrentPageKey
    {
        get => _currentPageKey;
        private set => SetProperty(ref _currentPageKey, value);
    }

    /// <summary>The page view model that is currently shown; null when the page has no view model yet.</summary>
    public object? CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    /// <summary>The currently selected sidebar entry; bound for the highlight style.</summary>
    public NavigationItemViewModel SelectedItem
    {
        get => _selectedItem;
        private set => SetProperty(ref _selectedItem, value);
    }

    /// <summary>Moves the sidebar selection to the key and refreshes the current page.</summary>
    public void Navigate(PageKey key)
    {
        foreach (var item in NavItems)
            item.IsSelected = item.Key == key;

        SelectedItem = NavItems.First(item => item.Key == key);
        CurrentPageKey = key;
        CurrentPage = _pageFactory?.Invoke(key);
    }
}
