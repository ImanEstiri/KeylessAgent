using System.IO;
using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Wpf.Infrastructure;
using ImanSoftware.KeylessAgent.Wpf.Services;
using ImanSoftware.KeylessAgent.Wpf.Tests.Fakes;
using ImanSoftware.KeylessAgent.Wpf.ViewModels;
using ImanSoftware.Outcomes;
using Xunit;

namespace ImanSoftware.KeylessAgent.Wpf.Tests.ViewModels;

public sealed class WorkspaceViewModelTests : IDisposable
{
    private sealed class FakeInspector : IWorkspaceInspector
    {
        private readonly WorkspaceCapabilities? _capabilities;
        private readonly Exception? _thrown;
        private readonly bool _fail;

        public FakeInspector(
            WorkspaceCapabilities? capabilities = null,
            Exception? thrown = null,
            bool fail = false)
            => (_capabilities, _thrown, _fail) = (capabilities, thrown, fail);

        public int CallCount;
        public string? LastPath;

        public Task<Outcome<WorkspaceCapabilities>> InspectAsync(
            string workspaceRoot, GitOptions options, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastPath = workspaceRoot;

            if (_thrown is not null)
                return Task.FromException<Outcome<WorkspaceCapabilities>>(_thrown);

            if (_fail)
                return Task.FromResult(Outcome.Failure<WorkspaceCapabilities>(new OutcomeError(
                    "inspect-failed", ErrorCodes.GitFailure, OutcomeErrorType.Failure)));

            return Task.FromResult(Outcome.Successful(_capabilities ?? new WorkspaceCapabilities(
                GitInstalled: true, IsRepository: true, CurrentBranch: "agent/fix", IsAgentBranch: true)));
        }
    }

    private readonly string _root;
    private readonly string _settingsFolder;
    private readonly string _workspace;
    private readonly ActivityFeed _feed = new();
    private readonly ImmediateDispatcher _dispatcher = new();
    private readonly AgentViewModel _agent;
    private readonly SettingsService _settings;

    public WorkspaceViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "keyless-ws-tests-" + Guid.NewGuid().ToString("N"));
        _settingsFolder = Path.Combine(_root, "settings");
        _workspace = Path.Combine(_root, "demo");
        Directory.CreateDirectory(_workspace);
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
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private WorkspaceViewModel CreateViewModel(FakeInspector inspector, Func<string?>? browse = null)
        => new(_agent, inspector, _settings, _dispatcher, browse ?? (() => null));

    [Fact]
    public async Task SelectValidFolder_InspectsCapabilities_AndPersistsTheRecent()
    {
        var inspector = new FakeInspector();
        var viewModel = CreateViewModel(inspector);

        viewModel.Select(_workspace);
        await WaitUntilAsync(() => !viewModel.IsInspecting);

        _agent.WorkspaceRoot.Should().Be(_workspace);
        _agent.BranchName.Should().Be("agent/fix");
        viewModel.SelectedPath.Should().Be(_workspace);
        viewModel.HasNote.Should().BeFalse();
        viewModel.Capabilities.Select(row => row.Label).Should().Equal(
            "Git installed", "Git repository", "Current branch", "Agent branch");
        viewModel.Capabilities[0].Value.Should().Be("yes");
        viewModel.Capabilities[2].Value.Should().Be("agent/fix");
        inspector.CallCount.Should().Be(1);
        inspector.LastPath.Should().Be(_workspace);
        _settings.Load().RecentWorkspaces.Should().Equal(_workspace);
        _settings.Load().LastWorkspace.Should().Be(_workspace);
        viewModel.Recents.Should().ContainSingle(card => card.Path == _workspace && card.IsNewest);
    }

    [Fact]
    public async Task SelectInvalidFolder_ShowsANote_AndTouchesNothing()
    {
        var inspector = new FakeInspector();
        var viewModel = CreateViewModel(inspector);
        var missing = Path.Combine(_root, "does-not-exist");

        viewModel.Select(missing);

        viewModel.HasNote.Should().BeTrue();
        viewModel.Note.Should().Contain("does-not-exist");
        _agent.WorkspaceRoot.Should().BeEmpty();
        inspector.CallCount.Should().Be(0);
        File.Exists(_settings.SettingsFilePath).Should().BeFalse();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Constructor_RestoresTheLastWorkspace_AndInspectsIt()
    {
        var seeded = SettingsService.WithRecentWorkspace(new WpfSettings(), _workspace);
        _settings.Save(seeded);
        var inspector = new FakeInspector();

        var viewModel = CreateViewModel(inspector);
        await WaitUntilAsync(() => inspector.CallCount > 0);

        _agent.WorkspaceRoot.Should().Be(_workspace);
        await WaitUntilAsync(() => _agent.BranchName is not null);
        _agent.BranchName.Should().Be("agent/fix");
        viewModel.Recents.Should().ContainSingle(card => card.Path == _workspace);
    }

    [Fact]
    public async Task InspectorException_ShowsAFriendlyNote_NeverCrashes()
    {
        var inspector = new FakeInspector(thrown: new InvalidOperationException("git exploded"));
        var viewModel = CreateViewModel(inspector);

        viewModel.Select(_workspace);
        await WaitUntilAsync(() => !viewModel.IsInspecting);

        viewModel.HasNote.Should().BeTrue();
        viewModel.Note.Should().Contain("Could not inspect this workspace");
        viewModel.Capabilities.Should().BeEmpty();
        inspector.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task FailedOutcome_ShowsAFriendlyNote()
    {
        var inspector = new FakeInspector(fail: true);
        var viewModel = CreateViewModel(inspector);

        viewModel.Select(_workspace);
        await WaitUntilAsync(() => !viewModel.IsInspecting);

        viewModel.Note.Should().Contain("Could not inspect this workspace");
    }

    [Fact]
    public async Task NoGitMode_ShowsTheNoGitNote()
    {
        var inspector = new FakeInspector(new WorkspaceCapabilities(false, false, "main", false));
        var viewModel = CreateViewModel(inspector);

        viewModel.Select(_workspace);
        await WaitUntilAsync(() => !viewModel.IsInspecting);

        viewModel.Note.Should().Contain("git was not found");
        viewModel.Capabilities[0].Value.Should().Be("no");
    }

    [Fact]
    public async Task Browse_WithFakeDialog_SelectsThePickedFolder()
    {
        var inspector = new FakeInspector();
        var viewModel = CreateViewModel(inspector, browse: () => _workspace);

        viewModel.BrowseCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsInspecting);

        viewModel.SelectedPath.Should().Be(_workspace);
        _agent.WorkspaceRoot.Should().Be(_workspace);
    }

    [Fact]
    public async Task Browse_WithCancelledDialog_ChangesNothing()
    {
        var inspector = new FakeInspector();
        var viewModel = CreateViewModel(inspector, browse: () => null);

        viewModel.BrowseCommand.Execute(null);

        viewModel.SelectedPath.Should().BeEmpty();
        inspector.CallCount.Should().Be(0);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task RemoveRecent_UpdatesTheListAndTheSettingsFile()
    {
        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        var inspector = new FakeInspector();
        var viewModel = CreateViewModel(inspector);
        viewModel.Select(_workspace);
        viewModel.Select(other);
        await WaitUntilAsync(() => viewModel.Recents.Count == 2);

        var firstCard = viewModel.Recents.First(card => card.Path == _workspace);
        firstCard.RemoveCommand.Execute(null);

        viewModel.Recents.Select(card => card.Path).Should().NotContain(_workspace);
        viewModel.Recents.Should().ContainSingle(card => card.Path == other && card.IsNewest);
        _settings.Load().RecentWorkspaces.Should().Equal(other);
    }

    [Fact]
    public async Task ReSelectingTheSameWorkspace_MovesItsCardToFront()
    {
        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        var inspector = new FakeInspector();
        var viewModel = CreateViewModel(inspector);
        viewModel.Select(_workspace);
        viewModel.Select(other);
        await WaitUntilAsync(() => viewModel.Recents.Count == 2);

        viewModel.Select(_workspace);
        await WaitUntilAsync(() => viewModel.Recents.Count == 2);

        viewModel.Recents[0].Path.Should().Be(_workspace);
        viewModel.Recents[0].IsNewest.Should().BeTrue();
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
