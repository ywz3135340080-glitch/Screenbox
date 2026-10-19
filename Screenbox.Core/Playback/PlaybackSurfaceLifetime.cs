using System;
using System.Threading.Tasks;

namespace Screenbox.Core.Playback;

/// <summary>Releases a presentation player only after its UI surface has detached.</summary>
public static class PlaybackSurfaceLifetime
{
    /// <summary>
    /// Serializes retirement with an earlier transition, waits for the UI to release its reference,
    /// and then disposes playback resources away from the UI thread.
    /// </summary>
    public static async Task ReleaseAsync(Task previous, Func<Task> detach, Action release)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(detach);
        ArgumentNullException.ThrowIfNull(release);
        await previous.ConfigureAwait(false);
        await detach().ConfigureAwait(false);
        await Task.Run(release).ConfigureAwait(false);
    }
}
