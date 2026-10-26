using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.Outcomes;
using ImanSoftware.KeylessAgent.Core.Implementation.Browser;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Browser;

/// <summary>
/// Doctor report classification with a scripted inspector: MATCHED / NOT FOUND /
/// NOT APPLICABLE NOW, the required-set exit rule, and snapshot capture.
/// </summary>
public class ChatDiagnosticsServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ka-doctor-" + Guid.CreateVersion7().ToString("N"));

    public ChatDiagnosticsServiceTests()
    {
        Directory.CreateDirectory(_root);
    }

    private static SelectorsCatalog Catalog() => RealCatalog;

    private static FakeInspector FullyWorking()
    {
        var inspector = new FakeInspector();
        inspector.Matched.AddRange(["InputBox", "SendButton", "FileInput", "NewChatButton",
            "MessageItems", "AssistantMessage", "FinalAnswer", "DeepThinkToggle", "SearchToggle"]);
        return inspector;
    }

    private sealed class FakeInspector : IChatPageInspector
    {
        public List<string> Matched { get; } = [];

        public string? ToggleState { get; set; } = "true";

        public bool InputEditable { get; set; } = true;

        public string? CapturedHtml { get; set; } = "<div class='composer'><textarea placeholder='Message DeepSeek'></textarea></div>";

        public string Url => "https://chat.deepseek.com/a/chat/s/test";

        public (int Index, int Count)? Probe(string[] selectors)
        {
            // The catalog's first fallback per key identifies the key uniquely in tests.
            var key = KeyFor(selectors);
            if (key is null)
                return null;

            return Matched.Contains(key) ? (0, 3) : null;
        }

        public string? GetToggleState(string[] selectors) => ToggleState;

        public bool IsInputEditable() => InputEditable;

        public string? CaptureTrimmed(string[] selectors)
        {
            var key = KeyFor(selectors);
            return key is null || !Matched.Contains(key) ? null : CapturedHtml;
        }

        /// <summary>Maps the first fallback selector back to its catalog key (test bookkeeping).</summary>
        private static string? KeyFor(string[] selectors)
        {
            var catalog = RealCatalog;
            foreach (var (key, list) in ChatDiagnosticsService.SelectorEntries(catalog))
            {
                if (list.Length > 0 && list[0] == selectors[0])
                    return key;
            }

            return null;
        }
    }

    private static readonly SelectorsCatalog RealCatalog = SelectorsCatalog.Load(
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ka-doctor-real-" + Guid.CreateVersion7().ToString("N"))).FullName,
        "selectors.json").Data!;

    [Fact]
    public async Task AllRequiredMatched_ProducesExitOkReport()
    {
        var inspector = FullyWorking();
        IChatPageInspector asInterface = inspector;
        var service = new ChatDiagnosticsService(_ => Task.FromResult(
            Outcome.Successful((asInterface, (Func<ValueTask>)(() => ValueTask.CompletedTask)))));

        var report = await service.InspectAsync(_root, new BrowserOptions(), null, watch: false);

        report.IsSuccess.Should().BeTrue();
        report.Data.AllRequiredMatched.Should().BeTrue();
        report.Data.LoggedIn.Should().BeTrue();
        report.Data.InputAcceptsText.Should().BeTrue();
        report.Data.DeepThinkPressed.Should().Be("true");
        report.Data.Checks.First(c => c.Key == "StopIndicator").Status.Should().Be(SelectorCheckStatus.NotApplicableNow);
        report.Data.Checks.First(c => c.Key == "AttachmentChips").Status.Should().Be(SelectorCheckStatus.NotApplicableNow);
    }

    [Fact]
    public async Task MissingRequiredSelector_MarksReportAsFailing()
    {
        var inspector = FullyWorking();
        inspector.Matched.Remove("SendButton");
        IChatPageInspector asInterface = inspector;
        var service = new ChatDiagnosticsService(_ => Task.FromResult(
            Outcome.Successful((asInterface, (Func<ValueTask>)(() => ValueTask.CompletedTask)))));

        var report = await service.InspectAsync(_root, new BrowserOptions(), null, watch: false);

        report.Data.AllRequiredMatched.Should().BeFalse();
        var send = report.Data.Checks.First(c => c.Key == "SendButton");
        send.Status.Should().Be(SelectorCheckStatus.NotFound);
        send.FallbackIndex.Should().Be(-1);
    }

    [Fact]
    public async Task LoginIndicatorMatched_MeansNotLoggedIn()
    {
        var inspector = FullyWorking();
        inspector.Matched.Add("LoginIndicator");
        IChatPageInspector asInterface = inspector;
        var service = new ChatDiagnosticsService(_ => Task.FromResult(
            Outcome.Successful((asInterface, (Func<ValueTask>)(() => ValueTask.CompletedTask)))));

        var report = await service.InspectAsync(_root, new BrowserOptions(), null, watch: false);

        report.Data.LoggedIn.Should().BeFalse();
        report.Data.Checks.First(c => c.Key == "LoginIndicator").Status.Should().Be(SelectorCheckStatus.Matched);
    }

    [Fact]
    public async Task Capture_WritesTrimmedSnapshots()
    {
        var capture = Path.Combine(_root, "capture");
        var inspector = FullyWorking();
        IChatPageInspector asInterface = inspector;
        var service = new ChatDiagnosticsService(_ => Task.FromResult(
            Outcome.Successful((asInterface, (Func<ValueTask>)(() => ValueTask.CompletedTask)))));

        var report = await service.InspectAsync(_root, new BrowserOptions(), capture, watch: false);

        report.Data.CapturedFiles.Should().NotBeEmpty();
        File.Exists(Path.Combine(capture, "composer.html")).Should().BeTrue();
        var composer = File.ReadAllText(Path.Combine(capture, "composer.html"));
        composer.Should().NotContain("<svg");
        composer.Should().Contain("textarea");
    }

    [Fact]
    public void HtmlTrimmer_RemovesScriptsStylesSvg_AndLongText()
    {
        var html = "<div><script>alert(1)</script><style>.x{}</style><svg><path/></svg>" +
                   "<p>short ok</p><span>" + new string('x', 200) + "</span></div>";

        var trimmed = HtmlTrimmer.Trim(html);

        trimmed.Should().Contain("<script removed>");
        trimmed.Should().Contain("<style removed>");
        trimmed.Should().Contain("<svg/>");
        trimmed.Should().Contain("short ok");
        trimmed.Should().NotContain(new string('x', 200));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
