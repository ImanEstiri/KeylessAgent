using System.IO;
using FluentAssertions;
using ImanSoftware.KeylessAgent.Wpf.Infrastructure;
using Xunit;

namespace ImanSoftware.KeylessAgent.Wpf.Tests.Infrastructure;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _folder;
    private readonly SettingsService _service;

    public SettingsServiceTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "keyless-wpf-settings-" + Guid.NewGuid().ToString("N"));
        _service = new SettingsService(_folder);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void DefaultFolder_PointsAtAppDataKeylessAgent()
    {
        var service = new SettingsService();

        service.SettingsFilePath.Should().EndWith(Path.Combine("KeylessAgent", "wpf-settings.json"));
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var settings = _service.Load();

        settings.Should().BeEquivalentTo(new WpfSettings());
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefaults()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(_service.SettingsFilePath, "{ this is not json");

        var settings = _service.Load();

        settings.Should().BeEquivalentTo(new WpfSettings());
    }

    [Fact]
    public void Save_CreatesFolderAndWritesTheFile()
    {
        _service.Save(new WpfSettings { LastWorkspace = @"G:\demo", ReduceEffects = true });

        File.Exists(_service.SettingsFilePath).Should().BeTrue();
        File.ReadAllText(_service.SettingsFilePath).Should().Contain("\"LastWorkspace\"");
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllValues()
    {
        var original = new WpfSettings
        {
            LastWorkspace = @"G:\projects\demo",
            RecentWorkspaces = [@"G:\projects\demo", @"C:\other"],
            ReduceEffects = true,
            BackdropPreference = "Acrylic",
            DryRunDefault = true,
            Window = new WindowSettings { Left = 10, Top = 20, Width = 1200, Height = 800, Maximized = true },
        };

        _service.Save(original);

        _service.Load().Should().BeEquivalentTo(original);
    }

    [Fact]
    public void WithRecentWorkspace_AddsAtFront_AndSetsLastWorkspace()
    {
        var updated = SettingsService.WithRecentWorkspace(new WpfSettings(), @"G:\demo");

        updated.LastWorkspace.Should().Be(@"G:\demo");
        updated.RecentWorkspaces.Should().Equal(@"G:\demo");
    }

    [Fact]
    public void WithRecentWorkspace_MovesExistingEntryToFront()
    {
        var settings = new WpfSettings { RecentWorkspaces = [@"C:\a", @"C:\b"] };

        var updated = SettingsService.WithRecentWorkspace(settings, @"C:\a");

        updated.RecentWorkspaces.Should().Equal(@"C:\a", @"C:\b");
    }

    [Fact]
    public void WithRecentWorkspace_TruncatesToTenEntries()
    {
        var settings = new WpfSettings();
        for (var i = 0; i < SettingsService.MaxRecentWorkspaces + 2; i++)
            settings = SettingsService.WithRecentWorkspace(settings, @"C:\ws" + i);

        settings.RecentWorkspaces.Should().HaveCount(SettingsService.MaxRecentWorkspaces);
        settings.RecentWorkspaces[0].Should().Be(@"C:\ws11");
        settings.RecentWorkspaces[^1].Should().Be(@"C:\ws2");
    }

    [Fact]
    public void WithRecentWorkspace_BlankPath_IsIgnored()
    {
        var settings = new WpfSettings { LastWorkspace = @"C:\keep", RecentWorkspaces = [@"C:\keep"] };

        var updated = SettingsService.WithRecentWorkspace(settings, "   ");

        updated.Should().BeSameAs(settings);
    }
}
