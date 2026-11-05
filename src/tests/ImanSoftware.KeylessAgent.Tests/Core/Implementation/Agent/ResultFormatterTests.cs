using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Models.Execution;
using ImanSoftware.KeylessAgent.Abstractions.Models.Parser;
using ImanSoftware.KeylessAgent.Core.Implementation.Agent;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Agent;

public class ResultFormatterTests
{
    private readonly ResultFormatter _formatter = new();

    private static LimitsOptions Limits() => new();

    [Fact]
    public void Format_MapsEveryResultToItsCommandNumberNameAndTarget()
    {
        var results = new List<CommandExecutionResult>
        {
            CommandExecutionResult.Ok(new NewFileCommand("src/Foo/Bar.cs", "x") { Number = 1 }),
            new CommandExecutionResult
            {
                Number = 2,
                Kind = CommandKind.RunCommand,
                Target = "dotnet build",
                Status = CommandStatus.Ok,
                ExitCode = 0,
                DurationSeconds = 4.2,
                Output = "Build succeeded.",
            },
        };

        var message = _formatter.Format(3, 2, results, batchLimit: 5, Limits(), workspaceRoot: "");
        message.Should().Contain("--- result 1/2 | #1 newfile | src/Foo/Bar.cs ---");
        message.Should().Contain("--- result 2/2 | #2 runcommand | dotnet build ---");
        message.Should().Contain("status: ok");
        message.Should().Contain("exit code: 0 | duration: 4.2s");
        message.Should().Contain("<<<OUTPUT");
        message.Should().Contain("[END RESULTS]");
        message.Should().NotContain("skipped");
    }

    [Fact]
    public void Format_ErrorResult_IncludesReason()
    {
        var results = new List<CommandExecutionResult>
        {
            CommandExecutionResult.Fail(
                new EditFileCommand("src/Foo/Baz.cs", "old", "new") { Number = 1 },
                "KA_FIND_NOT_UNIQUE",
                "FIND text matched 0 times (must match exactly once)"),
        };

        var message = _formatter.Format(1, 1, results, 5, Limits(), workspaceRoot: "");

        message.Should().Contain("status: error");
        message.Should().Contain("reason: FIND text matched 0 times");
    }

    [Fact]
    public void Format_SkippedCommands_AreReportedWithTheBatchLimit()
    {
        var results = new List<CommandExecutionResult>
        {
            CommandExecutionResult.Ok(new SayCommand("working") { Number = 1 }),
            new CommandExecutionResult
            {
                Number = 7,
                Kind = CommandKind.UpdateFile,
                Target = "src/A.cs",
                Status = CommandStatus.Skipped,
                ErrorMessage = "batch limit reached",
            },
        };

        var message = _formatter.Format(1, 1, results, 5, Limits(), workspaceRoot: "");

        message.Should().Contain("--- result 2/2 | #7 updatefile | src/A.cs ---");
        message.Should().Contain("status: skipped");
        message.Should().Contain("--- skipped 1 commands: batch limit is 5 per response, resend them next ---");
    }

    [Fact]
    public void Format_SkipsAfterFailureOrDone_GetTheirOwnReason_NoResendHint()
    {
        var results = new List<CommandExecutionResult>
        {
            CommandExecutionResult.Ok(new SayCommand("working") { Number = 1 }),
            new CommandExecutionResult
            {
                Number = 2,
                Kind = CommandKind.NewFile,
                Target = "x.txt",
                Status = CommandStatus.Skipped,
                ErrorMessage = "after @@done",
            },
            new CommandExecutionResult
            {
                Number = 3,
                Kind = CommandKind.RunCommand,
                Target = "dotnet build",
                Status = CommandStatus.Skipped,
                ErrorMessage = "an earlier command failed",
            },
        };

        var message = _formatter.Format(1, 1, results, 5, Limits(), workspaceRoot: "");

        message.Should().NotContain("resend them next",
            "commands skipped after a failure or @@done must NOT be resent");
        message.Should().Contain("after @@done");
        message.Should().NotContain("batch limit is 5");
    }

    [Fact]
    public void Format_LongOutput_IsCappedHeadAndTail()
    {
        var output = string.Join("\n", Enumerable.Range(1, 2000).Select(i => $"line {i}"));
        var results = new List<CommandExecutionResult>
        {
            new()
            {
                Number = 1,
                Kind = CommandKind.RunCommand,
                Target = "big",
                Status = CommandStatus.Ok,
                Output = output,
            },
        };

        var message = _formatter.Format(1, 1, results, 5, new LimitsOptions { MaxCommandOutputChars = 1000 }, workspaceRoot: "");

        message.Should().Contain("characters truncated ...");
        message.Should().Contain("line 1\n");
        message.Should().Contain("line 2000");
        message.Length.Should().BeLessThan(output.Length);
    }

    [Fact]
    public void Format_ReadFileOutput_IsWrappedInMarkers()
    {
        var results = new List<CommandExecutionResult>
        {
            new()
            {
                Number = 4,
                Kind = CommandKind.ReadFile,
                Target = "src/Foo/Bar.cs",
                Status = CommandStatus.Ok,
                Output = "file content here",
            },
        };

        var message = _formatter.Format(1, 1, results, 5, Limits(), workspaceRoot: "");

        message.Should().Contain("<<<OUTPUT");
        message.Should().Contain("file content here");
        message.Should().Contain("OUTPUT>>>");
    }

    [Fact]
    public void Format_LongTargetIsTruncatedToOneLine()
    {
        var results = new List<CommandExecutionResult>
        {
            CommandExecutionResult.Ok(new SayCommand(new string('x', 500)) { Number = 1 }),
        };

        var message = _formatter.Format(1, 1, results, 5, Limits(), workspaceRoot: "");

        message.Should().Contain("...");
        message.Split('\n').Should().NotContain(line => line.Length > 250);
    }
}
