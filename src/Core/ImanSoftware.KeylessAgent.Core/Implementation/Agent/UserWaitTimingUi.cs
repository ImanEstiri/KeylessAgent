using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Core.Implementation.Workspace;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Agent;

/// <summary>
/// Wrings the user-interaction time out of the other phases: every approval prompt, menu
/// and @@ask question is wrapped in a "user-wait" timing entry, so the summary can show
/// the turn with and without time spent waiting for the user. Everything else delegates.
/// </summary>
internal sealed class UserWaitTimingUi : IAgentUserInterface
{
    private readonly IAgentUserInterface _inner;
    private readonly ITimingRecorder _timings;

    public UserWaitTimingUi(IAgentUserInterface inner, ITimingRecorder timings)
    {
        _inner = inner;
        _timings = timings;
    }

    public void Say(string message) => _inner.Say(message);

    public void ShowProgress(AgentProgressEvent progressEvent) => _inner.ShowProgress(progressEvent);

    public Task<bool> ConfirmAsync(ConfirmRequest request)
        => TimedAsync(async () => await _inner.ConfirmAsync(request));

    public Task<string> AskAsync(string question)
        => TimedAsync(async () => await _inner.AskAsync(question));

    public Task<ChoiceResult> ChooseAsync(ChoiceRequest request, CancellationToken cancellationToken = default)
        => TimedAsync(async () => await _inner.ChooseAsync(request, cancellationToken));

    public IDisposable BeginWait(string message) => _inner.BeginWait(message);

    public void ShowDiff(FileChange change) => _inner.ShowDiff(change);

    private async Task<T> TimedAsync<T>(Func<Task<T>> call)
    {
        _timings.Start(TimingRecorder.UserWaitPhase);
        try
        {
            return await call();
        }
        finally
        {
            _timings.Stop(TimingRecorder.UserWaitPhase);
        }
    }
}

