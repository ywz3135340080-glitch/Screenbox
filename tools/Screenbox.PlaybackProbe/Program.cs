using Screenbox.Core.Playback;
using Windows.Media.Playback;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Storage;

internal static class Program
{
    private static readonly TaskCompletionSource<VideoEncodingProperties> VideoDescription =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [WinRT.DynamicWindowsRuntimeCast(typeof(VideoStreamDescriptor))]
    private static void InspectRequestedVideo(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        if (args.Request.StreamDescriptor is VideoStreamDescriptor video)
            VideoDescription.TrySetResult(video.EncodingProperties);
    }

    private static async Task VerifyHdrDescriptionAsync()
    {
        VideoEncodingProperties video = await VideoDescription.Task.WaitAsync(TimeSpan.FromSeconds(10));
        uint transfer = Convert.ToUInt32(video.Properties[new Guid("5fb0fce9-be5c-4935-a811-ec838f8eed93")]);
        uint primaries = Convert.ToUInt32(video.Properties[new Guid("dbfbe4d7-0740-4ee0-8192-850ab0e21935")]);
        uint matrix = Convert.ToUInt32(video.Properties[new Guid("3e23d450-2c75-4d25-a00e-b91670d12327")]);
        uint range = Convert.ToUInt32(video.Properties[new Guid("c21b8ee5-b956-4071-8daf-325edf5cab11")]);
        Console.WriteLine($"Video tags at sample request: {video.Subtype}, transfer={transfer}, primaries={primaries}, matrix={matrix}, range={range}");
        // These values are the Media Foundation HDR10 signal contract in mfobjects.h.
        if (video.Subtype != "P010" || transfer != 15 || primaries != 9 || matrix != 4 || range != 2)
            throw new IOException("The corrected PQ pixels are not described as limited-range BT.2020/PQ to Windows.");
    }

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
        source.Source.MediaStreamSource.SampleRequested += InspectRequestedVideo;
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
        await VerifyHdrDescriptionAsync();
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
