using FluentAssertions;
using ImanSoftware.FileStorage;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Models.Execution;
using ImanSoftware.KeylessAgent.Abstractions.Models.Parser;
using ImanSoftware.KeylessAgent.Core.Implementation.Execution;
using ImanSoftware.KeylessAgent.Core.Implementation.Git;
using ImanSoftware.Outcomes;
using ImanSoftware.KeylessAgent.Tests.Fakes;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Execution;

public class WorkspaceCommandExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ka-exec-" + Guid.CreateVersion7().ToString("N"));
    private readonly IFileStorage _storage = ImanFileStorageFactory.CreateStorage();
    private readonly FakeGitService _git = new() { CurrentBranch = "agent/test", HasOrigin = true };
    private readonly FakeAgentUserInterface _ui = new();
    private readonly WorkspaceCommandExecutor _executor;

    public WorkspaceCommandExecutorTests()
    {
        Directory.CreateDirectory(_root);
        var guard = new BranchGuard(_git);
        var backup = new BackupService(_storage, new FakeDateTimeProvider());
        _executor = new WorkspaceCommandExecutor(
            new SayExecutor(),
            new NewFileExecutor(_storage, guard),
            new UpdateFileExecutor(_storage, guard, backup),
            new EditFileExecutor(_storage, guard, backup),
            new DeleteFileExecutor(_storage, guard, backup),
            new ReadFileExecutor(_storage),
            new MoveFileExecutor(_storage, guard, backup),
            new RunCommandExecutor(),
            new AskExecutor(),
            new CommitExecutor(_git, guard),
            new DoneExecutor());
    }

    private AgentExecutionContext Context(ApprovalMode mode = ApprovalMode.Ask, bool dryRun = false)
        => new()
        {
            WorkspaceRoot = _root,
            Options = new KeylessOptions { Mode = mode },
            DryRun = dryRun,
            Ui = _ui,
        };

    private async Task<CommandExecutionResult> Run(AgentScriptCommand command, ApprovalMode mode = ApprovalMode.Ask, bool dryRun = false)
        => await _executor.ExecuteAsync(command, Context(mode, dryRun));

    [Fact]
    public async Task NewFile_CreatesFileIncludingMissingParentFolders()
    {
        var result = await Run(new NewFileCommand("src/Deep/Nested/a.cs", "class A {}") { Number = 1 });

        result.Status.Should().Be(CommandStatus.Ok);
        _storage.FileExists(Path.Combine(_root, "src", "Deep", "Nested", "a.cs")).Should().BeTrue();
        (await _storage.ReadAllTextAsync(Path.Combine(_root, "src", "Deep", "Nested", "a.cs"))).Data.Should().Be("class A {}");
    }

    [Fact]
    public async Task NewFile_ExistingFile_FailsWithConflictCode()
    {
        await _storage.WriteAllTextAsync(Path.Combine(_root, "exists.txt"), "old");

        var result = await Run(new NewFileCommand("exists.txt", "new") { Number = 1 });

        result.Status.Should().Be(CommandStatus.Error);
        result.ErrorCode.Should().Be(ErrorCodes.FileAlreadyExists);
    }

    [Fact]
    public async Task NewFile_OutsideWorkspace_FailsWithSandboxCode()
    {
        var result = await Run(new NewFileCommand("../outside.txt", "x") { Number = 1 });

        result.Status.Should().Be(CommandStatus.Error);
        result.ErrorCode.Should().Be(ErrorCodes.PathOutsideWorkspace);
    }

    [Fact]
    public async Task NewFile_OnProtectedBranch_FailsWithBranchProtected()
    {
        _git.CurrentBranch = "main";

        var result = await Run(new NewFileCommand("a.txt", "x") { Number = 1 });

        result.Status.Should().Be(CommandStatus.Error);
        result.ErrorCode.Should().Be(ErrorCodes.BranchProtected);
    }

    [Fact]
    public async Task NewFile_DryRun_ChangesNothing()
    {
        var result = await Run(new NewFileCommand("dry.txt", "x") { Number = 1 }, dryRun: true);

        result.Status.Should().Be(CommandStatus.Ok);
        _storage.FileExists(Path.Combine(_root, "dry.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task UpdateFile_ReplacesContent()
    {
        await _storage.WriteAllTextAsync(Path.Combine(_root, "u.txt"), "old content");

        var result = await Run(new UpdateFileCommand("u.txt", "new content") { Number = 1 });

        result.Status.Should().Be(CommandStatus.Ok);
        (await _storage.ReadAllTextAsync(Path.Combine(_root, "u.txt"))).Data.Should().Be("new content");
    }

    [Fact]
    public async Task UpdateFile_MissingFile_FailsWithNotFound()
    {
        var result = await Run(new UpdateFileCommand("missing.txt", "x") { Number = 1 });

        result.Status.Should().Be(CommandStatus.Error);
        result.ErrorCode.Should().Be(ErrorCodes.FileNotFound);
    }

    [Fact]
    public async Task UpdateFile_PreservesBom()
    {
        var path = Path.Combine(_root, "bom.txt");
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        await _storage.WriteAllBytesAsync(path, bom.Concat(System.Text.Encoding.UTF8.GetBytes("with bom")).ToArray());

        var result = await Run(new UpdateFileCommand("bom.txt", "replaced") { Number = 1 });

        result.Status.Should().Be(CommandStatus.Ok);
        var bytes = (await _storage.ReadAllBytesAsync(path)).Data;
        bytes.Should().StartWith(bom);
        System.Text.Encoding.UTF8.GetString(bytes[3..]).Should().Be("replaced");
    }

    [Fact]
    public async Task UpdateFile_WithoutBom_StaysWithoutBom()
    {
        var path = Path.Combine(_root, "nobom.txt");
        await _storage.WriteAllTextAsync(path, "plain");

        await Run(new UpdateFileCommand("nobom.txt", "replaced") { Number = 1 });

        var bytes = (await _storage.ReadAllBytesAsync(path)).Data;
        bytes.Take(3).Should().NotBeEquivalentTo(new byte[] { 0xEF, 0xBB, 0xBF });
    }

    [Fact]
    public async Task EditFile_UniqueFind_ReplacesOnce()
    {
        await _storage.WriteAllTextAsync(Path.Combine(_root, "e.txt"), "alpha old beta gamma");

        var result = await Run(new EditFileCommand("e.txt", "old", "NEW") { Number = 1 });

        result.Status.Should().Be(CommandStatus.Ok);
        var text = (await _storage.ReadAllTextAsync(Path.Combine(_root, "e.txt"))).Data;
        text.Should().Be("alpha NEW beta gamma");
    }

    [Fact]
    public async Task EditFile_FindMatchesTwice_Fails()
    {
        await _storage.WriteAllTextAsync(Path.Combine(_root, "e2.txt"), "x old y old z");

        var result = await Run(new EditFileCommand("e2.txt", "old", "NEW") { Number = 1 });

        result.Status.Should().Be(CommandStatus.Error);
        result.ErrorCode.Should().Be(ErrorCodes.FindNotUnique);
        result.ErrorMessage.Should().Contain("matched 2 times");
    }

    [Fact]
    public async Task EditFile_FindMatchesZeroTimes_Fails()
    {
        await _storage.WriteAllTextAsync(Path.Combine(_root, "e3.txt"), "nothing here");

        var result = await Run(new EditFileCommand("e3.txt", "old", "NEW") { Number = 1 });

        result.Status.Should().Be(CommandStatus.Error);
        result.ErrorCode.Should().Be(ErrorCodes.FindNotUnique);
        result.ErrorMessage.Should().Contain("matched 0 times");
    }

    [Fact]
    public async Task DeleteFile_Confirmed_DeletesAfterBackup()
    {
        await _storage.WriteAllTextAsync(Path.Combine(_root, "d.txt"), "doomed");

        var result = await Run(new DeleteFileCommand("d.txt") { Number = 1 }, mode: ApprovalMode.Ask);

        result.Status.Should().Be(CommandStatus.Ok);
        _storage.FileExists(Path.Combine(_root, "d.txt")).Should().BeFalse();
        _ui.Confirmations.Should().ContainSingle();
    }

    [Fact]
    public async Task DeleteFile_DeclinedByUser_FailsWithDeclinedCode()
    {
        await _storage.WriteAllTextAsync(Path.Combine(_root, "d2.txt"), "keep me");
        _ui.ConfirmAnswer = false;

        var result = await Run(new DeleteFileCommand("d2.txt") { Number = 1 }, mode: ApprovalMode.Ask);

        result.Status.Should().Be(CommandStatus.Error);
        result.ErrorCode.Should().Be(ErrorCodes.CommandDeclined);
        _storage.FileExists(Path.Combine(_root, "d2.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteFile_InAutoMode_DoesNotAsk()
    {
        await _storage.WriteAllTextAsync(Path.Combine(_root, "d3.txt"), "auto");

        var result = await Run(new DeleteFileCommand("d3.txt") { Number = 1 }, mode: ApprovalMode.Auto);

        result.Status.Should().Be(CommandStatus.Ok);
        _ui.Confirmations.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadFile_ReturnsContentAsOutput()
    {
        await _storage.WriteAllTextAsync(Path.Combine(_root, "r.txt"), "read me");

        var result = await Run(new ReadFileCommand("r.txt") { Number = 1 });

        result.Status.Should().Be(CommandStatus.Ok);
        result.Output.Should().Be("read me");
    }

    [Fact]
    public async Task ReadFile_MissingFile_Fails()
    {
        var result = await Run(new ReadFileCommand("nope.txt") { Number = 1 });

        result.ErrorCode.Should().Be(ErrorCodes.FileNotFound);
    }

    [Fact]
    public async Task Say_PrintsThroughUi()
    {
        var result = await Run(new SayCommand("hello there") { Number = 1 });

        result.Status.Should().Be(CommandStatus.Ok);
        _ui.Said.Should().Contain("hello there");
    }

    [Fact]
    public async Task Ask_ReturnsUserAnswerAsOutput()
    {
        _ui.AskAnswer = "use SQL Server";

        var result = await Run(new AskCommand("Which database?") { Number = 1 });

        result.Status.Should().Be(CommandStatus.Ok);
        result.Output.Should().Contain("use SQL Server");
        _ui.Questions.Should().ContainSingle().Which.Should().Be("Which database?");
    }

    [Fact]
    public async Task Commit_StagesCommitsAndPushes()
    {
        await _storage.WriteAllTextAsync(Path.Combine(_root, "c.txt"), "committed");

        var result = await Run(new CommitCommand("Add c") { Number = 1 }, mode: ApprovalMode.Auto);

        result.Status.Should().Be(CommandStatus.Ok);
        _git.Calls.Should().ContainInOrder("add-all", "commit:Add c", "push:agent/test");
    }

    [Fact]
    public async Task Commit_PushFailure_IsReported()
    {
        _git.PushResult = _ => Outcome.Failure(new OutcomeError("rejected", ErrorCodes.GitFailure, OutcomeErrorType.Failure));

        var result = await Run(new CommitCommand("Add c") { Number = 1 }, mode: ApprovalMode.Auto);

        result.Status.Should().Be(CommandStatus.Error);
        result.ErrorMessage.Should().Contain("push failed");
    }

    [Fact]
    public async Task RunCommand_DeniedByPolicy_FailsBeforeRunning()
    {
        var result = await Run(new RunCommandCommand("format C:") { Number = 1 }, mode: ApprovalMode.Auto);

        result.Status.Should().Be(CommandStatus.Error);
        result.ErrorCode.Should().Be(ErrorCodes.CommandDenied);
    }

    [Fact]
    public async Task Move_InAskMode_Declined_DoesNotMove()
    {
        await _storage.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "x");
        _ui.ConfirmAnswer = false;

        var result = await Run(new MoveFileCommand("a.txt", "b.txt") { Number = 1 }, mode: ApprovalMode.Ask);

        result.Status.Should().Be(CommandStatus.Error);
        result.ErrorCode.Should().Be(ErrorCodes.CommandDeclined);
        _storage.FileExists(Path.Combine(_root, "a.txt")).Should().BeTrue("nothing moved on decline");
        _storage.FileExists(Path.Combine(_root, "b.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task Move_InAskMode_Confirmed_MovesAndShowsDiff()
    {
        await _storage.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "x");

        var result = await Run(new MoveFileCommand("a.txt", "b.txt") { Number = 1 }, mode: ApprovalMode.Ask);

        result.Status.Should().Be(CommandStatus.Ok);
        _storage.FileExists(Path.Combine(_root, "b.txt")).Should().BeTrue();
        _storage.FileExists(Path.Combine(_root, "a.txt")).Should().BeFalse("the source is gone after the move");
        _ui.Diffs.Should().ContainSingle("the move is shown as a diff together with the confirmation");
    }

    [Fact]
    public async Task RunCommand_AllowedCommand_RunsAndCapturesOutput()
    {
        var result = await Run(new RunCommandCommand("cmd /c echo ka-ok") { Number = 1 }, mode: ApprovalMode.Auto);

        result.Status.Should().Be(CommandStatus.Ok);
        result.ExitCode.Should().Be(0);
        result.Output.Should().Contain("ka-ok");
        result.DurationSeconds.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task RunCommand_NonZeroExit_IsAnErrorResult()
    {
        var result = await Run(new RunCommandCommand("cmd /c exit 2") { Number = 1 }, mode: ApprovalMode.Auto);

        result.Status.Should().Be(CommandStatus.Error);
        result.ErrorCode.Should().Be(ErrorCodes.CommandFailed);
        result.ExitCode.Should().Be(2);
    }

    [Fact]
    public async Task RunCommand_Cancelled_ReportsCancelledNotTimeout()
    {
        using var cts = new CancellationTokenSource();
        var context = Context(mode: ApprovalMode.Auto);
        context = context with { CancellationToken = cts.Token };
        cts.CancelAfter(300);

        var result = await _executor.ExecuteAsync(
            new RunCommandCommand("cmd /c echo partial & ping -n 30 127.0.0.1 > nul") { Number = 1 }, context);

        result.Status.Should().Be(CommandStatus.Error);
        result.ErrorCode.Should().Be(ErrorCodes.Cancelled, "a cancelled command is not a timeout");
        result.FailureReason.Should().Be("cancelled");
        result.Output.Should().Contain("partial", "output printed before the kill is kept");
    }

    [Fact]
    public async Task RunCommand_ProgramMissing_ReturnsCommandNotFound()
    {
        var result = await Run(new RunCommandCommand("ka-test-no-such-program-xyz") { Number = 1 }, mode: ApprovalMode.Auto);

        result.Status.Should().Be(CommandStatus.Error);
        result.ErrorCode.Should().Be(ErrorCodes.CommandNotFound);
        result.FailureReason.Should().Be("command_not_found");
        result.ErrorMessage.Should().Contain("ka-test-no-such-program-xyz");
    }

    [Fact]
    public async Task RunCommand_InAskMode_RequiresConfirmation()
    {
        _ui.ConfirmAnswer = false;

        var result = await Run(new RunCommandCommand("cmd /c echo nope") { Number = 1 }, mode: ApprovalMode.Ask);

        result.Status.Should().Be(CommandStatus.Error);
        result.ErrorCode.Should().Be(ErrorCodes.CommandDeclined);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
