using System.Windows.Threading;

namespace ImanSoftware.KeylessAgent.Wpf.Infrastructure;

/// <summary>
/// Production <see cref="IUiDispatcher"/> backed by the WPF <see cref="Dispatcher"/> of the thread
/// that created it (the UI thread, when constructed at application start-up).
/// </summary>
public sealed class WpfDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    /// <summary>
    /// Creates a dispatcher bound to the calling thread's WPF dispatcher. Construct on the UI thread.
    /// </summary>
    public WpfDispatcher()
        : this(Dispatcher.CurrentDispatcher)
    {
    }

    /// <summary>
    /// Creates a dispatcher bound to an explicit WPF dispatcher; used by tests.
    /// </summary>
    internal WpfDispatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <inheritdoc />
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _ = _dispatcher.BeginInvoke(action);
    }

    /// <inheritdoc />
    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        return _dispatcher.InvokeAsync(func).Task;
    }
}
