using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Core.Implementation.Execution;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Execution;

public class PathSandboxTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ka-tests-" + Guid.CreateVersion7().ToString("N"));
    private readonly PathSandbox _sandbox;

    public PathSandboxTests()
    {
        Directory.CreateDirectory(_root);
        _sandbox = new PathSandbox(_root);
    }

    [Fact]
    public void Resolve_RelativePath_ReturnsAbsolutePathInsideWorkspace()
    {
        var outcome = _sandbox.Resolve("src/Foo/Bar.cs");

        outcome.IsSuccess.Should().BeTrue();
        outcome.Data.Should().Be(Path.GetFullPath(Path.Combine(_root, "src", "Foo", "Bar.cs")));
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("src/../../escape.cs")]
    [InlineData("C:/Windows/system32.cs")]
    [InlineData("/abs/path.cs")]
    [InlineData("src\\windows-style.cs")]
    public void Resolve_UnsafePaths_AreRejected(string path)
    {
        var outcome = _sandbox.Resolve(path);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error.Code.Should().Be(ErrorCodes.PathOutsideWorkspace);
    }

    [Theory]
    [InlineData(".git/config")]
    [InlineData(".git/hooks/pre-commit")]
    [InlineData(".agent/session.json")]
    [InlineData(".agent/logs/turn-1.jsonl")]
    public void Resolve_InternalFolders_AreRejected(string path)
    {
        var outcome = _sandbox.Resolve(path);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error.Code.Should().Be(ErrorCodes.PathOutsideWorkspace);
    }

    [Fact]
    public void Resolve_NormalSiblingFolder_IsAllowed()
    {
        var outcome = _sandbox.Resolve("agent-notes.txt");

        outcome.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Resolve_EmptyPath_IsRejected()
    {
        var outcome = _sandbox.Resolve("  ");

        outcome.IsFailure.Should().BeTrue();
        outcome.Error.Code.Should().Be(ErrorCodes.PathOutsideWorkspace);
    }

    [Fact]
    public void Resolve_JunctionEscapingWorkspace_IsRejected()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ka-outside-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(outside);
        var junction = Path.Combine(_root, "escape-link");
        var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{junction}\" \"{outside}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        });
        mklink!.WaitForExit(5000);

        try
        {
            if (!Directory.Exists(junction))
                return; // junction creation unavailable in this environment

            var outcome = _sandbox.Resolve("escape-link/secret.cs");

            outcome.IsFailure.Should().BeTrue();
            outcome.Error.Code.Should().Be(ErrorCodes.PathOutsideWorkspace);
        }
        finally
        {
            if (Directory.Exists(junction))
                Directory.Delete(junction);
            Directory.Delete(outside);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup is best-effort */ }
    }
}
