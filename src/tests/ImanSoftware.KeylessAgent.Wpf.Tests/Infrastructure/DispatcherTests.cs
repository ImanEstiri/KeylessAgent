using System.Windows.Threading;
using FluentAssertions;
using ImanSoftware.KeylessAgent.Wpf.Infrastructure;
using ImanSoftware.KeylessAgent.Wpf.Tests.Fakes;
using Xunit;

namespace ImanSoftware.KeylessAgent.Wpf.Tests.Infrastructure;

public sealed class DispatcherTests
{
    [Fact]
    public void ImmediateDispatcher_Post_RunsInline()
    {
        var ran = false;

        new ImmediateDispatcher().Post(() => ran = true);

        ran.Should().BeTrue();
    }

    [Fact]
    public async Task ImmediateDispatcher_InvokeAsync_CompletesSynchronously()
    {
        var task = new ImmediateDispatcher().InvokeAsync(() => 42);

        task.IsCompleted.Should().BeTrue();
        (await task).Should().Be(42);
    }

    [Fact]
    public void ImmediateDispatcher_Post_NullAction_Throws()
        => FluentActions.Invoking(() => new ImmediateDispatcher().Post(null!)).Should().Throw<ArgumentNullException>();

    [Fact]
    public async Task WpfDispatcher_MarshalsWorkOntoItsOwnThread()
    {
        var ready = new TaskCompletionSource();
        Dispatcher? captured = null;
        var thread = new Thread(() =>
        {
            captured = Dispatcher.CurrentDispatcher;
            ready.SetResult();
            Dispatcher.Run();
        });
        thread.Start();

        try
        {
            await ready.Task;
            var dispatcher = new WpfDispatcher(captured!);

            var invokeThreadId = await dispatcher.InvokeAsync(() => Thread.CurrentThread.ManagedThreadId);
            invokeThreadId.Should().Be(thread.ManagedThreadId);

            var postedThreadId = new TaskCompletionSource<int>();
            dispatcher.Post(() => postedThreadId.SetResult(Thread.CurrentThread.ManagedThreadId));
            (await postedThreadId.Task).Should().Be(thread.ManagedThreadId);
        }
        finally
        {
            captured?.BeginInvokeShutdown(DispatcherPriority.Normal);
            thread.Join(2000);
        }
    }

    [Fact]
    public void WpfDispatcher_NullDispatcher_Throws()
        => FluentActions.Invoking(() => new WpfDispatcher(null!)).Should().Throw<ArgumentNullException>();
}
