using Screenbox.Core.Playback;
using Windows.Media.Playback;
using Windows.Storage;

internal static class Program
{
    [MTAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        try { return RunAsync(args).GetAwaiter().GetResult(); }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test")
        {
            int result = ColorDecoder.SelfTest();
            Console.WriteLine($"Native P010 check: {result}");
            return result == 0 ? 0 : 1;
        }

        if (args.Length != 1) return 2;
        Console.WriteLine("Opening independent read-only inputs.");
        using ColorMediaSource source = await ColorMediaSource.CreateAsync(
            () => Task.FromResult<Stream>(File.OpenRead(args[0])));
        Console.WriteLine("Corrected media source created; opening Windows presentation.");
        using var player = new MediaPlayer
        {
            AutoPlay = false, Volume = 0, IsVideoFrameServerEnabled = true,
            AudioCategory = MediaPlayerAudioCategory.Movie
        };
        player.CommandManager.IsEnabled = false;
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? failure = null;
        int frames = 0;
        player.MediaOpened += (_, _) => opened.TrySetResult();
        player.MediaFailed += (_, e) =>
        {
            failure = $"{e.Error}: {e.ErrorMessage} ({e.ExtendedErrorCode})";
            opened.TrySetException(new IOException(failure));
        };
        player.VideoFrameAvailable += (_, _) => Interlocked.Increment(ref frames);
        Console.WriteLine("Windows media player created; assigning source.");
        player.Source = source.Source;
        player.Play();
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(40));
        Console.WriteLine($"Opened {player.PlaybackSession.NaturalVideoWidth}x{player.PlaybackSession.NaturalVideoHeight}; duration={player.PlaybackSession.NaturalDuration.TotalSeconds}");
        player.PlaybackSession.Position = TimeSpan.FromSeconds(600);
        await Task.Delay(3500);
        if (failure is not null) throw new IOException(failure);
        double playing = player.PlaybackSession.Position.TotalSeconds;
        Console.WriteLine($"Playing: position={playing:F3}, presented frame events={frames}");
        if (playing < 601 || playing > 612 || frames < 5) throw new IOException("Presentation did not advance.");

        player.Pause();
        double paused = player.PlaybackSession.Position.TotalSeconds;
        await Task.Delay(800);
        if (Math.Abs(player.PlaybackSession.Position.TotalSeconds - paused) > 0.2)
            throw new IOException("Pause did not hold the media clock.");
        player.Play();
        DateTime resumeDeadline = DateTime.UtcNow.AddSeconds(8);
        while (player.PlaybackSession.Position.TotalSeconds < paused + 0.5 &&
               DateTime.UtcNow < resumeDeadline)
        {
            if (failure is not null) throw new IOException(failure);
            await Task.Delay(100);
        }
        Console.WriteLine($"Resumed: paused at={paused:F3}, now={player.PlaybackSession.Position.TotalSeconds:F3}, state={player.PlaybackSession.PlaybackState}");
        if (player.PlaybackSession.Position.TotalSeconds < paused + 0.5)
            throw new IOException("Playback did not resume within eight seconds.");
        player.PlaybackSession.Position = TimeSpan.FromSeconds(1200);
        await Task.Delay(2500);
        double seeked = player.PlaybackSession.Position.TotalSeconds;
        if (failure is not null) throw new IOException(failure);
        Console.WriteLine($"After seek: position={seeked:F3}, presented frame events={frames}");
        if (seeked < 1200 || seeked > 1210) throw new IOException("Seek did not land correctly.");
        Console.WriteLine("PASS: media opened, video events arrived, clock advanced, pause/resume and seek succeeded. Display HDR brightness and perceptual audio sync are not verified by this test.");
        player.Pause();
        player.Source = null;
        return 0;
    }
}
