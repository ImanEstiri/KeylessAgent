using System.Text;
using System.Text.Json;
using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Core.Implementation.Configuration;

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

    [Fact]
    public void DefaultKeylessOptions_SerializeMatchesDocumentedJson()
    {
        var doc = ReadSourceFile(Path.Combine("docs", "configuration.md"));
        var documented = ParseDocumentedJsonBlocks(doc);

        var serializer = ConfigurationService.CreateSerializer();
        var serialized = serializer.Serialize(new KeylessOptions());
        using var actual = JsonDocument.Parse(serialized);

        var problems = new List<string>();
        foreach (var block in documented)
            CompareJson(block.RootElement, actual.RootElement, "", problems);

        problems.Should().BeEmpty(
            "docs/configuration.md must document every KeylessOptions property with its real default value, and only real ones: {0}",
            string.Join(" | ", problems));
    }

    /// <summary>Extracts the ```jsonc blocks and strips line comments (string-aware).</summary>
    private static List<JsonDocument> ParseDocumentedJsonBlocks(string markdown)
    {
        var blocks = new List<JsonDocument>();
        var lines = markdown.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].TrimStart().StartsWith("```jsonc", StringComparison.Ordinal))
                continue;

            var body = new StringBuilder();
            i++;
            while (i < lines.Length && !lines[i].TrimStart().StartsWith("```"))
                body.AppendLine(lines[i++]);

            var json = StripLineComments(body.ToString());
            blocks.Add(JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            }));
        }

        return blocks;
    }

    private static string StripLineComments(string json)
    {
        var result = new StringBuilder(json.Length);
        foreach (var line in json.Split('\n'))
        {
            var inString = false;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (c == '"' && (i == 0 || line[i - 1] != '\\'))
                    inString = !inString;
                if (!inString && c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                    break;
                result.Append(c);
            }

            result.Append('\n');
        }

        return result.ToString();
    }

    /// <summary>
    /// Both directions: every documented key must exist with the same value as the serialized
    /// default, and every KeylessOptions property must be documented. Arrays compare
    /// element-wise, numbers numerically (the doc may write 300 where the serializer writes 300).
    /// </summary>
    private static void CompareJson(JsonElement documented, JsonElement actual, string path, List<string> problems)
    {
        if (documented.ValueKind == JsonValueKind.Object && actual.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in documented.EnumerateObject())
            {
                if (!actual.TryGetProperty(prop.Name, out var actualValue))
                {
                    problems.Add($"{path}{prop.Name}: documented, but KeylessOptions has no such property");
                    continue;
                }

                CompareJson(prop.Value, actualValue, $"{path}{prop.Name}.", problems);
            }

            foreach (var prop in actual.EnumerateObject())
            {
                if (!documented.TryGetProperty(prop.Name, out _))
                    problems.Add($"{path}{prop.Name}: KeylessOptions property missing from the documented JSON");
            }

            return;
        }

        if (documented.ValueKind == JsonValueKind.Array && actual.ValueKind == JsonValueKind.Array)
        {
            var documentedItems = documented.EnumerateArray().Select(e => e.ToString()).ToList();
            var actualItems = actual.EnumerateArray().Select(e => e.ToString()).ToList();
            if (!documentedItems.SequenceEqual(actualItems))
                problems.Add($"{path}: documented [{string.Join(", ", documentedItems)}] but the default is [{string.Join(", ", actualItems)}]");
            return;
        }

        if (documented.ValueKind == JsonValueKind.Number && actual.ValueKind == JsonValueKind.Number)
        {
            if (documented.GetDecimal() != actual.GetDecimal())
                problems.Add($"{path}: documented {documented.GetRawText()} but the default is {actual.GetRawText()}");
            return;
        }

        if (!string.Equals(documented.ToString(), actual.ToString(), StringComparison.OrdinalIgnoreCase))
            problems.Add($"{path}: documented {documented.ToString()} but the default is {actual.ToString()}");
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
