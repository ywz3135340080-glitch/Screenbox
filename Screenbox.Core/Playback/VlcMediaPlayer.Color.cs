using System;
using System.IO;
using System.Collections.Generic;
using Windows.Storage.Streams;
using System.Threading;
using System.Threading.Tasks;
using Screenbox.Core.Events;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using NativeMediaPlayer = Windows.Media.Playback.MediaPlayer;

namespace Screenbox.Core.Playback;

public sealed partial class VlcMediaPlayer
{
    /// <summary>Raised when the Windows presentation surface must attach or detach.
    /// Handlers complete after the UI has updated its player reference.</summary>
    public event Func<Task>? ColorPlayerChanged;

    /// <summary>Gets the Windows player presenting corrected P010 video.</summary>
    public NativeMediaPlayer? ColorPlayer => _colorPlayer;

    private readonly object _colorLifetimeLock = new();
    private Task _colorReleaseTask = Task.CompletedTask;
    private NativeMediaPlayer? _colorPlayer;
    private ColorMediaSource? _colorSource;
    private bool _routingPlayback;
    private bool _wantColorPlay;
    private int _colorGeneration;
    private bool _closed;
    private double _requestedVolume = 1;
    private double _requestedRate = 1;
    private bool _requestedMute;
    private Windows.Media.Playback.MediaPlaybackItem? _colorPlaybackItem;
    private readonly List<IRandomAccessStream> _colorSubtitleStreams = [];
    private readonly Dictionary<string, TimedTextSource> _colorSubtitles = [];
    private string? _selectedColorSubtitle;
    private bool UsingColorPlayback => _routingPlayback || _colorPlayer is not null;

    private void ResetColorPlayback()
    {
        lock (_colorLifetimeLock)
        {
            Interlocked.Increment(ref _colorGeneration);
            _routingPlayback = false;
            NativeMediaPlayer? player = _colorPlayer;
            ColorMediaSource? source = _colorSource;
            _colorPlayer = null;
            _colorSource = null;
            _colorPlaybackItem = null;
            IRandomAccessStream[] subtitles = _colorSubtitleStreams.ToArray();
            _colorSubtitleStreams.Clear();
            _colorSubtitles.Clear();
            _selectedColorSubtitle = null;
            if (player is not null || source is not null || subtitles.Length > 0)
            {
                _colorReleaseTask = RetireColorPlaybackAsync(_colorReleaseTask, player, source, subtitles);
            }
        }
    }

    private async Task RetireColorPlaybackAsync(Task previous, NativeMediaPlayer? player,
        ColorMediaSource? source, IRandomAccessStream[] subtitles)
    {
        try
        {
            await PlaybackSurfaceLifetime.ReleaseAsync(previous, NotifyColorPlayerChangedAsync, () =>
            {
                try
                {
                    if (player is not null)
                    {
                        try { player.Pause(); }
                        finally
                        {
                            try { player.Source = null; }
                            finally { player.Dispose(); }
                        }
                    }
                }
                finally
                {
                    source?.Dispose();
                    foreach (IRandomAccessStream stream in subtitles) stream.Dispose();
                }
            }).ConfigureAwait(false);
        }
        catch (Exception error) { LogColorFailure(error); }
    }

    private async Task NotifyColorPlayerChangedAsync()
    {
        if (ColorPlayerChanged is not { } handlers) return;
        foreach (Func<Task> handler in handlers.GetInvocationList())
        {
            await handler().ConfigureAwait(false);
        }
    }

    [WinRT.DynamicWindowsRuntimeCast(typeof(StorageFile))]
    private async Task StartPlaybackAsync(PlaybackItem item)
    {
        int generation = Volatile.Read(ref _colorGeneration);
        _routingPlayback = true;
        try
        {
            // A new output must wait until the old UI surface has detached and released its player.
            await _colorReleaseTask.ConfigureAwait(false);
            if (_closed || generation != Volatile.Read(ref _colorGeneration) || PlaybackItem != item) return;
            IStorageFile? file = item.OriginalSource as IStorageFile;
            DolbyVisionConfiguration? configuration = null;
            if (file is not null)
            {
                using Stream input = await file.OpenStreamForReadAsync().ConfigureAwait(false);
                configuration = await DolbyVisionDetector.ReadConfigurationAsync(input).ConfigureAwait(false);
            }

            if (_closed || generation != Volatile.Read(ref _colorGeneration) || PlaybackItem != item) return;
            if (configuration is not { IsProfile5SingleLayer: true } || file is null)
            {
                _routingPlayback = false;
                if (_wantColorPlay) VlcPlayer.Play(item.Media);
                else _readyToPlay = true;
                return;
            }

            // Stop the old output before assigning the corrected Windows media source.
            VlcPlayer.Stop();
            PlaybackState = MediaPlaybackState.Opening;
            MediaOpened?.Invoke(this, EventArgs.Empty);
            ColorMediaSource source = await ColorMediaSource.CreateAsync(file).ConfigureAwait(false);
            if (_closed || generation != Volatile.Read(ref _colorGeneration) || PlaybackItem != item)
            {
                source.Dispose();
                return;
            }

            var player = new NativeMediaPlayer
            {
                AutoPlay = false,
                IsMuted = _requestedMute,
                Volume = Math.Clamp(_requestedVolume, 0, 1),
                IsVideoFrameServerEnabled = false,
                AudioCategory = MediaPlayerAudioCategory.Movie
            };
            player.CommandManager.IsEnabled = false;
            player.PlaybackSession.PlaybackRate = _requestedRate;
            player.IsMutedChanged += (_, _) => IsMutedChanged?.Invoke(this, EventArgs.Empty);
            player.VolumeChanged += (_, _) => VolumeChanged?.Invoke(this, EventArgs.Empty);
            player.MediaOpened += (_, _) =>
            {
                if (!ReferenceEquals(_colorPlayer, player)) return;
                NaturalDuration = player.PlaybackSession.NaturalDuration;
                NaturalVideoWidth = player.PlaybackSession.NaturalVideoWidth;
                NaturalVideoHeight = player.PlaybackSession.NaturalVideoHeight;
                NaturalVideoSizeChanged?.Invoke(this, EventArgs.Empty);
                CanSeek = player.PlaybackSession.CanSeek;
                CanSeekChanged?.Invoke(this, EventArgs.Empty);
                if (item.StartTime > TimeSpan.Zero) player.PlaybackSession.Position = item.StartTime;
            };
            player.MediaFailed += (_, args) =>
            {
                if (!ReferenceEquals(_colorPlayer, player)) return;
                LogColorFailure(new IOException(args.ErrorMessage));
                PlaybackState = MediaPlaybackState.None;
                MediaFailed?.Invoke(this, EventArgs.Empty);
            };
            player.MediaEnded += (_, _) =>
            {
                if (!ReferenceEquals(_colorPlayer, player)) return;
                PlaybackState = MediaPlaybackState.None;
                MediaEnded?.Invoke(this, EventArgs.Empty);
                if (IsLoopingEnabled && PlaybackItem == item)
                {
                    player.PlaybackSession.Position = TimeSpan.Zero;
                    player.Play();
                }
            };
            player.PlaybackSession.PlaybackStateChanged += (session, _) =>
            {
                if (!ReferenceEquals(_colorPlayer, player)) return;
                PlaybackState = session.PlaybackState;
            };
            player.PlaybackSession.PositionChanged += (session, _) =>
            {
                if (!ReferenceEquals(_colorPlayer, player)) return;
                TimeSpan old = _position;
                _position = session.Position;
                if (old != _position)
                    PositionChanged?.Invoke(this, new ValueChangedEventArgs<TimeSpan>(_position, old));
            };
            player.PlaybackSession.PlaybackRateChanged += (session, _) =>
            {
                if (!ReferenceEquals(_colorPlayer, player)) return;
                PlaybackRateChanged?.Invoke(this,
                    new ValueChangedEventArgs<double>(session.PlaybackRate, session.PlaybackRate));
            };

            lock (_colorLifetimeLock)
            {
                if (_closed || generation != Volatile.Read(ref _colorGeneration) || PlaybackItem != item)
                {
                    player.Dispose();
                    source.Dispose();
                    return;
                }

                source.AudioOffset = TimeSpan.FromMilliseconds(AudioDelay);
                _colorSource = source;
                _colorPlayer = player;
                _routingPlayback = false;
                _colorPlaybackItem = new Windows.Media.Playback.MediaPlaybackItem(source.Source);
                _colorPlaybackItem.TimedMetadataTracksChanged += (_, _) => SelectColorSubtitle(_selectedColorSubtitle);
                player.Source = _colorPlaybackItem;
            }
            await NotifyColorPlayerChangedAsync().ConfigureAwait(false);
            if (_closed || generation != Volatile.Read(ref _colorGeneration) || !ReferenceEquals(_colorPlayer, player)) return;
            if (_wantColorPlay) player.Play();

            // Metadata collections can continue to use the existing LibVLC parser.
            try { await item.Media.Parse(LibVLCSharp.Shared.MediaParseOptions.ParseLocal).ConfigureAwait(false); }
            catch (Exception error) { LogColorFailure(error); }
        }
        catch (Exception error)
        {
            if (_closed || generation != Volatile.Read(ref _colorGeneration)) return;
            _routingPlayback = false;
            LogColorFailure(error);
            PlaybackState = MediaPlaybackState.None;
            MediaFailed?.Invoke(this, EventArgs.Empty);
        }
    }


    private async Task AddColorSubtitleAsync(IStorageFile file, bool select)
    {
        ColorMediaSource? source = _colorSource;
        if (source is null) return;
        try
        {
            if (select) _selectedColorSubtitle = file.Name;
            if (_colorSubtitles.ContainsKey(file.Name))
            {
                SelectColorSubtitle(_selectedColorSubtitle);
                return;
            }

            IRandomAccessStream input = await file.OpenAsync(FileAccessMode.Read);
            if (!ReferenceEquals(source, _colorSource)) { input.Dispose(); return; }
            var timed = TimedTextSource.CreateFromStream(input);
            _colorSubtitleStreams.Add(input);
            _colorSubtitles.Add(file.Name, timed);
            timed.Resolved += (_, result) =>
            {
                if (!ReferenceEquals(source, _colorSource)) return;
                if (result.Error is not null)
                {
                    LogColorFailure(new IOException("External subtitles could not be resolved."));
                    return;
                }

                foreach (var track in result.Tracks) track.Label = file.Name;
                SelectColorSubtitle(_selectedColorSubtitle);
            };
            source.Source.ExternalTimedTextSources.Add(timed);
        }
        catch (Exception error) { LogColorFailure(error); }
    }

    private void SelectColorSubtitle(string? label)
    {
        if (_colorPlaybackItem is not { } item) return;
        for (int i = 0; i < item.TimedMetadataTracks.Count; ++i)
        {
            item.TimedMetadataTracks.SetPresentationMode((uint)i,
                label is not null && item.TimedMetadataTracks[i].Label == label
                    ? TimedMetadataTrackPresentationMode.PlatformPresented
                    : TimedMetadataTrackPresentationMode.Disabled);
        }
    }

    private static void LogColorFailure(Exception error)
    {
        try
        {
            File.AppendAllText(Path.Combine(ApplicationData.Current.LocalFolder.Path, "color-playback.log"),
                $"{DateTimeOffset.Now:o} {error}\n");
        }
        catch (Exception)
        {
            // Diagnostics must not interrupt playback or shutdown.
        }
    }
}
