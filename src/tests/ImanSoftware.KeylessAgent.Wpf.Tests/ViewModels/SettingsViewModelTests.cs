using System.IO;
using FluentAssertions;
using ImanSoftware.KeylessAgent.Wpf.Infrastructure;
using ImanSoftware.KeylessAgent.Wpf.Services;
using ImanSoftware.KeylessAgent.Wpf.Tests.Fakes;
using ImanSoftware.KeylessAgent.Wpf.ViewModels;
using ImanSoftware.KeylessAgent.Wpf.ViewModels.Feed;
using Xunit;

namespace ImanSoftware.KeylessAgent.Wpf.Tests.ViewModels;

public sealed class SettingsViewModelTests : IDisposable
{
    private readonly string _settingsFolder;
    private readonly SettingsService _settings;
    private readonly ActivityFeed _feed = new();
    private readonly ImmediateDispatcher _dispatcher = new();
    private readonly AgentViewModel _agent;
    private readonly FakeConfigurationService _configuration = new();

    public SettingsViewModelTests()
    {
        _settingsFolder = Path.Combine(Path.GetTempPath(), "keyless-wpf-settingspage-" + Guid.NewGuid().ToString("N"));
        _settings = new SettingsService(_settingsFolder);
        _agent = new AgentViewModel(
            new AgentRunner(
                _dispatcher,
                _feed,
                () => throw new NotSupportedException("turns are not part of these tests"),
                (_, _) => throw new NotSupportedException("turns are not part of these tests")),
            _dispatcher,
            _feed);
    }

    public void Dispose()
    {
        if (Directory.Exists(_settingsFolder))
            Directory.Delete(_settingsFolder, recursive: true);
    }

    private SettingsViewModel CreateViewModel(Func<string, bool>? openFile = null, Action<string>? backdropChanged = null)
        => new(_configuration, _settings, _agent, _dispatcher, openFile ?? (_ => true), backdropChanged);

    [Fact]
    public async Task Reload_MapsTheConfiguredGroups()
    {
        _agent.WorkspaceRoot = @"G:\demo";
        var viewModel = CreateViewModel();

        viewModel.ReloadCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsLoading);

        viewModel.HasNote.Should().BeFalse();
        viewModel.Groups.Select(group => group.Title).Should().Equal("Mode", "Browser", "Limits", "Snapshot", "Git", "Commands");
    }

    [Fact]
    public async Task Reload_WithoutWorkspace_IsDisabledAndShowsNoGroups()
    {
        var viewModel = CreateViewModel();

        viewModel.ReloadCommand.CanExecute(null).Should().BeFalse();
        viewModel.Groups.Should().BeEmpty();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task OpenKeylessJson_FailingOpener_ShowsANote()
    {
        _agent.WorkspaceRoot = @"G:\demo";
        string? opened = null;
        var viewModel = CreateViewModel(openFile: path =>
        {
            opened = path;
            return false;
        });

        viewModel.OpenKeylessJsonCommand.Execute(null);

        opened.Should().Be(@"G:\demo\keyless.json");
        viewModel.Note.Should().Contain("Could not open keyless.json");
        await Task.CompletedTask;
    }

    [Fact]
    public void TogglingDryRunDefault_PersistsToTheSettingsFile()
    {
        var viewModel = CreateViewModel();

        viewModel.DryRunDefault = true;

        viewModel.DryRunDefault.Should().BeTrue();
        _settings.Load().DryRunDefault.Should().BeTrue();
    }

    [Fact]
    public void ChangingTheBackdrop_PersistsAndNotifies()
    {
        string? notified = null;
        var viewModel = CreateViewModel(backdropChanged: preference => notified = preference);

        viewModel.BackdropPreference = "Acrylic";

        viewModel.BackdropPreference.Should().Be("Acrylic");
        _settings.Load().BackdropPreference.Should().Be("Acrylic");
        notified.Should().Be("Acrylic");
    }

    [Fact]
    public void ChangingReduceEffects_PersistsAndKeepsRecentWorkspacesFromOtherWriters()
    {
        _settings.Save(SettingsService.WithRecentWorkspace(_settings.Load(), @"C:\other"));

        var viewModel = CreateViewModel();
        viewModel.ReduceEffects = true;

        var reloaded = _settings.Load();
        reloaded.ReduceEffects.Should().BeTrue();
        reloaded.RecentWorkspaces.Should().Equal(@"C:\other");
    }

    [Fact]
    public void BackdropChoices_OffersTheGuideOptions()
    {
        SettingsViewModel.BackdropChoices.Should().Equal("Auto", "Mica", "Acrylic", "None");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            if (condition())
                return;

            await Task.Delay(10);
        }

        throw new TimeoutException("Condition was not met within 5 seconds.");
    }
}
