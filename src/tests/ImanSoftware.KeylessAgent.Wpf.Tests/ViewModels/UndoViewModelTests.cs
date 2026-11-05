using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Wpf.Infrastructure;
using ImanSoftware.KeylessAgent.Wpf.Services;
using ImanSoftware.KeylessAgent.Wpf.Tests.Fakes;
using ImanSoftware.KeylessAgent.Wpf.ViewModels;
using ImanSoftware.KeylessAgent.Wpf.ViewModels.Feed;
using ImanSoftware.Outcomes;
using Xunit;

namespace ImanSoftware.KeylessAgent.Wpf.Tests.ViewModels;

public sealed class UndoViewModelTests
{
    private readonly ActivityFeed _feed = new();
    private readonly ImmediateDispatcher _dispatcher = new();
    private readonly AgentViewModel _agent;
    private readonly FakeBackupService _backup = new();
    private readonly FakeDialogService _dialogs = new();
    private readonly UndoViewModel _viewModel;

    public UndoViewModelTests()
    {
        _agent = new AgentViewModel(
            new AgentRunner(
                _dispatcher,
                _feed,
                () => throw new NotSupportedException("turns are not part of these tests"),
                (_, _) => throw new NotSupportedException("turns are not part of these tests")),
            _dispatcher,
            _feed);
        _viewModel = new UndoViewModel(_backup, _dialogs, _agent, _dispatcher);
    }

    [Fact]
    public async Task DeclinedConfirmation_CallsNothing()
    {
        _agent.WorkspaceRoot = @"G:\demo";
        _dialogs.ConfirmResult = false;

        _viewModel.UndoCommand.Execute(null);
        await WaitUntilAsync(() => !_viewModel.IsRestoring);

        _dialogs.ConfirmCallCount.Should().Be(1);
        _backup.CallCount.Should().Be(0);
        _viewModel.HasRestoredFiles.Should().BeFalse();
        _viewModel.Note.Should().BeEmpty();
    }

    [Fact]
    public async Task AcceptedConfirmation_RestoresAndListsTheFiles()
    {
        _agent.WorkspaceRoot = @"G:\demo";

        _viewModel.UndoCommand.Execute(null);
        await WaitUntilAsync(() => !_viewModel.IsRestoring);

        _backup.CallCount.Should().Be(1);
        _backup.LastWorkspace.Should().Be(@"G:\demo");
        _viewModel.RestoredFiles.Should().Equal("src/a.cs", "src/b.cs");
        _viewModel.HasRestoredFiles.Should().BeTrue();
        _viewModel.Note.Should().Contain("Restored 2 file(s)");
    }

    [Fact]
    public async Task FailedUndo_ShowsTheServiceMessage()
    {
        _agent.WorkspaceRoot = @"G:\demo";
        _backup.Fail = true;

        _viewModel.UndoCommand.Execute(null);
        await WaitUntilAsync(() => !_viewModel.IsRestoring);

        _viewModel.Note.Should().Contain("KA_FILE_NOT_FOUND");
        _viewModel.HasRestoredFiles.Should().BeFalse();
    }

    [Fact]
    public void UndoCommand_DisabledWithoutWorkspace()
    {
        _viewModel.UndoCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task BackupException_ShowsAFriendlyMessage_NeverCrashes()
    {
        _agent.WorkspaceRoot = @"G:\demo";
        var backup = new ThrowingBackupService();
        var viewModel = new UndoViewModel(backup, _dialogs, _agent, _dispatcher);

        viewModel.UndoCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsRestoring);

        viewModel.Note.Should().Contain("InvalidOperationException: disk gone");
    }

    private sealed class ThrowingBackupService : IBackupService
    {
        public Task<Outcome> BackupAsync(string workspaceRoot, string fullPath)
            => throw new NotSupportedException();

        public Task<Outcome<IReadOnlyList<string>>> UndoLatestAsync(string workspaceRoot)
            => throw new InvalidOperationException("disk gone");
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
