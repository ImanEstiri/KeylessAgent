using FluentAssertions;
using ImanSoftware.KeylessAgent.Wpf.ViewModels;
using Xunit;

namespace ImanSoftware.KeylessAgent.Wpf.Tests.ViewModels;

public sealed class AboutViewModelTests
{
    [Fact]
    public void Version_ComesFromTheAssembly()
    {
        var expected = typeof(AboutViewModel).Assembly.GetName().Version;

        new AboutViewModel(_ => true).Version.Should().Be($"{expected!.Major}.{expected.Minor}.{expected.Build}");
    }

    [Fact]
    public void OpenFolder_PassesTheRepositoryFolder()
    {
        string? opened = null;
        var viewModel = new AboutViewModel(folder =>
        {
            opened = folder;
            return true;
        }, @"G:\repo");

        viewModel.OpenRepositoryFolderCommand.Execute(null);

        opened.Should().Be(@"G:\repo");
        viewModel.Note.Should().BeEmpty();
    }

    [Fact]
    public void OpenFolder_Failure_ShowsANote()
    {
        var viewModel = new AboutViewModel(_ => false);

        viewModel.OpenRepositoryFolderCommand.Execute(null);

        viewModel.Note.Should().Contain("Could not open the folder.");
    }

    [Fact]
    public void Texts_CarryTheNotices()
    {
        var viewModel = new AboutViewModel(_ => true);

        viewModel.Name.Should().Be("KeylessAgent");
        viewModel.Disclaimer.Should().Contain("not affiliated with DeepSeek");
        viewModel.Warning.Should().Contain("Terms of Use");
        viewModel.Description.Should().NotBeEmpty();
        viewModel.RepositoryFolder.Should().NotBeEmpty();
    }
}
