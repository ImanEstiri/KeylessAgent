using Microsoft.Playwright;
using System.Text.RegularExpressions;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Browser;

/// <summary>
/// The ONE place that knows what a DeepSeek CONVERSATION URL is:
/// <c>https://chat.deepseek.com/a/chat/s/&lt;id&gt;</c>. The base URL
/// (<c>https://chat.deepseek.com/</c>) is NOT a conversation — persisting it as a session's
/// ChatUrl made every workspace share the first DeepSeek tab it found and broke resume.
/// Everything that persists, compares or selects chat URLs goes through here.
/// </summary>
internal static partial class ChatUrl
{
    [GeneratedRegex(@"^https://chat\.deepseek\.com/a/chat/s/[\w-]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConversationRegex();

    /// <summary>Whether the URL identifies ONE specific DeepSeek conversation.</summary>
    public static bool IsConversationUrl(string? url)
        => !string.IsNullOrWhiteSpace(url) && ConversationRegex().IsMatch(url);

    /// <summary>The conversation id (<c>/a/chat/s/&lt;id&gt;</c>), or null for non-conversation URLs.</summary>
    public static string? ConversationId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !IsConversationUrl(url))
            return null;

        return url["https://chat.deepseek.com/a/chat/s/".Length..];
    }

    /// <summary>Whether two URLs point at the SAME conversation (ids compared Ordinal).</summary>
    public static bool SameConversation(string? a, string? b)
    {
        var idA = ConversationId(a);
        return idA is not null && idA == ConversationId(b);
    }

    /// <summary>
    /// What may be persisted as a session's ChatUrl: a conversation URL passes through;
    /// anything else (the base URL, a new-chat URL, garbage) becomes EMPTY — never the base.
    /// </summary>
    public static string ForPersistence(string? url)
        => IsConversationUrl(url) ? url! : string.Empty;

    /// <summary>What OpenChatTabAsync should do with the open tabs for one session.</summary>
    public enum TabSelectionAction
    {
        /// <summary>A tab already shows the SAVED conversation — reuse it as-is.</summary>
        ReuseSameConversation,

        /// <summary>A tab sits on the base/new-chat page — navigate it to the saved conversation.</summary>
        NavigateExistingBaseTab,

        /// <summary>No usable tab exists — open a new tab (at the saved conversation URL, or the base URL when there is none).</summary>
        OpenNewTab,
    }

    /// <summary>
    /// The tab-selection decision for one session, pure over the open tabs' URLs so it is
    /// unit-testable without Playwright:
    /// - with a saved conversation: prefer the tab showing the SAME conversation; else a tab
    ///   on the base/new-chat page gets NAVIGATED to it; else a new tab opens.
    /// - NEVER hijack a tab sitting on a DIFFERENT conversation (that conversation belongs to
    ///   another workspace/session).
    /// - without a saved conversation: ALWAYS open a new tab at the base URL (reusing any
    ///   deepseek tab would attach this workspace to somebody's conversation).
    /// </summary>
    public static (IPage? Page, TabSelectionAction Action) SelectChatTab(IReadOnlyList<IPage> pages, string? resumeChatUrl)
    {
        var resumeId = ConversationId(resumeChatUrl);
        if (resumeId is not null)
        {
            foreach (var page in pages)
            {
                if (ConversationId(page.Url) == resumeId)
                    return (page, TabSelectionAction.ReuseSameConversation);
            }

            foreach (var page in pages)
            {
                if (page.Url.StartsWith("https://chat.deepseek.com/", StringComparison.OrdinalIgnoreCase)
                    && !IsConversationUrl(page.Url))
                {
                    return (page, TabSelectionAction.NavigateExistingBaseTab);
                }
            }
        }

        return (null, TabSelectionAction.OpenNewTab);
    }
}
