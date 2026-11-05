using FluentAssertions;
using ImanSoftware.KeylessAgent.Wpf.Mvvm;
using Xunit;

namespace ImanSoftware.KeylessAgent.Wpf.Tests.Mvvm;

public sealed class ObservableObjectTests
{
    private sealed class Sample : ObservableObject
    {
        private string _name = string.Empty;

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }
    }

    [Fact]
    public void SetProperty_SetsValue_AndRaisesChangeWithMemberName()
    {
        var sample = new Sample();
        var raised = new List<string?>();
        sample.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        sample.Name = "agent";

        sample.Name.Should().Be("agent");
        raised.Should().ContainSingle().Which.Should().Be(nameof(Sample.Name));
    }

    [Fact]
    public void SetProperty_SameValue_DoesNotRaiseChange()
    {
        var sample = new Sample { Name = "agent" };
        var raised = new List<string?>();
        sample.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        sample.Name = "agent";

        sample.Name.Should().Be("agent");
        raised.Should().BeEmpty();
    }
}
