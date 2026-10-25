using Microsoft.Extensions.Logging;

namespace ImanSoftware.KeylessAgent.Core.Factory;

/// <summary>
/// The minimal ILoggerFactory for a single provider — enough for the CLI's verbose mode,
/// without pulling in the full Microsoft.Extensions.Logging package at the call site.
/// </summary>
internal sealed class SingleProviderLoggerFactory(ILoggerProvider singleProvider) : ILoggerFactory
{
    private bool _disposed;

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => singleProvider.CreateLogger(categoryName);

    /// <inheritdoc />
    public void AddProvider(ILoggerProvider provider)
    {
        // This factory wraps exactly one provider; additional providers are ignored.
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        singleProvider.Dispose();
    }
}
