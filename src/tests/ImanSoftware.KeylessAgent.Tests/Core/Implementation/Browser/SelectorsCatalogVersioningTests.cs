using System.IO;
using FluentAssertions;
using ImanSoftware.KeylessAgent.Core.Implementation.Browser;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Browser;

/// <summary>
/// The selectors.json versioning: a file written by an OLDER release (no/older CatalogVersion
/// and old shipped selector values) is merged up to the current embedded defaults on load —
/// never-customized keys refreshed, genuine user customizations preserved, original backed up.
/// This is what carries improved MessageCopyButton selectors into existing workspaces.
/// </summary>
public class SelectorsCatalogVersioningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ka-selectors-" + Guid.CreateVersion7().ToString("N"));
    private readonly CapturingLoggerFactory _loggerFactory = new();

    public SelectorsCatalogVersioningTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private string ConfigPath => Path.Combine(_root, "selectors.json");
    private string BackupPath => Path.Combine(_root, "selectors.json.v0.bak");

    [Fact]
    public async Task LoadAsync_OldVersionFile_RefreshesStaleCopyButton_PreservesCustomizedInputBox()
    {
        // A pre-versioning workspace file: no CatalogVersion, the OLD shipped MessageCopyButton
        // value, and a hand-edited InputBox that matches NO shipped version ever.
        File.WriteAllText(ConfigPath, """
            {
              "InputBox": [
                "textarea.my-own-input",
                "textarea#my-custom-input"
              ],
              "SendButton": [
                "div:has(> input[type=\"file\"]) [role=\"button\"].ds-button--filled",
                "[role=\"button\"].ds-button--filled.ds-button--circle",
                "button[type=\"submit\"]"
              ],
              "FinalAnswer": [
                ".ds-markdown.ds-assistant-message-main-content"
              ],
              "MessageCopyButton": [
                ".ds-message [role=\"button\"][aria-label=\"Copy\"]",
                ".ds-message .ds-flex [role=\"button\"]:first-child",
                ".ds-message [role=\"button\"]:has-text(\"Copy\")"
              ]
            }
            """);
        var oldContent = File.ReadAllText(ConfigPath);

        var outcome = await SelectorsCatalog.LoadAsync(_root, "selectors.json", CancellationToken.None, loggerFactory: _loggerFactory);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Data.CatalogVersion.Should().Be(SelectorsCatalog.EmbeddedCatalogVersion, "the merged file is born at the current version");

        // The stale copy-button selectors (matching an OLD shipped value) are refreshed...
        var embedded = await SelectorsCatalog.LoadAsync(
            Path.Combine(Path.GetTempPath(), "ka-selectors-fresh-" + Guid.CreateVersion7().ToString("N")),
            "selectors.json", CancellationToken.None);
        outcome.Data.MessageCopyButton.Should().Equal(embedded.Data.MessageCopyButton,
            "the never-customized MessageCopyButton key picks up the improved embedded selectors");
        outcome.Data.SendButton.Should().Equal(embedded.Data.SendButton,
            "the OLD shipped SendButton value is recognized as never-customized and refreshed too");

        // ...while the hand-edited InputBox survives untouched.
        outcome.Data.InputBox.Should().Equal("textarea.my-own-input", "textarea#my-custom-input");

        File.Exists(BackupPath).Should().BeTrue("the stale file is backed up before the merge");
        File.ReadAllText(BackupPath).Should().Contain("my-own-input", "the backup holds the ORIGINAL file");

        var onDisk = File.ReadAllText(ConfigPath);
        onDisk.Should().Contain("\"CatalogVersion\": 2");
        onDisk.Should().Contain("my-own-input", "the customization is written back");

        _loggerFactory.Lines.Should().Contain(l =>
            l.Contains("MessageCopyButton") && l.Contains("refreshed"),
            "the log names the refreshed keys");
    }

    [Fact]
    public async Task LoadAsync_CurrentVersionFile_IsLeftUntouched()
    {
        var first = await SelectorsCatalog.LoadAsync(_root, "selectors.json", CancellationToken.None);
        first.IsSuccess.Should().BeTrue();
        var written = File.ReadAllText(ConfigPath);
        File.SetLastWriteTimeUtc(ConfigPath, DateTime.UtcNow.AddMinutes(-1));

        var second = await SelectorsCatalog.LoadAsync(_root, "selectors.json", CancellationToken.None, loggerFactory: _loggerFactory);

        second.IsSuccess.Should().BeTrue();
        second.Data.CatalogVersion.Should().Be(SelectorsCatalog.EmbeddedCatalogVersion);
        File.ReadAllText(ConfigPath).Should().Be(written, "a current file is byte-identical after the load");
        File.Exists(BackupPath).Should().BeFalse("no backup without a merge");
        _loggerFactory.Lines.Should().BeEmpty("nothing to log");
    }

    [Fact]
    public async Task MergeAsync_ForcedRefresh_MergesEvenAtCurrentVersion()
    {
        var first = await SelectorsCatalog.LoadAsync(_root, "selectors.json", CancellationToken.None);

        var forced = await SelectorsCatalog.MergeAsync(_root, "selectors.json", forceRefresh: true, _loggerFactory);

        forced.IsSuccess.Should().BeTrue();
        forced.Data.RefreshedKeys.Should().BeEmpty("all keys are already current — the merge is a no-op");
        File.Exists(Path.Combine(_root, "selectors.json.v2.bak")).Should().BeTrue(
            "the forced refresh still backs up the file first");
        forced.Data.Catalog.MessageCopyButton.Should().Equal(first.Data.MessageCopyButton);
    }

    [Fact]
    public async Task MergeAsync_MissingKeys_AreAddedFromTheEmbeddedDefault()
    {
        // A hand-trimmed file that only carries three keys (as if an old release wrote fewer).
        File.WriteAllText(ConfigPath, """
            {
              "InputBox": ["textarea#chat-input"],
              "SendButton": ["button[type=\"submit\"]"],
              "FinalAnswer": [".ds-markdown.ds-assistant-message-main-content"]
            }
            """);

        var merged = await SelectorsCatalog.MergeAsync(_root, "selectors.json", forceRefresh: false, null);

        merged.IsSuccess.Should().BeTrue();
        merged.Data.RefreshedKeys.Should().Contain("MessageCopyButton").And.Contain("StopIndicator")
            .And.Contain("AttachmentChips", "keys a later release added are picked up");
        merged.Data.Catalog.InputBox.Should().Equal(new[] { "textarea#chat-input" },
            "the hand-written InputBox matches no shipped version — preserved");
    }

    /// <summary>ILoggerFactory that keeps every formatted log line for assertions.</summary>
    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public List<string> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Lines);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => lines.Add(formatter(state, exception));
        }
    }
}
