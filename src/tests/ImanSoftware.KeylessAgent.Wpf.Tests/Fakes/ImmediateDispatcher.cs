using ImanSoftware.KeylessAgent.Wpf.Infrastructure;

namespace ImanSoftware.KeylessAgent.Wpf.Tests.Fakes;

/// <summary>
/// A dispatcher fake that runs everything synchronously on the calling thread, so view models
/// can be tested without a WPF Application or a message pump.
/// </summary>
public sealed class ImmediateDispatcher : IUiDispatcher
{
    /// <inheritdoc />
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }

    /// <inheritdoc />
    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        return Task.FromResult(func());
    }
}
