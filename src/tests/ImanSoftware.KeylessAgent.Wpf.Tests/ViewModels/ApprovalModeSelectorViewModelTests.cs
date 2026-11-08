using System.IO;
using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Wpf.Infrastructure;
using ImanSoftware.KeylessAgent.Wpf.Services;
using ImanSoftware.KeylessAgent.Wpf.Infrastructure;
using ImanSoftware.KeylessAgent.Wpf.Tests.Fakes;
using ImanSoftware.KeylessAgent.Wpf.ViewModels;
using ImanSoftware.KeylessAgent.Wpf.ViewModels.Feed;
using Xunit;

namespace ImanSoftware.KeylessAgent.Wpf.Tests.ViewModels;

/// <summary>
/// The approval-mode dropdown: per-workspace persistence through keyless.json, one-time
/// Full-access confirmation, and the three plain-language options.
/// </summary>
public class ApprovalModeSelectorViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ka-approval-" + Guid.CreateVersion7().ToString("N"));
    private readonly ImmediateDispatcher _dispatcher = new();
    private readonly FakeConfigurationService _configuration = new();
    private readonly AgentViewModel _agent;
    private int _confirmCalls;
    private bool _confirmAnswer = true;

    public ApprovalModeSelectorViewModelTests()
    {
        Directory.CreateDirectory(_root);
        _agent = new AgentViewModel(
            new AgentRunner(_dispatcher, new ActivityFeed(),
                () => throw new NotSupportedException("turns are not part of these tests"),
                (_, _) => throw new NotSupportedException("turns are not part of these tests")),
            _dispatcher, new ActivityFeed());
        _agent.WorkspaceRoot = _root;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private Task<bool> RecordingConfirmFullAccess()
    {
        _confirmCalls++;
        return Task.FromResult(_confirmAnswer);
    }

    private ApprovalModeSelectorViewModel Create() => new(_configuration, _agent, _dispatcher, confirmFullAccess: RecordingConfirmFullAccess);

    [Fact]
    public async Task Reload_LoadsTheWorkspacesSavedMode()
    {
        await _configuration.SaveApprovalModeAsync(_root, ApprovalMode.AutoSafe);
        var viewModel = Create();

        await viewModel.ReloadAsync();

        viewModel.SelectedMode.Should().Be(ApprovalMode.AutoSafe);
        viewModel.SelectedOption.Label.Should().Be("Auto-run safe commands");
    }

    [Fact]
    public async Task SelectingAutoSafe_PersistsIntoKeylessJson()
    {
        var viewModel = Create();

        viewModel.SelectedOption = viewModel.Options.First(o => o.Mode == ApprovalMode.AutoSafe);
        await Task.Yield(); // the setter kicks off the async persistence

        _configuration.SavedApprovalMode.Should().Be(ApprovalMode.AutoSafe);
        var onDisk = await _configuration.LoadAsync(_root);
        onDisk.Data.Mode.Should().Be(ApprovalMode.AutoSafe);
    }

    [Fact]
    public async Task SelectingFullAccess_ShowsTheModal_EveryTime_ThenApplies()
    {
        var viewModel = Create();

        viewModel.SelectedOption = viewModel.Options.First(o => o.Mode == ApprovalMode.Auto);
        await Task.Yield();

        _confirmCalls.Should().Be(1, "the Full-access modal is shown before applying");
        _configuration.SavedApprovalMode.Should().Be(ApprovalMode.Auto);

        // The modal is shown on EVERY Full-access selection, not once per session.
        viewModel.SelectedOption = viewModel.Options.First(o => o.Mode == ApprovalMode.Ask);
        await Task.Yield();
        viewModel.SelectedOption = viewModel.Options.First(o => o.Mode == ApprovalMode.Auto);
        await Task.Yield();

        _confirmCalls.Should().Be(2, "each Full-access selection goes through the modal");
        _configuration.SavedApprovalMode.Should().Be(ApprovalMode.Auto);
    }

    [Fact]
    public async Task DecliningTheFullAccessWarning_RevertsWithoutPersisting()
    {
        var viewModel = Create();
        viewModel.SelectedOption = viewModel.Options.First(o => o.Mode == ApprovalMode.Ask);
        await Task.Yield();

        _confirmAnswer = false;
        viewModel.SelectedOption = viewModel.Options.First(o => o.Mode == ApprovalMode.Auto);
        await Task.Yield();

        _configuration.SavedApprovalMode.Should().BeNull("a declined warning never persists anything");
        viewModel.SelectedMode.Should().Be(ApprovalMode.Ask, "the selection reverts visually");
    }

    [Fact]
    public void Options_CarryPlainLanguageLabels_AndADistinctDangerFlagForFullAccess()
    {
        var viewModel = Create();

        viewModel.Options.Should().HaveCount(3);
        viewModel.Options[0].Label.Should().Be("Ask before every command");
        viewModel.Options[1].Label.Should().Be("Auto-run safe commands");
        viewModel.Options[2].Label.Should().Be("Full access (workspace only)");
        viewModel.Options[2].IsDangerous.Should().BeTrue("Full access is the least safe of the three");
        viewModel.Options.Take(2).Should().OnlyContain(o => !o.IsDangerous);
        viewModel.Options.Should().OnlyContain(o => o.Tooltip.Length > 20, "each option explains itself");
    }

    [Fact]
    public async Task Reload_IsPerWorkspace_SwitchingWorkspacesSwitchesModes()
    {
        var rootB = Path.Combine(Path.GetTempPath(), "ka-approval-b-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(rootB);
        try
        {
            await _configuration.SaveApprovalModeAsync(_root, ApprovalMode.AutoSafe);
            await _configuration.SaveApprovalModeAsync(rootB, ApprovalMode.Auto);

            var viewModel = Create();
            await viewModel.ReloadAsync();
            viewModel.SelectedMode.Should().Be(ApprovalMode.AutoSafe, "workspace A's own mode");

            _agent.WorkspaceRoot = rootB;
            await viewModel.ReloadAsync();
            viewModel.SelectedMode.Should().Be(ApprovalMode.Auto, "workspace B keeps ITS OWN mode");
        }
        finally
        {
            try { Directory.Delete(rootB, recursive: true); } catch { }
        }
    }
}
