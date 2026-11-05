using FluentAssertions;
using ImanSoftware.FileStorage;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Models.Execution;
using ImanSoftware.KeylessAgent.Abstractions.Models.Parser;
using ImanSoftware.KeylessAgent.Core.Implementation.Execution;
using ImanSoftware.KeylessAgent.Core.Implementation.Git;
using ImanSoftware.KeylessAgent.Tests.Fakes;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Execution;

/// <summary>
/// @@mkdir: creates a folder (and parents), sandboxed, never confirmed, idempotent
/// (an existing folder is fine, an existing FILE conflicts), journaled for undo.
/// </summary>
public class MkdirExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ka-mkdir-" + Guid.CreateVersion7().ToString("N"));
    private readonly IFileStorage _storage = ImanFileStorageFactory.CreateStorage();
    private readonly BackupService _backup;
    private readonly MkdirExecutor _executor;

    public MkdirExecutorTests()
    {
        Directory.CreateDirectory(_root);
        _backup = new BackupService(_storage, new FakeDateTimeProvider());
        var git = new FakeGitService { CurrentBranch = "agent/test", HasOrigin = true };
        _executor = new MkdirExecutor(_storage, new BranchGuard(git), _backup);
    }

    private AgentExecutionContext Context(bool dryRun = false)
        => new()
        {
            WorkspaceRoot = _root,
            Options = new KeylessOptions { Mode = ApprovalMode.Auto },
            DryRun = dryRun,
            Ui = new FakeAgentUserInterface(),
        };

    [Fact]
    public async Task Mkdir_CreatesTheFolder()
    {
        var result = await _executor.ExecuteAsync(
            new MkdirCommand("generated/output") { Number = 1 }, Context());

        result.Status.Should().Be(CommandStatus.Ok);
        Directory.Exists(Path.Combine(_root, "generated", "output")).Should().BeTrue();
    }

    [Fact]
    public async Task Mkdir_CreatesMissingParents()
    {
        var result = await _executor.ExecuteAsync(
            new MkdirCommand("a/b/c/deep") { Number = 1 }, Context());

        result.Status.Should().Be(CommandStatus.Ok);
        Directory.Exists(Path.Combine(_root, "a", "b", "c", "deep")).Should().BeTrue();
    }

    [Fact]
    public async Task Mkdir_ExistingFolder_IsIdempotent()
    {
        Directory.CreateDirectory(Path.Combine(_root, "already-there"));

        var result = await _executor.ExecuteAsync(
            new MkdirCommand("already-there") { Number = 1 }, Context());

        result.Status.Should().Be(CommandStatus.Ok, "an existing folder makes mkdir a no-op, not an error");
    }

    [Fact]
    public async Task Mkdir_ExistingFile_Conflicts()
    {
        File.WriteAllText(Path.Combine(_root, "occupied.txt"), "content");

        var result = await _executor.ExecuteAsync(
            new MkdirCommand("occupied.txt") { Number = 1 }, Context());

        result.Status.Should().Be(CommandStatus.Error);
        result.ErrorCode.Should().Be(ErrorCodes.FileAlreadyExists);
        File.ReadAllText(Path.Combine(_root, "occupied.txt")).Should().Be("content");
    }

    [Fact]
    public async Task Mkdir_OutsideWorkspace_IsRejected()
    {
        var result = await _executor.ExecuteAsync(
            new MkdirCommand("../escape-dir") { Number = 1 }, Context());

        result.Status.Should().Be(CommandStatus.Error);
        result.ErrorCode.Should().Be(ErrorCodes.PathOutsideWorkspace);
        Directory.Exists(Path.Combine(Path.GetTempPath(), "escape-dir")).Should().BeFalse();
    }

    [Fact]
    public async Task Mkdir_DryRun_CreatesNothing()
    {
        var result = await _executor.ExecuteAsync(
            new MkdirCommand("dry-dir") { Number = 1 }, Context(dryRun: true));

        result.Status.Should().Be(CommandStatus.Ok);
        Directory.Exists(Path.Combine(_root, "dry-dir")).Should().BeFalse();
    }

    [Fact]
    public async Task Undo_RemovesAgentCreatedFolder_WhileEmpty()
    {
        var result = await _executor.ExecuteAsync(
            new MkdirCommand("agent-dir") { Number = 1 }, Context());

        result.Status.Should().Be(CommandStatus.Ok);
        Directory.Exists(Path.Combine(_root, "agent-dir")).Should().BeTrue();

        var undo = await _backup.UndoLatestAsync(_root);

        undo.IsSuccess.Should().BeTrue();
        Directory.Exists(Path.Combine(_root, "agent-dir")).Should().BeFalse(
            "undo removes an agent-created folder while it is empty");
    }

    [Fact]
    public async Task Undo_KeepsTheFolder_WhenNotEmpty()
    {
        var result = await _executor.ExecuteAsync(
            new MkdirCommand("agent-dir") { Number = 1 }, Context());
        File.WriteAllText(Path.Combine(_root, "agent-dir", "user-file.txt"), "user content");

        var undo = await _backup.UndoLatestAsync(_root);

        Directory.Exists(Path.Combine(_root, "agent-dir")).Should().BeTrue(
            "a folder with content is never removed");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup is best-effort */ }
    }
}
