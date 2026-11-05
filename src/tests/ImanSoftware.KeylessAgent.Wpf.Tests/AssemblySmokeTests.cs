using FluentAssertions;
using ImanSoftware.KeylessAgent.Wpf;
using Xunit;

namespace ImanSoftware.KeylessAgent.Wpf.Tests;

/// <summary>
/// Phase 1 smoke tests: the WPF assembly builds, loads and carries the expected identity.
/// Later phases add real ViewModel and service tests.
/// </summary>
public class AssemblySmokeTests
{
    [Fact]
    public void WpfAssembly_Loads_AndCarriesTheExpectedName()
    {
        typeof(App).Assembly.GetName().Name.Should().Be("ImanSoftware.KeylessAgent.Wpf");
    }
}
