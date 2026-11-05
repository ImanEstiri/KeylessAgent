using System.IO;
using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models.Chat;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Core.Implementation.Browser;
using ImanSoftware.KeylessAgent.Core.Implementation.Agent;
using ImanSoftware.KeylessAgent.Tests.Fakes;
using Microsoft.Playwright;
using NSubstitute;
using Xunit;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Browser;

/// <summary>
/// The conversation-URL contract: only https://chat.deepseek.com/a/chat/s/&lt;id&gt; is a
/// conversation (the base URL never persists), ids drive tab selection (never a prefix
/// match, which hijacked other workspaces' conversations), and the selection rule never
/// touches a tab showing a DIFFERENT conversation.
/// </summary>
public class ChatUrlTests
{
    private const string Conversation = "https://chat.deepseek.com/a/chat/s/abc123XYZ_-";
    private const string Base = "https://chat.deepseek.com/";

    // --- helper ---

    [Theory]
    [InlineData("https://chat.deepseek.com/a/chat/s/abc123", true)]
    [InlineData("https://chat.deepseek.com/a/chat/s/AbC_-9", true)]
    [InlineData("https://chat.deepseek.com/", false)]
    [InlineData("https://chat.deepseek.com/a/chat/s/", false)]
    [InlineData("https://chat.deepseek.com/a/chat", false)]
    [InlineData("https://chat.deepseek.com/a/chat/s/abc123?x=1", false)]
    [InlineData("https://example.com/a/chat/s/abc123", false)]
    [InlineData("", false)]
    public void IsConversationUrl_MatchesOnlyConversationUrls(string url, bool expected)
        => ChatUrl.IsConversationUrl(url).Should().Be(expected);

    [Fact]
    public void ConversationId_ExtractsTheId_AndNullForNonConversations()
    {
        ChatUrl.ConversationId(Conversation).Should().Be("abc123XYZ_-");
        ChatUrl.ConversationId(Base).Should().BeNull();
        ChatUrl.ConversationId(null).Should().BeNull();
    }

    [Fact]
    public void SameConversation_ComparesIds_NotPrefixes()
    {
        ChatUrl.SameConversation(Conversation, Conversation).Should().BeTrue();
        ChatUrl.SameConversation(Conversation, "https://chat.deepseek.com/a/chat/s/other").Should().BeFalse();
        ChatUrl.SameConversation(Base, Base).Should().BeFalse("the base is not a conversation");
        ChatUrl.SameConversation(null, null).Should().BeFalse();
    }

    [Fact]
    public void ForPersistence_NeverPersistsTheBaseOrGarbage()
    {
        ChatUrl.ForPersistence(Conversation).Should().Be(Conversation);
        ChatUrl.ForPersistence(Base).Should().BeEmpty();
        ChatUrl.ForPersistence("https://chat.deepseek.com/a/fake").Should().BeEmpty();
        ChatUrl.ForPersistence(null).Should().BeEmpty();
    }

    // --- tab selection (pure core of OpenChatTabAsync) ---

    private static IPage PageWith(string url)
    {
        var page = Substitute.For<IPage>();
        page.Url.Returns(url);
        return page;
    }

    [Fact]
    public void SelectChatTab_ReusesTheTabOfTheSameConversation()
    {
        var own = PageWith(Conversation);
        var other = PageWith("https://chat.deepseek.com/a/chat/s/someone-else");
        var baseTab = PageWith(Base);

        var (page, action) = ChatUrl.SelectChatTab([other, own, baseTab], Conversation);

        action.Should().Be(ChatUrl.TabSelectionAction.ReuseSameConversation);
        page.Should().BeSameAs(own, "the SAVED conversation's tab is reused, not anybody else's");
    }

    [Fact]
    public void SelectChatTab_NavigatesAnEmptyBaseTab_NeverAnotherConversation()
    {
        var other = PageWith("https://chat.deepseek.com/a/chat/s/someone-else");
        var baseTab = PageWith(Base);

        var (page, action) = ChatUrl.SelectChatTab([other, baseTab], Conversation);

        action.Should().Be(ChatUrl.TabSelectionAction.NavigateExistingBaseTab);
        page.Should().BeSameAs(baseTab, "only the EMPTY base tab is navigated to the saved conversation");
    }

    [Fact]
    public void SelectChatTab_OpensANewTab_WhenOnlyOtherConversationsExist()
    {
        var other = PageWith("https://chat.deepseek.com/a/chat/s/someone-else");

        var (page, action) = ChatUrl.SelectChatTab([other], Conversation);

        action.Should().Be(ChatUrl.TabSelectionAction.OpenNewTab);
        page.Should().BeNull();
    }

    [Fact]
    public void SelectChatTab_WithoutASavedConversation_AlwaysOpensANewTab()
    {
        var deepseek = PageWith(Base);
        var conversation = PageWith("https://chat.deepseek.com/a/chat/s/someone-else");

        foreach (var resume in new[] { null, Base })
        {
            var (page, action) = ChatUrl.SelectChatTab([deepseek, conversation], resume);
            action.Should().Be(ChatUrl.TabSelectionAction.OpenNewTab,
                "without a saved conversation, reusing any tab would attach this workspace to somebody's chat");
            page.Should().BeNull();
        }
    }

    // --- AgentLoop: invariant + resume-failure path ---

    private static string DoneReply(string message) =>
        "@@say\n<<<MSG\nworking\nMSG>>>\n@@end\n@@done\n<<<MSG\n" + message + "\nMSG>>>\n@@end\n@@eof\n";

    [Fact]
    public async Task RunAsync_SystemPromptResent_WhenSavedChatUrlIsNotAConversation()
    {
        // A pre-fix session.json: SystemPromptSent=true with the BASE url persisted - an
        // inconsistent state; the invariant forces the system prompt to be resent.
        var root = Path.Combine(Path.GetTempPath(), "ka-chaturl-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".agent"));
        try
        {
            var chat = new FakeChatProvider(DoneReply("first turn done"));
        var loop = new AgentLoop(chat);
        var ui = new FakeAgentUserInterface();

        await loop.RunAsync(root, "first request", ui); // persists fake-session-1 + prompt sent

            // Simulate the legacy state: same session, ChatUrl rewritten to the base.
            var sessionPath = Path.Combine(root, ".agent", "session.json");
            var json = (await ImanSoftware.FileStorage.ImanFileStorageFactory.CreateStorage().ReadAllTextAsync(sessionPath)).Data
                .Replace("/a/chat/s/fake-session-1", "").Replace("https://chat.deepseek.com", Base.TrimEnd('/'));
            await ImanSoftware.FileStorage.ImanFileStorageFactory.CreateStorage().WriteAllTextAsync(sessionPath, json);

            var chat2 = new FakeChatProvider(DoneReply("second turn done"));
            var loop2 = new AgentLoop(chat2) { ResumeProbeSeconds = 1 };

            await loop2.RunAsync(root, "second request", ui);

            chat2.Sent.Should().HaveCount(2, "the new chat gets the system prompt AND the request");
            chat2.Sent[0].Text.Should().NotContain("second request",
                "message 1 of a fresh chat is the AgentScript rules alone");
            chat2.Sent[1].Text.Should().Contain("second request");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (UnauthorizedAccessException) { /* git objects are read-only; best-effort */ }
        }
    }

    [Fact]
    public async Task RunAsync_ResumeFailure_LogsChatResumeFailed_AndStartsANewChat()
    {
        var root = Path.Combine(Path.GetTempPath(), "ka-chaturl-resume-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var chat = new FakeChatProvider(DoneReply("first turn done"));
            var loop = new AgentLoop(chat);
            await loop.RunAsync(root, "first request", new FakeAgentUserInterface());

            var sessionPath = Path.Combine(root, ".agent", "session.json");
            var storage = ImanSoftware.FileStorage.ImanFileStorageFactory.CreateStorage();
            var saved = (await storage.ReadAllTextAsync(sessionPath)).Data;
            saved.Should().Contain("fake-session-1", "the first turn persists the conversation URL");

            // Simulate a workspace that MOVED to another conversation id (or a hand-edited
            // session): the saved id no longer matches what the provider opens.
            await storage.WriteAllTextAsync(sessionPath, saved!.Replace("fake-session-1", "fake-session-99"));

            // The conversation is GONE (fake ignores the resume URL): resume must fail.
            var chat2 = new FakeChatProvider(DoneReply("second turn done")) { HonorResumeUrl = false };
            var loop2 = new AgentLoop(chat2) { ResumeProbeSeconds = 1 };

            (await loop2.RunAsync(root, "second request", new FakeAgentUserInterface())).ErrorMessage.Should().BeNull();

            var turnLog = Directory.GetFiles(Path.Combine(root, ".agent", "logs"), "turn-2.jsonl");
            turnLog.Should().NotBeEmpty("the failure is a TURN event");
            (await storage.ReadAllTextAsync(turnLog[0]))
                .Data.Should().Contain("chat-resume-failed");

            var session = (await storage.ReadAllTextAsync(sessionPath)).Data;
            session.Should().Contain("\"SystemPromptSent\": true", "the fresh chat received the prompt");
            session.Should().Contain("\"TurnsInChat\": 1", "the resume failure reset the per-chat counter");
            session.Should().Contain("fake-session-", "a conversation URL is persisted (the fresh fake chat)");
            chat2.Sent[0].Text.Should().NotContain("second request",
                "the system prompt is resent to the fresh chat");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (UnauthorizedAccessException) { /* git objects are read-only; best-effort */ }
        }
    }
}
