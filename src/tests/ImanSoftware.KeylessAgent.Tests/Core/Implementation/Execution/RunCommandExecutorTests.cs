using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Models.Execution;
using ImanSoftware.KeylessAgent.Abstractions.Models.Parser;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Core.Implementation.Execution;
using ImanSoftware.KeylessAgent.Core.Implementation.Workspace;
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

    private sealed class FakeEnvironmentTools(ToolStatus? status = null) : ImanSoftware.KeylessAgent.Core.Implementation.Workspace.IEnvironmentTools
    {
        public List<string> Invalidated { get; } = [];

        public ToolStatus Status { get; set; } = status
            ?? new ToolStatus("dotnet", ToolStates.Missing, string.Empty, string.Empty,
                DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

        public Task<IReadOnlyDictionary<string, ToolStatus>> GetToolsCachedAsync(
            string workspaceRoot, bool forceRefresh, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, ToolStatus>>(new Dictionary<string, ToolStatus>());

        public Task<ToolStatus> GetToolStatusAsync(string workspaceRoot, string tool, CancellationToken cancellationToken = default)
            => Task.FromResult(Status);

        public Task InvalidateToolAsync(string workspaceRoot, string tool, CancellationToken cancellationToken = default)
        {
            Invalidated.Add(tool);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RunCommand_MissingTool_FailsFastWithoutStartingTheProcess()
    {
        Directory.CreateDirectory(_root);
        var tools = new FakeEnvironmentTools(new ToolStatus(
            "dotnet", ToolStates.Missing, string.Empty, string.Empty,
            DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
        var executor = new RunCommandExecutor(new CommandPolicy(), new CommandRiskClassifier(), _runner, tools);

        var result = await executor.ExecuteAsync(new RunCommandCommand("dotnet build") { Number = 1 }, Context());

        result.Status.Should().Be(CommandStatus.Error);
        result.ErrorCode.Should().Be("KA_TOOL_MISSING");
        result.FailureReason.Should().Be("tool_missing");
        result.ErrorMessage.Should().Contain("'dotnet' is not installed on this machine (see Environment). Do not retry it.");
        _runner.LastFileName.Should().BeNull("the process is never started for a missing tool");
    }

    [Fact]
    public async Task RunCommand_CommandNotFound_InvalidatesTheToolCache()
    {
        Directory.CreateDirectory(_root);
        var tools = new FakeEnvironmentTools(new ToolStatus(
            "dotnet", ToolStates.Available, "10.0.100", "dotnet",
            DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
        _runner.Result = new ProcessResult(9009, string.Empty,
            "'dotnet' is not recognized as an internal or external command", 0.1, TimedOut: false);
        var executor = new RunCommandExecutor(new CommandPolicy(), new CommandRiskClassifier(), _runner, tools);

        var result = await executor.ExecuteAsync(new RunCommandCommand("dotnet build") { Number = 1 }, Context());

        result.FailureReason.Should().Be("command_not_found");
        tools.Invalidated.Should().Equal("dotnet");
    }

    private AgentExecutionContext Context(ApprovalMode mode = ApprovalMode.Auto)
        => new()
        {
            WorkspaceRoot = _root,
            Options = new KeylessOptions { Mode = mode },
            Ui = new ImanSoftware.KeylessAgent.Tests.Fakes.FakeAgentUserInterface(),
        };

    // --- ApprovalMode.Auto: workspace-escape hard deny + in-workspace auto-run ---

    [Fact]
    public async Task AutoMode_AbsolutePathOutsideWorkspace_IsHardDenied_WithNoPromptAndNoExecution()
    {
        Directory.CreateDirectory(_root);
        var executor = CreateExecutor(ApprovalMode.Auto);
        var context = Context(ApprovalMode.Auto);
        var ui = (ImanSoftware.KeylessAgent.Tests.Fakes.FakeAgentUserInterface)context.Ui;

        var result = await executor.ExecuteAsync(
            new RunCommandCommand("type C:\\Windows\\win.ini") { Number = 1 }, context);

        result.Status.Should().Be(CommandStatus.Error, "an escaping command must never execute");
        result.ErrorCode.Should().Be("KA_COMMAND_DENIED");
        result.ErrorMessage.Should().Contain("C:\\Windows\\win.ini").And.Contain("outside the workspace");
        ui.Confirmations.Should().BeEmpty("Auto mode has no confirmation branch — the deny is outright");
        _runner.LastFileName.Should().BeNull("the process is never started");
    }

    [Fact]
    public async Task AutoMode_RelativeDotDotEscape_IsHardDenied_WithNoPromptAndNoExecution()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        var executor = CreateExecutor(ApprovalMode.Auto);
        var context = Context(ApprovalMode.Auto);
        var ui = (ImanSoftware.KeylessAgent.Tests.Fakes.FakeAgentUserInterface)context.Ui;

        var result = await executor.ExecuteAsync(
            new RunCommandCommand("type ..\\..\\secret.txt") { Number = 1 }, context);

        result.Status.Should().Be(CommandStatus.Error);
        result.ErrorCode.Should().Be("KA_COMMAND_DENIED");
        result.ErrorMessage.Should().Contain("outside the workspace");
        ui.Confirmations.Should().BeEmpty("no confirmation prompt in Auto mode");
        _runner.LastFileName.Should().BeNull("the process is never started");
    }

    [Fact]
    public async Task AutoMode_InWorkspaceCommand_StillAutoRunsWithZeroPrompts()
    {
        Directory.CreateDirectory(_root);
        var executor = CreateExecutor(ApprovalMode.Auto);
        var context = Context(ApprovalMode.Auto);
        var ui = (ImanSoftware.KeylessAgent.Tests.Fakes.FakeAgentUserInterface)context.Ui;
        _runner.Result = new ProcessResult(0, "build output", string.Empty, 1.0, TimedOut: false);

        var result = await executor.ExecuteAsync(
            new RunCommandCommand("dotnet build") { Number = 1 }, context);

        result.Status.Should().Be(CommandStatus.Ok,
            "a legitimate in-workspace command must not be affected by the escape deny");
        result.ErrorMessage.Should().BeNull();
        ui.Confirmations.Should().BeEmpty("Auto mode runs in-workspace work with zero prompts");
        _runner.LastFileName.Should().NotBeNull("the command actually executed");
    }

    [Fact]
    public async Task AskMode_WorkspaceEscape_StillRoutesToConfirmation()
    {
        // The intentional Ask/AutoSafe behavior: an escaping command can be knowingly
        // approved by the user — it routes to confirmation, not to the hard deny.
        Directory.CreateDirectory(_root);
        var executor = CreateExecutor(ApprovalMode.Ask);
        var context = Context(ApprovalMode.Ask);
        var ui = (ImanSoftware.KeylessAgent.Tests.Fakes.FakeAgentUserInterface)context.Ui;

        await executor.ExecuteAsync(
            new RunCommandCommand("type C:\\Windows\\win.ini") { Number = 1 }, context);

        ui.Confirmations.Should().ContainSingle(c => c.Title.Contains("Run command"),
            "Ask mode asks, and the user may knowingly approve");
    }

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
