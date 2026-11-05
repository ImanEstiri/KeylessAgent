using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Wpf.Infrastructure;
using ImanSoftware.KeylessAgent.Wpf.ViewModels;

namespace ImanSoftware.KeylessAgent.Wpf.Services;

/// <summary>
/// The queued modal dialog service. A <c>SemaphoreSlim(1,1)</c> serializes dialogs; every
/// request creates its completion source up front so <see cref="CancelAll"/> can complete even
/// requests that are still queued behind another dialog. The gate is always released in
/// <c>finally</c>, the host is always hidden after a dialog ends.
/// </summary>
public sealed class DialogService : IDialogService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DialogHostViewModel _host;
    private readonly IUiDispatcher _ui;
    private readonly object _pendingLock = new();
    private readonly List<PendingDialog> _pending = [];

    /// <summary>Creates the dialog service.</summary>
    /// <param name="host">The shell's dialog host view model (overlay visibility).</param>
    /// <param name="ui">Dispatcher used to show/hide the overlay on the UI thread.</param>
    public DialogService(DialogHostViewModel host, IUiDispatcher ui)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
    }

    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(ConfirmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tcs = NewSource<bool>();
        var pending = Register(() => tcs.TrySetResult(false));

        await _gate.WaitAsync();
        try
        {
            if (!tcs.Task.IsCompleted)
            {
                var viewModel = new ViewModels.Dialogs.ConfirmDialogViewModel(
                    request, approved => Complete(pending, () => tcs.TrySetResult(approved)));
                Show(viewModel);
            }

            return await tcs.Task;
        }
        finally
        {
            HideAndRelease();
        }
    }

    /// <inheritdoc />
    public async Task<string> AskAsync(string question)
    {
        var tcs = NewSource<string>();
        var pending = Register(() => tcs.TrySetResult(string.Empty));

        await _gate.WaitAsync();
        try
        {
            if (!tcs.Task.IsCompleted)
            {
                var viewModel = new ViewModels.Dialogs.AskDialogViewModel(
                    question, answer => Complete(pending, () => tcs.TrySetResult(answer)));
                Show(viewModel);
            }

            return await tcs.Task;
        }
        finally
        {
            HideAndRelease();
        }
    }

    /// <inheritdoc />
    public async Task<ChoiceResult> ChooseAsync(ChoiceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tcs = NewSource<ChoiceResult>();
        var pending = Register(() => tcs.TrySetResult(ChoiceResult.CancelledMenu()));
        using var registration = cancellationToken.Register(
            () => Complete(pending, () => tcs.TrySetResult(ChoiceResult.CancelledMenu())));

        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (!tcs.Task.IsCompleted)
            {
                var viewModel = new ViewModels.Dialogs.ChoiceDialogViewModel(
                    request, result => Complete(pending, () => tcs.TrySetResult(result)));
                Show(viewModel);
            }

            return await tcs.Task;
        }
        finally
        {
            HideAndRelease();
        }
    }

    /// <inheritdoc />
    public void CancelAll()
    {
        List<PendingDialog> snapshot;
        lock (_pendingLock)
        {
            snapshot = [.. _pending];
            _pending.Clear();
        }

        if (snapshot.Count == 0)
            return;

        foreach (var pending in snapshot)
            pending.OnCancel();

        Hide();
    }

    private static TaskCompletionSource<T> NewSource<T>()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private PendingDialog Register(Action onCancel)
    {
        var pending = new PendingDialog(onCancel);
        lock (_pendingLock)
            _pending.Add(pending);
        return pending;
    }

    private void Complete(PendingDialog pending, Action setResult)
    {
        setResult();
        Unregister(pending);
    }

    private void Unregister(PendingDialog pending)
    {
        lock (_pendingLock)
            _ = _pending.Remove(pending);
    }

    private void Show(object viewModel) => _ui.Post(() => _host.Show(viewModel));

    private void Hide() => _ui.Post(_host.Hide);

    private void HideAndRelease()
    {
        Hide();
        _gate.Release();
    }

    /// <summary>Releases the internal gate semaphore. Only call when no dialog requests are in flight.</summary>
    public void Dispose() => _gate.Dispose();

    private sealed class PendingDialog(Action onCancel)
    {
        public Action OnCancel { get; } = onCancel;
    }
}
