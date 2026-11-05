namespace ImanSoftware.KeylessAgent.Wpf.Infrastructure;

/// <summary>
/// Marshals work onto the WPF UI thread. Abstracted so view models stay testable:
/// tests run against a synchronous fake instead of a real WPF dispatcher.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Queues fire-and-forget work on the UI thread; never blocks the caller.</summary>
    void Post(Action action);

    /// <summary>Runs work on the UI thread and completes with its result.</summary>
    /// <typeparam name="T">Result type of the work.</typeparam>
    Task<T> InvokeAsync<T>(Func<T> func);
}
