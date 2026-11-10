
















using FluentAssertions;
using ImanSoftware.KeylessAgent.Core.Implementation.Browser;
using ImanSoftware.KeylessAgent.Core.Implementation.Configuration;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Browser;

public class InterActionDelaysConfigTests
{
    private static async Task<(string Root, ConfigurationService Service)> WorkspaceWith(string json)
    {
        var root = Path.Combine(Path.GetTempPath(), "ka-interaction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "keyless.json"), json);
        return (root, new ConfigurationService());
    }

    [Fact]
    public async Task PartialBlock_KeepsOtherDefaults()
    {
        var json = "{ \"SchemaVersion\": 2, \"Browser\": { \"InterActionDelays\": { \"AfterPasteSeconds\": 3 } } }";
        var (root, service) = await WorkspaceWith(json);
        try
        {
            var outcome = await service.LoadAsync(root);
            outcome.IsSuccess.Should().BeTrue();
            var delays = outcome.Data.Browser.InterActionDelays;
            delays.AfterExecutionSeconds.Should().Be(2);
            delays.AfterUploadSeconds.Should().Be(2);
            delays.AfterPasteSeconds.Should().Be(3);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
