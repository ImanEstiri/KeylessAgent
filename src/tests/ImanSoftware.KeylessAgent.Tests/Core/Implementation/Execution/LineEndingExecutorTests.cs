using FluentAssertions;
using ImanSoftware.FileStorage;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Models.Execution;
using ImanSoftware.KeylessAgent.Abstractions.Models.Parser;
using ImanSoftware.KeylessAgent.Core.Implementation.Execution;
using ImanSoftware.KeylessAgent.Core.Implementation.Git;
using ImanSoftware.KeylessAgent.Tests.Fakes;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Execution;

/// <summary>
/// Executor-level line-ending behavior: workspace files are CRLF, model text is LF.
/// </summary>
public class LineEndingExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ka-eol-exec-" + Guid.CreateVersion7().ToString("N"));
    private readonly IFileStorage _storage = ImanFileStorageFactory.CreateStorage();
    private readonly FakeAgentUserInterface _ui = new();
    private readonly BackupService _backup;
    private readonly WorkspaceCommandExecutor _executor;

    public LineEndingExecutorTests()
    {
        Directory.CreateDirectory(_root);
        var guard = new BranchGuard(new FakeGitService { CurrentBranch = "agent/test" });
        _backup = new BackupService(_storage, new FakeDateTimeProvider());
        _executor = new WorkspaceCommandExecutor(
            new UpdateFileExecutor(_storage, guard, _backup),
            new EditFileExecutor(_storage, guard, _backup),
            new NewFileExecutor(_storage, guard),
            new ReadFileExecutor(_storage));
    }

    private AgentExecutionContext Context()
        => new() { WorkspaceRoot = _root, Options = new() { Mode = ApprovalMode.Auto }, Ui = _ui };

    private async Task<string> ReadAsync(string name)
        => (await _storage.ReadAllTextAsync(Path.Combine(_root, name))).Data;

    [Fact]
    public async Task EditFile_CrlfFileWithLfFind_MatchesOnceAndStaysCrlf()
    {
        var path = Path.Combine(_root, "svc.cs");
        var crlf = "class S\r\n{\r\n    // TODO: fix me\r\n}\r\n";
        await _storage.WriteAllTextAsync(path, crlf);

        var result = await _executor.ExecuteAsync(
            new EditFileCommand("svc.cs", "// TODO: fix me", "// FIXED") { Number = 1 }, Context());

        result.Status.Should().Be(CommandStatus.Ok, result.ErrorMessage ?? "");
        var written = await ReadAsync("svc.cs");
        written.Should().Be("class S\r\n{\r\n    // FIXED\r\n}\r\n");
        written.Should().NotContain("\n\r", "no double conversions");
        CountLoneLf(written).Should().Be(0, "the file stays 100% CRLF");
    }

    [Fact]
    public async Task EditFile_LfFindWithLfInMiddle_MatchesAcrossLineBreaks()
    {
        var path = Path.Combine(_root, "multi.cs");
        await _storage.WriteAllTextAsync(path, "start\r\nmiddle\r\nend\r\n");

        var result = await _executor.ExecuteAsync(
            new EditFileCommand("multi.cs", "start\nmiddle", "begin\nmiddle") { Number = 1 }, Context());

        result.Status.Should().Be(CommandStatus.Ok);
        (await ReadAsync("multi.cs")).Should().Be("begin\r\nmiddle\r\nend\r\n");
    }

    [Fact]
    public async Task EditFile_MixedEndingFile_WritesDominantEnding()
    {
        var path = Path.Combine(_root, "mixed.cs");
        await _storage.WriteAllTextAsync(path, "a\r\nb\r\nc\nd\r\ne\r\n"); // CRLF dominant

        var result = await _executor.ExecuteAsync(
            new EditFileCommand("mixed.cs", "c\nd", "C\r\nD") { Number = 1 }, Context());

        result.Status.Should().Be(CommandStatus.Ok);
        var written = await ReadAsync("mixed.cs");
        written.Should().Be("a\r\nb\r\nC\r\nD\r\ne\r\n");
        CountLoneLf(written).Should().Be(0, "dominant ending (CRLF) wins for the whole file");
    }

    [Fact]
    public async Task EditFile_BomAndCrlf_AreBothPreserved()
    {
        var path = Path.Combine(_root, "bom.cs");
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var bytes = bom.Concat(System.Text.Encoding.UTF8.GetBytes("x\r\ny\r\n")).ToArray();
        await _storage.WriteAllBytesAsync(path, bytes);

        var result = await _executor.ExecuteAsync(
            new EditFileCommand("bom.cs", "x\ny", "z\nw") { Number = 1 }, Context());

        result.Status.Should().Be(CommandStatus.Ok);
        var written = (await _storage.ReadAllBytesAsync(path)).Data;
        written.Should().StartWith(bom);
        System.Text.Encoding.UTF8.GetString(written[3..]).Should().Be("z\r\nw\r\n");
    }

    [Fact]
    public async Task UpdateFile_LfContent_KeepsCrlfEnding()
    {
        var path = Path.Combine(_root, "u.cs");
        await _storage.WriteAllTextAsync(path, "old\r\ncontent\r\n");

        var result = await _executor.ExecuteAsync(
            new UpdateFileCommand("u.cs", "brand\nnew\ncontent\n") { Number = 1 }, Context());

        result.Status.Should().Be(CommandStatus.Ok);
        var written = await ReadAsync("u.cs");
        written.Should().Be("brand\r\nnew\r\ncontent\r\n");
    }

    [Fact]
    public async Task NewFile_ObeysEditorConfig()
    {
        File.WriteAllText(Path.Combine(_root, ".editorconfig"), "[*]\nend_of_line = lf\n");

        var result = await _executor.ExecuteAsync(
            new NewFileCommand("gen/New.cs", "class New {}\nmore\n") { Number = 1 }, Context());

        result.Status.Should().Be(CommandStatus.Ok);
        (await ReadAsync("gen/New.cs")).Should().Be("class New {}\nmore\n");
    }

    [Fact]
    public async Task NewFile_NoConfigOnWindows_UsesNewline()
    {
        var result = await _executor.ExecuteAsync(
            new NewFileCommand("plain/New.txt", "a\nb\n") { Number = 1 }, Context());

        result.Status.Should().Be(CommandStatus.Ok);
        var written = await ReadAsync("plain/New.txt");
        var expected = "a\nb\n".Replace("\n", Environment.NewLine);
        written.Should().Be(expected);
    }

    private static int CountLoneLf(string text)
    {
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n' && (i == 0 || text[i - 1] != '\r'))
                count++;
        }

        return count;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
