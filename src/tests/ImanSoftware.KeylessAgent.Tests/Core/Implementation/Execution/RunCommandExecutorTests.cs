using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Models.Execution;
using ImanSoftware.KeylessAgent.Abstractions.Models.Parser;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Core.Implementation.Execution;
using ImanSoftware.KeylessAgent.Tests.Fakes;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Execution;

/// <summary>
/// Every command line runs through the platform shell: cmd.exe /d /s /c on Windows — so
/// built-ins (mkdir, dir, …) and shell operators (&&, >) work as the model wrote them.
/// The classifier and policy still inspect the WHOLE line before anything runs.
/// </summary>
public class RunCommandExecutorTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ka-runexe-" + Guid.CreateVersion7().ToString("N"));
    private readonly FakeProcessRunner _runner = new();

    private RunCommandExecutor CreateExecutor(ApprovalMode mode = ApprovalMode.Auto)
        => new(
            new CommandPolicy(),
            new CommandRiskClassifier(),
            _runner);

    private AgentExecutionContext Context(ApprovalMode mode = ApprovalMode.Auto)
        => new()
        {
            WorkspaceRoot = _root,
            Options = new KeylessOptions { Mode = mode },
            Ui = new ImanSoftware.KeylessAgent.Tests.Fakes.FakeAgentUserInterface(),
        };

    [Fact]
    public async Task ShellWrapsTheLine_InCmdOnWindows()
    {
        Directory.CreateDirectory(_root);

        var result = await CreateExecutor().ExecuteAsync(
            new RunCommandCommand("echo hello") { Number = 1 }, Context());

        result.Status.Should().Be(CommandStatus.Ok);
        _runner.LastFileName.Should().Be("cmd.exe");
        _runner.LastArguments.Should().Be("/d /s /c \"echo hello\"");
        _runner.LastWorkingDirectory.Should().Be(_root);
    }

    [Fact]
    public async Task ShellWrapsTheLine_NonWindows_UsesSh()
    {
        Directory.CreateDirectory(_root);

        var invocation = RunCommandExecutor.ShellInvocationFor("echo hello", isWindows: false);

        invocation.FileName.Should().Be("/bin/sh");
        invocation.Arguments.Should().Be("-c \"echo hello\"");
    }

    [Fact]
    public async Task MissingProgram_ReportsNotAvailable()
    {
        Directory.CreateDirectory(_root);
        _runner.Result = new ProcessResult(9009, string.Empty,
            "'ka-no-such-cmd-xyz' is not recognized as an internal or external command", 0.2, TimedOut: false);

        var result = await CreateExecutor().ExecuteAsync(
            new RunCommandCommand("ka-no-such-cmd-xyz") { Number = 1 }, Context());

        result.Status.Should().Be(CommandStatus.Error);
        result.ErrorCode.Should().Be(ErrorCodes.CommandNotFound);
        result.FailureReason.Should().Be("command_not_found");
        result.ErrorMessage.Should().Contain("'ka-no-such-cmd-xyz' is not available");
        result.ErrorMessage.Should().Contain("shell built-ins work");
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public string? LastFileName { get; private set; }
        public string? LastArguments { get; private set; }
        public string? LastWorkingDirectory { get; private set; }

        public ProcessResult Result { get; set; } =
            new(0, "ok", string.Empty, 0.1, TimedOut: false);

        public Task<ProcessResult> RunAsync(
            string fileName, string arguments, string workingDirectory,
            int timeoutSeconds, CancellationToken cancellationToken = default)
        {
            LastFileName = fileName;
            LastArguments = arguments;
            LastWorkingDirectory = workingDirectory;
            return Task.FromResult(Result);
        }
    }
}
