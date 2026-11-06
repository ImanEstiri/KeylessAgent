using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Core.Implementation.Browser;
using ImanSoftware.KeylessAgent.Wpf.Infrastructure;
using ImanSoftware.KeylessAgent.Wpf.Services;
using ImanSoftware.KeylessAgent.Wpf.Tests.Fakes;
using ImanSoftware.KeylessAgent.Wpf.ViewModels;
using ImanSoftware.Outcomes;
using Xunit;

namespace ImanSoftware.KeylessAgent.Wpf.Tests.Fakes;

/// <summary>Fake IBackupService counting undo calls; never touches the file system.</summary>
public sealed class FakeBackupService : IBackupService
{
    public bool Fail;
    public int CallCount;
    public string? LastWorkspace;
    public IReadOnlyList<string> Restored = ["src/a.cs", "src/b.cs"];

    public Task<Outcome> BackupAsync(string workspaceRoot, string fullPath)
        => Task.FromResult(Outcome.Successful());

    public Task<Outcome<IReadOnlyList<string>>> UndoLatestAsync(string workspaceRoot)
    {
        CallCount++;
        LastWorkspace = workspaceRoot;
        return Task.FromResult(Fail
            ? Outcome.Failure<IReadOnlyList<string>>(new OutcomeError(
                ErrorCodes.FileNotFound, ErrorCodes.FileNotFound, OutcomeErrorType.Failure))
            : Outcome.Successful(Restored));
    }
}

/// <summary>Fake IConfigurationService returning a fixed result; never reads keyless.json.</summary>
public sealed class FakeConfigurationService : IConfigurationService
{
    private readonly bool _fail;

    public FakeConfigurationService(KeylessOptions? options = null, bool fail = false)
    {
        Options = options ?? new KeylessOptions();
        _fail = fail;
    }

    public KeylessOptions Options { get; }

    public bool LastLoadMigratedSchema => false;

    public Task<Outcome<KeylessOptions>> LoadAsync(string workspaceRoot)
        => Task.FromResult(_fail
            ? Outcome.Failure<KeylessOptions>(new OutcomeError(
                ErrorCodes.ConfigurationInvalid, ErrorCodes.ConfigurationInvalid, OutcomeErrorType.Failure))
            : Outcome.Successful(Options));
}

/// <summary>Fake IBrowserConnector; never launches a real Chrome.</summary>
/// <summary>Records Show/Hide calls of the Chrome window controller.</summary>
public sealed class FakeChromeWindowController : IChromeWindowController
{
    public int ShowCalls;
    public int HideCalls;
    public int LastPeekSeconds = -1;
    public bool Fail;

    /// <summary>When true, ShowAsync blocks until ReleaseShow() — for the busy-state tests.</summary>
    public bool SlowShow;
    private TaskCompletionSource? _showGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ReleaseShow() => _showGate?.TrySetResult();

    public async Task<Outcome> ShowAsync(BrowserOptions options, int peekSeconds, CancellationToken ct = default)
    {
        ShowCalls++;
        LastPeekSeconds = peekSeconds;
        if (SlowShow && _showGate is not null)
            await _showGate.Task;
        return Fail
            ? Outcome.Failure(new OutcomeError("show failed", "KA_BROWSER_CONNECTION_FAILED", OutcomeErrorType.Failure))
            : Outcome.Successful();
    }

    public Task<Outcome> HideAsync(BrowserOptions options, CancellationToken ct = default)
    {
        HideCalls++;
        return Task.FromResult(Fail
            ? Outcome.Failure(new OutcomeError("hide failed", "KA_BROWSER_CONNECTION_FAILED", OutcomeErrorType.Failure))
            : Outcome.Successful());
    }

    public int CloseCalls;

    public ChromeProfileState? State { get; set; }

    public Task<ChromeProfileState> GetStateAsync(BrowserOptions options, CancellationToken ct = default)
        => Task.FromResult(State ?? new ChromeProfileState(DebugPortOpen: false, OwnedByAgent: false, ProcessCount: 0));

    public Task<Outcome> CloseAsync(BrowserOptions options, bool allowAttached = false, CancellationToken ct = default)
    {
        CloseCalls++;
        if (!Fail)
            State = new ChromeProfileState(DebugPortOpen: false, OwnedByAgent: false, ProcessCount: 0); // a real close stops Chrome
        return Task.FromResult(Fail
            ? Outcome.Failure(new OutcomeError("close failed", "KA_BROWSER_CONNECTION_FAILED", OutcomeErrorType.Failure))
            : Outcome.Successful());
    }

}

public sealed class FakeBrowserConnector : IBrowserConnector
{
    public BrowserConnectionKind Kind = BrowserConnectionKind.AutoLaunchedChrome;
    public bool Fail;
    public Exception? Thrown;
    public int CallCount;
    public BrowserOptions? LastOptions;

    public Task<Outcome<(BrowserAttachment Attachment, object Page)>> ConnectAsync(
        BrowserOptions options, IAgentUserInterface ui, CancellationToken cancellationToken = default, ITimingRecorder? timings = null, string? resumeChatUrl = null)
    {
        CallCount++;
        LastOptions = options;

        if (Thrown is not null)
            return Task.FromException<Outcome<(BrowserAttachment Attachment, object Page)>>(Thrown);

        if (Fail)
            return Task.FromResult(Outcome.Failure<(BrowserAttachment Attachment, object Page)>(new OutcomeError(
                ErrorCodes.BrowserConnectionFailed, ErrorCodes.BrowserConnectionFailed, OutcomeErrorType.Failure)));

        var attachment = new BrowserAttachment { Kind = Kind, UserDataDir = @"C:\profile", LaunchedProcess = true };
        return Task.FromResult(Outcome.Successful((attachment, new object())));
    }
}
