using System.IO;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Models.Execution;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Core.Implementation.Agent;
using Xunit;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Agent;

/// <summary>
/// The "verified:" line appended to file-changing results: bytes, lines, sha and the
/// first/last snippets for LF and CRLF files, empty files and one-line files.
/// </summary>
public class PaceFormatterTests
{
    private const string Lf = "\n";
    private const string CrLf = "\r\n";
    private const string LfEscape = "\\n";

    private readonly ResultFormatter _formatter = new();

    private static CommandExecutionResult OkResult(CommandKind kind, string target) =>
        new() { Number = 1, Kind = kind, Target = target, Status = CommandStatus.Ok };

    private static string Sha8(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))[..8];

    [Fact]
    public async Task VerifiedLine_ForLfFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "ka-vf-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "lf.txt"), "alpha" + Lf + "beta" + Lf + "gamma" + Lf);
        var results = new List<CommandExecutionResult> { OkResult(CommandKind.NewFile, "lf.txt") };

        var message = _formatter.Format(1, 1, results, 5, new LimitsOptions(), root);

        message.Should().Contain("verified: path=lf.txt bytes=17 lines=3 sha=" + Sha8(Path.Combine(root, "lf.txt")));
        message.Should().Contain("first=\"alpha" + LfEscape + "beta" + LfEscape + "gamma\"");
    }

    [Fact]
    public async Task VerifiedLine_ForCrlfFile_CountsBytesAndLinesCorrectly()
    {
        var root = Path.Combine(Path.GetTempPath(), "ka-vf-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "crlf.txt"), "one" + CrLf + "two" + CrLf);
        var results = new List<CommandExecutionResult> { OkResult(CommandKind.UpdateFile, "crlf.txt") };

        var message = _formatter.Format(1, 1, results, 5, new LimitsOptions(), root);

        message.Should().Contain("bytes=10", "CRLF counts both bytes");
        message.Should().Contain("lines=2", "CRLF is two lines, not four");
        message.Should().Contain("first=\"one" + LfEscape + "two\"");
    }

    [Fact]
    public async Task VerifiedLine_ForEmptyFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "ka-vf-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "empty.txt"), string.Empty);
        var results = new List<CommandExecutionResult> { OkResult(CommandKind.NewFile, "empty.txt") };

        var message = _formatter.Format(1, 1, results, 5, new LimitsOptions(), root);

        message.Should().Contain("bytes=0 lines=0");
        message.Should().Contain("first=\"\"");
    }

    [Fact]
    public async Task VerifiedLine_ForOneLineFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "ka-vf-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "one.txt"), "only line");
        var results = new List<CommandExecutionResult> { OkResult(CommandKind.NewFile, "one.txt") };

        var message = _formatter.Format(1, 1, results, 5, new LimitsOptions(), root);

        message.Should().Contain("bytes=9 lines=1");
        message.Should().Contain("first=\"only line\" last=\"only line\"");
    }

    [Fact]
    public async Task VerifiedLine_Under300Characters()
    {
        var root = Path.Combine(Path.GetTempPath(), "ka-vf-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "big.txt"), new string('x', 5000));
        var results = new List<CommandExecutionResult> { OkResult(CommandKind.NewFile, "big.txt") };

        var message = _formatter.Format(1, 1, results, 5, new LimitsOptions(), root);

        var line = message.Split('\n').First(l => l.StartsWith("verified:", StringComparison.Ordinal));
        line.Length.Should().BeLessThanOrEqualTo(300);
    }

    [Fact]
    public async Task VerifiedLine_ForMkdir()
    {
        var root = Path.Combine(Path.GetTempPath(), "ka-vf-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "newdir"));
        var results = new List<CommandExecutionResult> { OkResult(CommandKind.Mkdir, "newdir") };

        var message = _formatter.Format(1, 1, results, 5, new LimitsOptions(), root);

        message.Should().Contain("verified: dir exists");
    }
}
