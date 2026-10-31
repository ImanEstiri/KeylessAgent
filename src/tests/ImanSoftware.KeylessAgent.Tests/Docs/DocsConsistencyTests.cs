using FluentAssertions;

namespace ImanSoftware.KeylessAgent.Tests.Docs;

/// <summary>
/// Docs ↔ code consistency: every KA_ code in ErrorCodes appears in
/// docs/agentscript-spec.md, and every default value of the configuration model appears
/// in docs/configuration.md.
/// </summary>
public class DocsConsistencyTests
{
    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Directory.Packages.props")))
            current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }

    [Fact]
    public void EveryErrorCode_IsDocumentedInAgentscriptSpec()
    {
        var root = FindRepoRoot();
        var spec = File.ReadAllText(Path.Combine(root, "docs", "agentscript-spec.md"));

        var codes = typeof(ImanSoftware.KeylessAgent.Abstractions.Models.ErrorCodes)
            .GetFields()
            .Where(f => f.IsPublic && f.IsStatic && f.FieldType == typeof(string))
            .Select(f => (string)f.GetValue(null)!)
            .Where(code => code.StartsWith("KA_", StringComparison.Ordinal))
            .ToList();

        codes.Should().NotBeEmpty();
        var missing = codes.Where(code => !spec.Contains(code, StringComparison.Ordinal)).ToList();
        missing.Should().BeEmpty("these KA_ codes are missing from docs/agentscript-spec.md: {0}", string.Join(", ", missing));
    }

    [Fact]
    public void EveryConfigDefault_IsDocumentedInConfigurationMd()
    {
        var root = FindRepoRoot();
        var config = File.ReadAllText(Path.Combine(root, "docs", "configuration.md"));

        var browser = new ImanSoftware.KeylessAgent.Abstractions.Models.Configuration.BrowserOptions();
        var limits = new ImanSoftware.KeylessAgent.Abstractions.Models.Configuration.LimitsOptions();
        var git = new ImanSoftware.KeylessAgent.Abstractions.Models.Configuration.GitOptions();
        var commands = new ImanSoftware.KeylessAgent.Abstractions.Models.Configuration.CommandsOptions();

        // (section header present, key name, expected default rendering)
        var expectations = new (string Key, string Default)[]
        {
            ("ChromeDebugPort", browser.ChromeDebugPort.ToString()),
            ("ReplySource", browser.ReplySource.ToString()),
            ("ReplyTimeoutSeconds", browser.ReplyTimeoutSeconds.ToString()),
            ("SendConfirmSeconds", browser.SendConfirmSeconds.ToString()),
            ("UploadWaitSeconds", browser.UploadWaitSeconds.ToString()),
            ("MinDelayMs", browser.MinDelayMs.ToString()),
            ("MaxDelayMs", browser.MaxDelayMs.ToString()),
            ("MaxMessagesPerMinute", browser.MaxMessagesPerMinute.ToString()),
            ("MaxCommandsPerResponse", limits.MaxCommandsPerResponse.ToString()),
            ("MaxResponseChars", limits.MaxResponseChars.ToString()),
            ("MaxFileContentChars", limits.MaxFileContentChars.ToString()),
            ("MaxCommandOutputChars", limits.MaxCommandOutputChars.ToString()),
            ("MaxFormatRetries", limits.MaxFormatRetries.ToString()),
            ("MaxIterationsPerTurn", limits.MaxIterationsPerTurn.ToString()),
            ("AutoInit", git.AutoInit.ToString()),
            ("BranchPrefix", git.BranchPrefix),
            ("TimeoutSeconds", commands.TimeoutSeconds.ToString()),
        };

        var missing = expectations
            .Where(e => !config.Contains("\"" + e.Key + "\"", StringComparison.Ordinal) ||
                        !config.Contains(e.Default, StringComparison.OrdinalIgnoreCase))
            .Select(e => $"{e.Key}={e.Default}")
            .ToList();

        missing.Should().BeEmpty("these key/default pairs are missing from docs/configuration.md: {0}", string.Join(", ", missing));
    }

    [Fact]
    public void DefaultKeylessOptions_SerializeMatchesDocumentedJson()
    {
        // The documented keyless.json block must parse into the default options object.
        var root = FindRepoRoot();
        var config = File.ReadAllText(Path.Combine(root, "docs", "configuration.md"));

        // Extract the first ```jsonc block
        var start = config.IndexOf("```jsonc", StringComparison.Ordinal);
        var end = config.IndexOf("```", start + 8, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "configuration.md must contain a jsonc block");
        var json = config[(start + 8)..end];

        var options = ImanSoftware.Serialization.JsonSerializerFactory
            .CreateSystemTextJsonSerializer(new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            })
            .Deserialize<ImanSoftware.KeylessAgent.Abstractions.Models.Configuration.KeylessOptions>(json);

        options.Should().NotBeNull("the documented block must deserialize");
        var defaults = new ImanSoftware.KeylessAgent.Abstractions.Models.Configuration.KeylessOptions();

        options.Browser.ChromeDebugPort.Should().Be(defaults.Browser.ChromeDebugPort);
        options.Browser.ReplySource.Should().Be(defaults.Browser.ReplySource);
        options.Browser.ReplyTimeoutSeconds.Should().Be(defaults.Browser.ReplyTimeoutSeconds);
        options.Browser.SendConfirmSeconds.Should().Be(defaults.Browser.SendConfirmSeconds);
        options.Browser.UploadWaitSeconds.Should().Be(defaults.Browser.UploadWaitSeconds);
        options.Browser.MaxMessagesPerMinute.Should().Be(defaults.Browser.MaxMessagesPerMinute);
        options.Limits.MaxCommandsPerResponse.Should().Be(defaults.Limits.MaxCommandsPerResponse);
        options.Limits.MaxResponseChars.Should().Be(defaults.Limits.MaxResponseChars);
        options.Limits.MaxFileContentChars.Should().Be(defaults.Limits.MaxFileContentChars);
        options.Limits.MaxCommandOutputChars.Should().Be(defaults.Limits.MaxCommandOutputChars);
        options.Limits.MaxFormatRetries.Should().Be(defaults.Limits.MaxFormatRetries);
        options.Limits.MaxIterationsPerTurn.Should().Be(defaults.Limits.MaxIterationsPerTurn);
        options.Git.AutoInit.Should().Be(defaults.Git.AutoInit);
        options.Git.BranchPrefix.Should().Be(defaults.Git.BranchPrefix);
        options.Commands.TimeoutSeconds.Should().Be(defaults.Commands.TimeoutSeconds);
        options.Mode.Should().Be(defaults.Mode);
    }
}
