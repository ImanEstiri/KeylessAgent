using ImanSoftware.KeylessAgent.Abstractions.Models.Chat;
using ImanSoftware.KeylessAgent.Abstractions.Models.Snapshot;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Browser;

/// <summary>
/// A scripted chat provider for tests, the Playground, and dry runs:
/// replies come from a queue set by the caller; everything the agent sends is recorded.
/// </summary>
internal sealed class FakeChatProvider : IChatProvider
{
    private readonly Queue<string> _replies;

    public FakeChatProvider(params string[] replies)
    {
        _replies = new Queue<string>(replies);
    }

    /// <summary>
    /// Replies left in the queue.
    /// </summary>
    public int RemainingReplies => _replies.Count;

    /// <summary>
    /// How many chat sessions this provider has started; each session gets a distinct ChatUrl.
    /// </summary>
    public int StartCount { get; private set; }

    /// <summary>
    /// Messages sent per session, indexed by session number minus one.
    /// </summary>
    public List<int> MessagesPerSession { get; } = [];

    /// <summary>
    /// Everything sent to the fake chat, in order.
    /// </summary>
    public List<SentMessage> Sent { get; } = [];

    /// <summary>
    /// When false, the provider IGNORES ResumeChatUrl and always starts a fresh fake
    /// conversation - simulating a dead/deleted conversation for the resume-failure path.
    /// Default true (a resume lands on the SAME conversation, like the real provider).
    /// </summary>
    public bool HonorResumeUrl { get; set; } = true;

    public Task<Outcome<IChatSession>> StartSessionAsync(ChatSessionOptions options, CancellationToken cancellationToken = default)
    {
        StartCount++;
        MessagesPerSession.Add(0);

        // Conversation-SHAPED URLs (/a/chat/s/<id>): the loop's ChatUrl invariant only
        // persists and resumes conversation URLs. A resume lands on the SAME conversation
        // (like the real provider); a fresh chat gets a new id.
        var url = HonorResumeUrl && ChatUrl.IsConversationUrl(options.ResumeChatUrl)
            ? options.ResumeChatUrl!
            : $"https://chat.deepseek.com/a/chat/s/fake-session-{StartCount}";
        IChatSession session = new FakeChatSession(this, StartCount, url);
        return Task.FromResult(Outcome.Successful(session));
    }

    internal sealed record SentMessage(string Text, IReadOnlyList<string> AttachmentNames);

    private sealed class FakeChatSession(FakeChatProvider owner, int sessionIndex, string url) : IChatSession
    {
        public string ChatUrl => url;

        public string LastReplyOrigin => "Dom";

        public Task<Outcome> SendMessageAsync(string message, IReadOnlyList<SnapshotAttachment> attachments, CancellationToken cancellationToken = default)
        {
            owner.MessagesPerSession[sessionIndex - 1]++;
            owner.Sent.Add(new SentMessage(message, attachments.Select(a => a.Name).ToList()));
            return Task.FromResult(Outcome.Successful());
        }

        public Task<Outcome<string>> WaitForReplyAsync(CancellationToken cancellationToken = default)
        {
            if (owner._replies.Count == 0)
            {
                return Task.FromResult(Outcome.Failure<string>(new OutcomeError(
                    "The fake chat has no more scripted replies.", "KA_REPLY_TIMEOUT", OutcomeErrorType.Failure)));
            }

            return Task.FromResult(Outcome.Successful(owner._replies.Dequeue()));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
