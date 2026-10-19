using System;
using System.Threading;
using Windows.UI.Core;

namespace Screenbox.Helpers;

/// <summary>Preserves UWP UI continuations when the composition dispatcher queue is not yet available.</summary>
internal sealed class CoreDispatcherContext(CoreDispatcher dispatcher) : SynchronizationContext
{
    /// <inheritdoc />
    public override void Post(SendOrPostCallback callback, object? state)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _ = dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => callback(state));
    }

    /// <inheritdoc />
    public override void Send(SendOrPostCallback callback, object? state)
    {
        throw new NotSupportedException("Synchronous dispatch to the UWP UI thread is not supported.");
    }

    /// <inheritdoc />
    public override SynchronizationContext CreateCopy() => new CoreDispatcherContext(dispatcher);
}
