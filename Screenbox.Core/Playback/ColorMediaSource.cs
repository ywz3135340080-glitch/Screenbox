using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Screenbox.Core.Playback;

/// <summary>
/// Feeds corrected, timestamped P010 video and PCM audio into Windows' synchronized presentation.
/// Each decoder owns an independent input cursor. All native operations are serialized.
/// </summary>
internal sealed partial class ColorMediaSource : IDisposable
{
    internal MediaSource Source { get; }
    internal TimeSpan AudioOffset { get; set; }
    private readonly ColorDecoder _video;
    private readonly ColorDecoder? _audio;
    private readonly MediaStreamSource _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private byte[] _videoBytes = [];
    private int _disposed;
    private bool _started;

    private ColorMediaSource(ColorDecoder video, ColorDecoder? audio)
    {
        _video = video;
        _audio = audio;
        var info = video.Info;
        var properties = VideoEncodingProperties.CreateUncompressed("P010",
            (uint)info.Width, (uint)info.Height);
        properties.FrameRate.Numerator = (uint)info.FpsNumerator;
        properties.FrameRate.Denominator = (uint)info.FpsDenominator;
        properties.PixelAspectRatio.Numerator = 1;
        properties.PixelAspectRatio.Denominator = 1;
        // Describe the already-converted output, rather than relabeling the original IPT pixels.
        properties.Properties[new Guid("5fb0fce9-be5c-4935-a811-ec838f8eed93")] = 16u;
        properties.Properties[new Guid("dbfbe4d7-0740-4ee0-8192-850ab0e21935")] = 9u;
        properties.Properties[new Guid("3e23d450-2c75-4d25-a00e-b91670d12327")] = 4u;
        properties.Properties[new Guid("c21b8ee5-b956-4071-8daf-325edf5cab11")] = 2u;
        properties.Properties[new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd")] = 2u;
        properties.Properties[new Guid("d6c6b997-272f-4ca1-8d00-8042111a0ff6")] = 1000u;
        properties.Properties[new Guid("839a4460-4e7e-4b4f-ae79-cc08905c7b27")] = 10u;
        var videoDescriptor = new VideoStreamDescriptor(properties);
        if (audio is null)
        {
            _stream = new MediaStreamSource(videoDescriptor);
        }
        else
        {
            var audioProperties = AudioEncodingProperties.CreatePcm(
                (uint)audio.Info.SampleRate, (uint)audio.Info.Channels, 16);
            if (audio.Info.ChannelMask != 0)
            {
                audioProperties.Properties[new Guid("55fb5765-644a-4caf-8479-938983bb1588")] =
                    audio.Info.ChannelMask;
            }

            _stream = new MediaStreamSource(videoDescriptor, new AudioStreamDescriptor(audioProperties));
        }

        _stream.CanSeek = info.DurationTicks > 0;
        if (_stream.CanSeek) _stream.Duration = TimeSpan.FromTicks(info.DurationTicks);
        _stream.BufferTime = TimeSpan.FromMilliseconds(250);
        _stream.Starting += OnStarting;
        _stream.SampleRequested += OnSampleRequested;
        _stream.Closed += OnClosed;
        Source = MediaSource.CreateFromMediaStreamSource(_stream);
    }

    internal static Task<ColorMediaSource> CreateAsync(IStorageFile file) =>
        CreateAsync(() => file.OpenStreamForReadAsync());

    /// <summary>Uses independent read cursors, including in the unpackaged verification host.</summary>
    internal static async Task<ColorMediaSource> CreateAsync(Func<Task<Stream>> openRead)
    {
        ColorDecoder? video = null;
        ColorDecoder? audio = null;
        try
        {
            // GPU creation and demuxing must not run on the XAML thread.
            video = await Task.Run(async () => ColorDecoder.Open(await openRead().ConfigureAwait(false), false)).ConfigureAwait(false)
                ?? throw new IOException("The video stream is absent.");
            audio = await Task.Run(async () => ColorDecoder.Open(await openRead().ConfigureAwait(false), true)).ConfigureAwait(false);
            return new ColorMediaSource(video, audio);
        }
        catch
        {
            video?.Dispose();
            audio?.Dispose();
            throw;
        }
    }

    private async void OnStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
    {
        var deferral = args.Request.GetDeferral();
        try
        {
            await Task.Run(async () =>
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (Volatile.Read(ref _disposed) != 0) return;
                    TimeSpan position = args.Request.StartPosition ?? TimeSpan.Zero;
                    if (_started || position > TimeSpan.Zero)
                    {
                        _video.Seek(position);
                        _audio?.Seek(position);
                    }

                    _started = true;
                    args.Request.SetActualStartPosition(position);
                }
                finally
                {
                    _gate.Release();
                }
            }).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Fail(error);
        }
        finally
        {
            try { deferral.Complete(); }
            catch (Exception error) { Fail(error); }
        }
    }

    [WinRT.DynamicWindowsRuntimeCast(typeof(VideoStreamDescriptor))]
    private async void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        var deferral = args.Request.GetDeferral();
        try
        {
            await Task.Run(async () =>
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (Volatile.Read(ref _disposed) != 0) return;
                    bool video = args.Request.StreamDescriptor is VideoStreamDescriptor;
                    ColorDecoder? decoder = video ? _video : _audio;
                    ColorDecoder.Sample? next = decoder?.Next();
                    if (next is not { } sample) return; // null signals end of this stream.
                    if (sample.Length <= 0 || sample.TimestampTicks < 0 ||
                        sample.DurationTicks <= 0) throw new IOException("Invalid decoded sample.");

                    byte[] bytes;
                    if (video)
                    {
                        if (_videoBytes.Length != sample.Length) _videoBytes = new byte[sample.Length];
                        bytes = _videoBytes;
                    }
                    else
                    {
                        bytes = new byte[sample.Length];
                    }

                    Marshal.Copy(sample.Data, bytes, 0, sample.Length);
                    IBuffer buffer = CryptographicBuffer.CreateFromByteArray(bytes);
                    long timestamp = sample.TimestampTicks + (video ? 0 : AudioOffset.Ticks);
                    var output = MediaStreamSample.CreateFromBuffer(buffer, TimeSpan.FromTicks(Math.Max(0, timestamp)));
                    output.Duration = TimeSpan.FromTicks(sample.DurationTicks);
                    output.KeyFrame = true; // These are independent uncompressed samples.
                    args.Request.Sample = output;
                }
                finally
                {
                    _gate.Release();
                }
            }).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Fail(error);
        }
        finally
        {
            try { deferral.Complete(); }
            catch (Exception error) { Fail(error); }
        }
    }

    private void Fail(Exception error)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            File.AppendAllText(Path.Combine(ApplicationData.Current.LocalFolder.Path, "color-playback.log"),
                $"{DateTimeOffset.Now:o} {error}\n");
        }
        catch (Exception)
        {
            // Logging is optional, including in an unpackaged verification host.
        }

        try { _stream.NotifyError(MediaStreamSourceErrorStatus.Other); }
        catch (Exception) { /* A closed source no longer accepts notifications. */ }
    }

    private void OnClosed(MediaStreamSource sender, MediaStreamSourceClosedEventArgs args) => Dispose();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stream.Starting -= OnStarting;
        _stream.SampleRequested -= OnSampleRequested;
        _stream.Closed -= OnClosed;
        Source.Dispose();
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _video.Dispose();
                _audio?.Dispose();
                _videoBytes = [];
            }
            finally
            {
                _gate.Release();
            }
        });
    }
}
