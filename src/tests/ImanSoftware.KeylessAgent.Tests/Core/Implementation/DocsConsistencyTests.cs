using FluentAssertions;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation;

/// <summary>
/// The prompt resources teach the model the command grammar; when a command is added to the
/// parser, these files must list it too — otherwise the model is told its valid commands are
/// invalid. The resources are embedded from source, so the tests read the source tree.
/// </summary>
public class DocsConsistencyTests
{
    [Fact]
    public void SystemPrompt_ListsMovefileAndNext()
    {
        var prompt = ReadResource("system-prompt.md");

        prompt.Should().Contain("@@movefile",
            "the parser accepts @@movefile; omitting it from the keyword list makes the model avoid (or invent around) a valid command");
        prompt.Should().Contain("@@next",
            "the parser accepts @@next as a control directive; omitting it makes the model think only @@done ends a batch");
    }

    [Fact]
    public void FormatCorrection_ListsMovefileAndNext()
    {
        var correction = ReadResource("format-correction.txt");

        correction.Should().Contain("@@movefile",
            "the format-correction template must list every keyword the parser accepts");
        correction.Should().Contain("@@next",
            "the format-correction template must list every keyword the parser accepts");
    }

    [Fact]
    public void ConfigurationDoc_ListsTheConfigurableKeys()
    {
        var doc = ReadSourceFile("docs/configuration.md");

        foreach (var key in new[]
                 {
                     "LoginProbeSeconds", "WindowMode", "MinimizeAfterLogin", "EnableDeepThink",
                     "EnableSearch", "ReplySource", "RefreshAfterEachBatch", "MaxTurnsPerChat",
                     "BatchFailureMode", "AutoApprove", "Allowlist", "TimeoutSeconds",
                 })
        {
            doc.Should().Contain(key, $"the configuration doc must document the {key} key");
        }
    }

    [Fact]
    public void AgentScriptDoc_ListsTheRecoverableErrorCodes()
    {
        var doc = ReadSourceFile("docs/agentscript-spec.md");

        foreach (var code in new[]
                 {
                     "KA_COMMAND_NOT_FOUND", "KA_COMMAND_DECLINED", "KA_COMMAND_DENIED",
                     "KA_PARSE_BAD_PATH", "KA_CHAT_EXHAUSTED", "KA_REPLY_TIMEOUT",
                     "KA_SEND_NOT_CONFIRMED", "KA_SESSION_NOT_FOUND",
                 })
        {
            doc.Should().Contain(code, $"the AgentScript spec must document {code} so the model can react");
        }
    }

    [Fact]
    public void Readme_ListsTheCliOptionsAndInteractiveCommands()
    {
        var readme = ReadSourceFile("README.md");

        foreach (var token in new[]
                 {
                     "--dry-run", "--verbose", "--timing", "keyless doctor", "keyless undo",
                     "keyless chrome", "/workspace", "/newchat", "/help", "Ctrl+C",
                 })
        {
            readme.Should().Contain(token, $"the README must document {token}");
        }
    }

    [Fact]
    public void CliReadme_ListsTheSlashCommandsAndCtrlCBehavior()
    {
        var readme = ReadSourceFile(Path.Combine("src", "UI", "ImanSoftware.KeylessAgent.Cli", "README.md"));

        readme.Should().Contain("/newchat");
        readme.Should().Contain("/workspace");
        readme.Should().Contain("Ctrl+C");
    }

    private static string ReadSourceFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && directory is not null; i++, directory = directory.Parent)
        {
            var path = Path.Combine(directory!.FullName, relativePath);
            if (File.Exists(path))
                return File.ReadAllText(path);
        }

        throw new FileNotFoundException($"Could not locate {relativePath} above {AppContext.BaseDirectory}.");
    }

    private static string ReadResource(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && directory is not null; i++, directory = directory.Parent)
        {
            var path = Path.Combine(directory!.FullName,
                "src", "Core", "ImanSoftware.KeylessAgent.Core", "Resources", name);
            if (File.Exists(path))
                return File.ReadAllText(path);
        }

        throw new FileNotFoundException(
            $"Could not locate src/Core/ImanSoftware.KeylessAgent.Core/Resources/{name} above {AppContext.BaseDirectory}.");
    }
}
