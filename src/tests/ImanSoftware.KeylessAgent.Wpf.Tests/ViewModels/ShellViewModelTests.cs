using FluentAssertions;
using ImanSoftware.KeylessAgent.Wpf.ViewModels;
using Xunit;

namespace ImanSoftware.KeylessAgent.Wpf.Tests.ViewModels;

public sealed class ShellViewModelTests
{
    [Fact]
    public void ShellViewModel_StartsOnTheAgentPage()
    {
        var shell = new ShellViewModel();

        shell.CurrentPageKey.Should().Be(PageKey.Agent);
        shell.SelectedItem.Key.Should().Be(PageKey.Agent);
        shell.SelectedItem.IsSelected.Should().BeTrue();
        shell.NavItems.Select(item => item.Key).Should().Equal(
            PageKey.Agent, PageKey.Workspace, PageKey.Doctor, PageKey.Settings, PageKey.About);
    }

    [Fact]
    public void Navigate_MovesSelectionToTheRequestedPage()
    {
        var shell = new ShellViewModel();

        shell.Navigate(PageKey.Settings);

        shell.CurrentPageKey.Should().Be(PageKey.Settings);
        shell.SelectedItem.Key.Should().Be(PageKey.Settings);
        shell.SelectedItem.IsSelected.Should().BeTrue();
        shell.NavItems.Single(item => item.Key == PageKey.Agent).IsSelected.Should().BeFalse();
    }

    [Fact]
    public void Navigate_WithoutFactory_CurrentPageStaysNull()
    {
        var shell = new ShellViewModel();

        shell.Navigate(PageKey.About);

        shell.CurrentPage.Should().BeNull();
    }

    [Fact]
    public void Navigate_WithFactory_ResolvesThePageViewModel()
    {
        var pages = new Dictionary<PageKey, object> { [PageKey.Workspace] = new object() };
        var shell = new ShellViewModel(key => pages.TryGetValue(key, out var page) ? page : null);

        shell.Navigate(PageKey.Workspace);
        shell.CurrentPage.Should().BeSameAs(pages[PageKey.Workspace]);

        shell.Navigate(PageKey.Agent);
        shell.CurrentPage.Should().BeNull();
    }

    [Fact]
    public void NavigateCommand_ExecutesNavigate()
    {
        var shell = new ShellViewModel();

        shell.NavigateCommand.Execute(PageKey.Doctor);

        shell.CurrentPageKey.Should().Be(PageKey.Doctor);
        shell.SelectedItem.Key.Should().Be(PageKey.Doctor);
    }

    [Fact]
    public void NavigationItem_IsSelected_RaisesPropertyChanged()
    {
        var item = new NavigationItemViewModel(PageKey.Agent, "Agent");
        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        item.IsSelected = true;

        raised.Should().ContainSingle().Which.Should().Be(nameof(NavigationItemViewModel.IsSelected));
    }
}
