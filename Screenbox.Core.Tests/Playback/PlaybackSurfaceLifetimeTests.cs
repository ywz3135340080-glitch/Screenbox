using Screenbox.Core.Playback;

namespace Screenbox.Core.Tests.Playback;

public partial class PlaybackSurfaceLifetimeTests
{
    [Test]
    public async Task Release_QueuedSurfaceDetach_ShouldKeepPlayerAliveUntilUiCompletes()
    {
        var detached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int releases = 0;
        Task retirement = PlaybackSurfaceLifetime.ReleaseAsync(Task.CompletedTask,
            () => detached.Task, () => Interlocked.Increment(ref releases));
        await Assert.That(retirement.IsCompleted).IsFalse();
        await Assert.That(Volatile.Read(ref releases)).IsEqualTo(0);

        detached.SetResult();
        await retirement;
        await Assert.That(releases).IsEqualTo(1);
    }

    [Test]
    public async Task Release_RapidFileChanges_ShouldFinishPreviousPlayerBeforeNextDetach()
    {
        var firstDetach = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        Task first = PlaybackSurfaceLifetime.ReleaseAsync(Task.CompletedTask,
            () => firstDetach.Task, () => order.Add("first released"));
        Task second = PlaybackSurfaceLifetime.ReleaseAsync(first,
            () => { order.Add("second detached"); return Task.CompletedTask; },
            () => order.Add("second released"));
        await Assert.That(order.Count).IsEqualTo(0);

        firstDetach.SetResult();
        await Task.WhenAll(first, second);
        await Assert.That(string.Join(",", order))
            .IsEqualTo("first released,second detached,second released");
    }

    [Test]
    public async Task Release_FailedDetach_ShouldNotDisposePlayerStillAttachedToUi()
    {
        int releases = 0;
        bool failed = false;
        try
        {
            await PlaybackSurfaceLifetime.ReleaseAsync(Task.CompletedTask,
                () => Task.FromException(new InvalidOperationException("UI detached unsuccessfully")),
                () => Interlocked.Increment(ref releases));
        }
        catch (InvalidOperationException) { failed = true; }
        await Assert.That(failed).IsTrue();
        await Assert.That(releases).IsEqualTo(0);
    }
}
