using FluentAssertions;
using Xunit;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Browser;

public class PlaywrightChatPageTests
{
    // IsSendEnabledAsync is exercised through PlaywrightChatSessionTests (FakeChatPage);
    // the JSON disable/paperclip states are covered by the session-level send tests.
}
