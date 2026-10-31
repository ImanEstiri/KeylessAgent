using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Wpf.Infrastructure;
using ImanSoftware.KeylessAgent.Wpf.Services;
using ImanSoftware.KeylessAgent.Wpf.Tests.Fakes;
using ImanSoftware.KeylessAgent.Wpf.ViewModels;
using ImanSoftware.Outcomes;
using Xunit;

namespace ImanSoftware.KeylessAgent.Wpf.Tests.Fakes;

/// <summary>Fake IDialogService: returns a configured confirm answer and counts calls.</summary>
public sealed class FakeDialogService : IDialogService
{
    public bool ConfirmResult = true;
    public int ConfirmCallCount;

    public Task<bool> ConfirmAsync(ConfirmRequest request)
    {
        ConfirmCallCount++;
        return Task.FromResult(ConfirmResult);
    }

    public Task<string> AskAsync(string question) => Task.FromResult(string.Empty);

    public Task<ChoiceResult> ChooseAsync(ChoiceRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(ChoiceResult.CancelledMenu());

    public void CancelAll()
    {
    }
}
