using ImanSoftware.KeylessAgent.Core.Implementation.Browser;
using ImanSoftware.KeylessAgent.Core.Implementation.Parsing;

namespace ImanSoftware.KeylessAgent.Playground;

/// <summary>
/// Manual-testing harness: runs one scripted agent conversation locally with the fake
/// chat provider — no browser, no DeepSeek, no git changes. Prints every step.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.WriteLine("KeylessAgent Playground — fake chat provider harness");
        Console.WriteLine(new string('─', 60));

        var request = args.Length > 0 ? string.Join(" ", args) : "Add a README section about running tests.";
        var scriptedReply = """
            @@say
            <<<MSG
            I will add the section now.
            MSG>>>
            @@end
            @@newfile docs/running-tests.md
            <<<CONTENT
            # Running tests

            dotnet test
            CONTENT>>>
            @@end
            @@done
            <<<MSG
            Added docs/running-tests.md.
            MSG>>>
            @@end
            @@eof
            """;

        // --- Step 1: fake chat session ---
        var provider = new FakeChatProvider(scriptedReply);
        var sessionOutcome = await provider.StartSessionAsync(new Abstractions.Models.Chat.ChatSessionOptions
        {
            WorkspaceRoot = Directory.GetCurrentDirectory(),
        });
        if (sessionOutcome.IsFailure)
        {
            Console.WriteLine($"Failed to start fake session: {sessionOutcome.Error.Message}");
            return 1;
        }

        var session = sessionOutcome.Data;
        Console.WriteLine($"Chat URL: {session.ChatUrl}");

        // --- Step 2: send the request (with no attachments) ---
        var send = await session.SendMessageAsync($"[USER REQUEST] {request}", []);
        Console.WriteLine(send.IsSuccess ? "Message sent." : $"Send failed: {send.Error.Message}");

        // --- Step 3: wait for the reply and parse it ---
        var replyOutcome = await session.WaitForReplyAsync();
        if (replyOutcome.IsFailure)
        {
            Console.WriteLine($"No reply: {replyOutcome.Error.Message}");
            return 1;
        }

        var reply = replyOutcome.Data;
        Console.WriteLine("Reply received:");
        Console.WriteLine(reply);
        Console.WriteLine(new string('─', 60));

        var guard = new ResponseFormatGuard();
        var verdict = guard.Check(reply, new Abstractions.Models.Configuration.LimitsOptions());
        Console.WriteLine(verdict.Data.IsValid
            ? $"Valid AgentScript: {verdict.Data.Script!.Commands.Count} command(s)."
            : string.Join(Environment.NewLine, verdict.Data.Problems.Select(p => $"  - {p.ToDisplayString()}")));

        return verdict.Data.IsValid ? 0 : 1;
    }
}
