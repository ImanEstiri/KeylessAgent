using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Abstractions.Services;

namespace ImanSoftware.KeylessAgent.Tests.Fakes;

/// <summary>
/// Records everything the agent shows/asks so tests can assert on it.
/// </summary>
internal sealed class FakeAgentUserInterface : IAgentUserInterface
{
    public List<string> Said { get; } = [];

    public List<AgentProgressEvent> Progress { get; } = [];

    public List<ConfirmRequest> Confirmations { get; } = [];

    public List<string> Questions { get; } = [];

    public List<FileChange> Diffs { get; } = [];

    public bool ConfirmAnswer { get; set; } = true;

    public string AskAnswer { get; set; } = "fake answer";

    public void Say(string message) => Said.Add(message);

    public void ShowProgress(AgentProgressEvent progressEvent) => Progress.Add(progressEvent);

    public Task<bool> ConfirmAsync(ConfirmRequest request)
    {
        Confirmations.Add(request);
        return Task.FromResult(ConfirmAnswer);
    }

    public Task<string> AskAsync(string question)
    {
        Questions.Add(question);
        return Task.FromResult(AskAnswer);
    }

    public void ShowDiff(FileChange change) => Diffs.Add(change);
}
