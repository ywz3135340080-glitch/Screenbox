using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Screenbox.Core.Playback;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using CommunityToolkit.WinUI;
using Windows.System;

namespace Screenbox.Helpers;

internal static class ColorStartupCheck
{
    internal static void Observe(FrameworkElement root)
    {
        if (root.IsLoaded) Ready(root);
        else root.Loaded += RootLoaded;
    }

    [WinRT.DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    private static void RootLoaded(object sender, RoutedEventArgs args)
    {
        ((FrameworkElement)sender).Loaded -= RootLoaded;
        Ready((FrameworkElement)sender);
    }

    internal static void RecordFailure(Exception error, string stage)
    {
        try
        {
            File.AppendAllText(Path.Combine(ApplicationData.Current.LocalFolder.Path, "crash.log"),
                $"{DateTimeOffset.Now:o} {stage}\n{error}\n");
        }
        catch (Exception) { }
    }

    private static void Ready(FrameworkElement root)
    {
        try
        {
            string folder = ApplicationData.Current.LocalFolder.Path;
            File.AppendAllText(Path.Combine(folder, "startup.log"),
                $"{DateTimeOffset.Now:o} Main window ready\n");
            if (!File.Exists(Path.Combine(folder, "color-self-test.request"))) return;
            _ = RunChecksAsync(root, folder);
        }
        catch (Exception)
        {
            // Diagnostics must not change app startup behavior.
        }
    }

    private static async Task RunChecksAsync(FrameworkElement root, string folder)
    {
        try
        {
            int result = await Task.Run(ColorRuntimeDiagnostics.RunSelfTest);
            File.WriteAllText(Path.Combine(folder, "color-self-test.log"), $"Native color self-test: {result}");
            await CheckPlaybackSurfaceAsync(root);
            File.WriteAllText(Path.Combine(folder, "color-surface-test.log"), "Playback surface check: 8 transitions");
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(folder, "color-surface-test.log"), error.ToString());
        }
    }

    [WinRT.DynamicWindowsRuntimeCast(typeof(MediaPlayerElement))]
    private static MediaPlayerElement? FindSurface(DependencyObject root)
    {
        if (root is MediaPlayerElement surface) return surface;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindSurface(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
        }

        return null;
    }

    private static async Task CheckPlaybackSurfaceAsync(FrameworkElement root)
    {
        MediaPlayerElement surface = FindSurface(root)
            ?? throw new InvalidOperationException("The playback surface was not loaded.");
        DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
        Visibility visibility = surface.Visibility;
        try
        {
            for (int transition = 0; transition < 8; transition++)
            {
                var properties = VideoEncodingProperties.CreateUncompressed("P010", 64, 32);
                properties.FrameRate.Numerator = 25;
                properties.FrameRate.Denominator = 1;
                properties.PixelAspectRatio.Numerator = 1;
                properties.PixelAspectRatio.Denominator = 1;
                properties.Properties[new Guid("5fb0fce9-be5c-4935-a811-ec838f8eed93")] = 15u;
                properties.Properties[new Guid("dbfbe4d7-0740-4ee0-8192-850ab0e21935")] = 9u;
                properties.Properties[new Guid("3e23d450-2c75-4d25-a00e-b91670d12327")] = 4u;
                properties.Properties[new Guid("c21b8ee5-b956-4071-8daf-325edf5cab11")] = 2u;
                properties.Properties[new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd")] = 2u;
                var stream = new MediaStreamSource(new VideoStreamDescriptor(properties))
                {
                    BufferTime = TimeSpan.Zero,
                    Duration = TimeSpan.FromSeconds(60),
                    CanSeek = false
                };
                byte[] pixels = new byte[64 * 32 * 3];
                for (int i = 0; i < pixels.Length; i += 2)
                {
                    ushort value = i < 64 * 32 * 2 ? (ushort)4096 : (ushort)32768;
                    pixels[i] = (byte)value;
                    pixels[i + 1] = (byte)(value >> 8);
                }

                var buffer = CryptographicBuffer.CreateFromByteArray(pixels);
                long frame = -1;
                stream.Starting += (_, args) => args.Request.SetActualStartPosition(TimeSpan.Zero);
                stream.SampleRequested += (_, args) =>
                {
                    long index = Interlocked.Increment(ref frame);
                    var sample = MediaStreamSample.CreateFromBuffer(buffer, TimeSpan.FromMilliseconds(index * 40));
                    sample.Duration = TimeSpan.FromMilliseconds(40);
                    args.Request.Sample = sample;
                };
                MediaSource source = MediaSource.CreateFromMediaStreamSource(stream);
                var player = new MediaPlayer { AutoPlay = false, IsMuted = true };
                var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                player.MediaOpened += (_, _) => opened.TrySetResult();
                player.MediaFailed += (_, args) => opened.TrySetException(new IOException(args.ErrorMessage));
                try
                {
                    player.Source = source;
                    surface.Visibility = Visibility.Visible;
                    surface.SetMediaPlayer(player);
                    player.Play();
                    await opened.Task.WaitAsync(TimeSpan.FromSeconds(6));
                    await Task.Delay(80);
                    if (Interlocked.Read(ref frame) < 0)
                        throw new InvalidOperationException("The surface did not request a P010 sample.");
                }
                finally
                {
                    await PlaybackSurfaceLifetime.ReleaseAsync(Task.CompletedTask,
                        () => dispatcher.EnqueueAsync(() => surface.SetMediaPlayer(null)),
                        () =>
                        {
                            try { player.Source = null; }
                            finally { player.Dispose(); source.Dispose(); }
                        });
                }
            }
        }
        finally
        {
            surface.Visibility = visibility;
        }
    }
}
