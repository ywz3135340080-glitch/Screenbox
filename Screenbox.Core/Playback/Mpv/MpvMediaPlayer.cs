using System;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Screenbox.Core.Events;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace Screenbox.Core.Playback;

/// <summary>Adapts mpv playback and metadata to Screenbox's existing player controls.</summary>
public sealed class MpvMediaPlayer : IMediaPlayer
{
    public event TypedEventHandler<IMediaPlayer, EventArgs>? MediaEnded;
    public event TypedEventHandler<IMediaPlayer, EventArgs>? MediaFailed;
    public event TypedEventHandler<IMediaPlayer, EventArgs>? MediaOpened;
    public event TypedEventHandler<IMediaPlayer, EventArgs>? IsMutedChanged;
    public event TypedEventHandler<IMediaPlayer, EventArgs>? VolumeChanged;
    public event TypedEventHandler<IMediaPlayer, ValueChangedEventArgs<PlaybackItem?>>? PlaybackItemChanged;
    public event TypedEventHandler<IMediaPlayer, EventArgs>? BufferingProgressChanged;
    public event TypedEventHandler<IMediaPlayer, EventArgs>? BufferingStarted;
    public event TypedEventHandler<IMediaPlayer, EventArgs>? BufferingEnded;
    public event TypedEventHandler<IMediaPlayer, ValueChangedEventArgs<TimeSpan>>? NaturalDurationChanged;
    public event TypedEventHandler<IMediaPlayer, EventArgs>? NaturalVideoSizeChanged;
    public event TypedEventHandler<IMediaPlayer, EventArgs>? CanSeekChanged;
    public event TypedEventHandler<IMediaPlayer, ValueChangedEventArgs<TimeSpan>>? PositionChanged;
    public event TypedEventHandler<IMediaPlayer, ValueChangedEventArgs<ChapterCue?>>? ChapterChanged;
    public event TypedEventHandler<IMediaPlayer, ValueChangedEventArgs<MediaPlaybackState>>? PlaybackStateChanged;
    public event TypedEventHandler<IMediaPlayer, ValueChangedEventArgs<double>>? PlaybackRateChanged;

    private readonly MpvConnection _connection;
    private PlaybackItem? _item;
    private bool _closed;
    private int _generation;
    private bool _needsLoad;
    private bool _updatingTracks;
    private bool _isMuted;
    private bool _loop;
    private bool _paused;
    private double _volume = 1;
    private double _rate = 1;
    private TimeSpan _position;
    private MediaPlaybackState _state;
    private Rect _sourceRect = new(0, 0, 1, 1);

    /// <summary>Creates an adapter for the single mpv renderer owned by the application.</summary>
    public MpvMediaPlayer()
    {
        _connection = MpvConnection.Current;
        _connection.EventReceived += OnEvent;
    }

    public bool CanPause => _item is not null;
    public bool CanSeek { get; private set; }
    public double BufferingProgress { get; private set; }
    public uint NaturalVideoHeight { get; private set; }
    public uint NaturalVideoWidth { get; private set; }
    public TimeSpan NaturalDuration { get; private set; }
    public ChapterCue? Chapter { get; private set; }
    public MediaPlaybackState PlaybackState => _state;

    public bool IsMuted
    {
        get => _isMuted;
        set { _isMuted = value; Set("mute", value ? "yes" : "no"); }
    }

    public double Volume
    {
        get => _volume;
        set { _volume = Math.Clamp(value, 0, 1); Set("volume", Number(_volume * 100)); }
    }

    public bool IsLoopingEnabled
    {
        get => _loop;
        set { _loop = value; Set("loop-file", value ? "inf" : "no"); }
    }

    public double PlaybackRate
    {
        get => _rate;
        set { _rate = value; Set("speed", Number(value)); }
    }

    public TimeSpan Position
    {
        get => _position;
        set => Send("seek", Number(Math.Max(0, value.TotalSeconds)), "absolute+exact");
    }

    public DeviceInformation? AudioDevice
    {
        get => null;
        set { if (value is not null) Set("audio-device", "wasapi/" + value.Id); }
    }

    public Rect NormalizedSourceRect
    {
        get => _sourceRect;
        set
        {
            _sourceRect = value;
            Set("video-aspect-override", value.Width > 0 && value.Height > 0 && NaturalVideoWidth > 0
                ? Number(value.Width * NaturalVideoWidth / (value.Height * NaturalVideoHeight)) : "-1");
        }
    }

    public PlaybackItem? PlaybackItem
    {
        get => _item;
        set
        {
            if (ReferenceEquals(value, _item)) return;
            PlaybackItem? previous = _item;
            if (previous is not null) RegisterTracks(previous, false);
            _item = value;
            _generation++;
            _needsLoad = value is not null;
            if (value is not null) RegisterTracks(value, true);
            else { Send("stop"); ChangeState(MediaPlaybackState.None); }
            PlaybackItemChanged?.Invoke(this, new(value, previous));
        }
    }

    /// <summary>Loads the current item or resumes it through mpv.</summary>
    public void Play()
    {
        if (_item is null) return;
        _connection.RequestInitialization();
        if (_needsLoad)
        {
            _needsLoad = false;
            string source = _item.OriginalSource switch
            {
                IStorageFile file => file.Path,
                Uri uri => uri.AbsoluteUri,
                string path => path,
                _ => throw new NotSupportedException("This source cannot be passed to mpv.")
            };
            if (string.IsNullOrWhiteSpace(source))
            {
                MediaFailed?.Invoke(this, EventArgs.Empty);
                return;
            }
            ChangeState(MediaPlaybackState.Opening);
            Send("loadfile", source, "replace");
        }
        Set("pause", "no");
    }

    /// <summary>Saves a rendered screenshot after mpv confirms the image has been written.</summary>
    public Task SaveScreenshotAsync(string path) => _connection.CommandAsync("screenshot-to-file", path, "window");

    public void Pause() => Set("pause", "yes");
    public void StepForwardOneFrame() => Send("frame-step");
    public void StepBackwardOneFrame() => Send("frame-back-step");
    public void NextChapter() => Send("add", "chapter", "1");
    public void PreviousChapter() => Send("add", "chapter", "-1");

    /// <summary>Loads an external subtitle using mpv's subtitle renderer.</summary>
    public void AddSubtitle(IStorageFile file, bool select = true) =>
        Send("sub-add", file.Path, select ? "select" : "auto");

    /// <summary>Gets or sets audio delay in milliseconds, matching Screenbox's settings.</summary>
    public double AudioDelay
    {
        get => _audioDelay;
        set { _audioDelay = value; Set("audio-delay", Number(value / 1000)); }
    }
    private double _audioDelay;

    /// <summary>Gets or sets subtitle delay in milliseconds, matching Screenbox's settings.</summary>
    public double SubtitleDelay
    {
        get => _subtitleDelay;
        set { _subtitleDelay = value; Set("sub-delay", Number(value / 1000)); }
    }
    private double _subtitleDelay;

    /// <summary>Stops this adapter and removes its event subscription.</summary>
    public void Close()
    {
        if (_closed) return;
        if (_item is not null) RegisterTracks(_item, false);
        _connection.EventReceived -= OnEvent;
        _ = _connection.CommandAsync("stop");
        _closed = true;
    }

    private void Set(string property, string value) => Send("set_property", property, value);

    private void Send(params string[] command) => _ = SendAsync(command);

    private async Task SendAsync(string[] command)
    {
        if (_closed) return;
        int generation = _generation;
        bool mediaSpecific = command[0] is "loadfile" or "seek" or "sub-add" or "frame-step" or "frame-back-step" ||
            command.Length >= 3 && command[0] == "set_property" && command[1] is "aid" or "vid" or "sid";
        try
        {
            await _connection.CommandAsync(() => !_closed && (!mediaSpecific || generation == _generation), command);
        }
        catch (Exception)
        {
            if (!_closed && _item is not null && command[0] == "loadfile")
                MediaFailed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private void ChangeState(MediaPlaybackState state)
    {
        if (_state == state) return;
        var previous = _state;
        _state = state;
        PlaybackStateChanged?.Invoke(this, new(state, previous));
    }

    private void RegisterTracks(PlaybackItem item, bool register)
    {
        if (register)
        {
            item.AudioTracks.SelectedIndexChanged += OnAudioSelection;
            item.VideoTracks.SelectedIndexChanged += OnVideoSelection;
            item.SubtitleTracks.SelectedIndexChanged += OnSubtitleSelection;
        }
        else
        {
            item.AudioTracks.SelectedIndexChanged -= OnAudioSelection;
            item.VideoTracks.SelectedIndexChanged -= OnVideoSelection;
            item.SubtitleTracks.SelectedIndexChanged -= OnSubtitleSelection;
        }
    }

    private void OnAudioSelection(ISingleSelectMediaTrackList sender, object? args) => Select("aid", _item?.AudioTracks, sender.SelectedIndex);
    private void OnVideoSelection(ISingleSelectMediaTrackList sender, object? args) => Select("vid", _item?.VideoTracks, sender.SelectedIndex);
    private void OnSubtitleSelection(ISingleSelectMediaTrackList sender, object? args) => Select("sid", _item?.SubtitleTracks, sender.SelectedIndex);

    private void Select<T>(string property, SingleSelectTrackList<T>? tracks, int index) where T : IMediaTrack
    {
        if (!_updatingTracks) Set(property, tracks is not null && index >= 0 && index < tracks.Count ? tracks[index].Id : "no");
    }

    private void OnEvent(string kind, JsonNode? data)
    {
        if (_closed) return;
        if (kind is "initialization-canceled" or "initialization-error")
        {
            _generation++;
            _needsLoad = _item is not null;
            if (_item is not null) ChangeState(MediaPlaybackState.Paused);
        }
        else if (kind == "error")
        {
            if (_item is not null) { ChangeState(MediaPlaybackState.None); MediaFailed?.Invoke(this, EventArgs.Empty); }
        }
        else if (kind == "file-loaded")
        {
            MediaOpened?.Invoke(this, EventArgs.Empty);
            ChangeState(MediaPlaybackState.Playing);
        }
        else if (kind == "end-file" && data?["reason"]?.GetValue<string>() is { } reason)
        {
            if (reason == "eof") { ChangeState(MediaPlaybackState.None); MediaEnded?.Invoke(this, EventArgs.Empty); }
            else if (reason == "error") { ChangeState(MediaPlaybackState.None); MediaFailed?.Invoke(this, EventArgs.Empty); }
        }
        else if (kind == "state" && data is JsonObject state)
        {
            UpdateState(state);
        }
    }

    private void UpdateState(JsonObject state)
    {
        if (_item is null) return;
        if (state["time-pos"] is { } position)
        {
            TimeSpan previous = _position;
            _position = Seconds(position);
            PositionChanged?.Invoke(this, new(_position, previous));
        }
        if (state["duration"] is { } duration)
        {
            TimeSpan previous = NaturalDuration;
            NaturalDuration = Seconds(duration);
            if (_item is not null) _item.MpvDuration = NaturalDuration;
            NaturalDurationChanged?.Invoke(this, new(NaturalDuration, previous));
        }
        if (state["pause"] is { } paused) { _paused = paused.GetValue<bool>(); ChangeState(_paused ? MediaPlaybackState.Paused : MediaPlaybackState.Playing); }
        if (state["volume"] is { } volume) { _volume = volume.GetValue<double>() / 100; VolumeChanged?.Invoke(this, EventArgs.Empty); }
        if (state["mute"] is { } mute) { _isMuted = mute.GetValue<bool>(); IsMutedChanged?.Invoke(this, EventArgs.Empty); }
        if (state["speed"] is { } speed)
        {
            double previous = _rate;
            _rate = speed.GetValue<double>();
            PlaybackRateChanged?.Invoke(this, new(_rate, previous));
        }
        if (state["seekable"] is { } seekable) { CanSeek = seekable.GetValue<bool>(); CanSeekChanged?.Invoke(this, EventArgs.Empty); }
        if (state["video-params"] is JsonObject video)
        {
            NaturalVideoWidth = (uint)Math.Max(0, video["dw"]?.GetValue<int>() ?? video["w"]?.GetValue<int>() ?? 0);
            NaturalVideoHeight = (uint)Math.Max(0, video["dh"]?.GetValue<int>() ?? video["h"]?.GetValue<int>() ?? 0);
            NaturalVideoSizeChanged?.Invoke(this, EventArgs.Empty);
        }
        if (state["paused-for-cache"] is { } buffering)
        {
            bool waiting = buffering.GetValue<bool>();
            BufferingProgress = waiting ? 0 : 1;
            if (waiting) { BufferingStarted?.Invoke(this, EventArgs.Empty); ChangeState(MediaPlaybackState.Buffering); }
            else { BufferingEnded?.Invoke(this, EventArgs.Empty); ChangeState(_paused ? MediaPlaybackState.Paused : MediaPlaybackState.Playing); }
            BufferingProgressChanged?.Invoke(this, EventArgs.Empty);
        }
        if (state["track-list"] is JsonArray tracks && _item is not null) UpdateTracks(tracks);
        if (state["chapter-list"] is JsonArray chapters && _item is not null) _item.Chapters.ReplaceMpv(chapters, NaturalDuration);
        if (state["chapter"] is { } chapter && _item is not null)
        {
            ChapterCue? previous = Chapter;
            int index = chapter.GetValue<int>();
            Chapter = index >= 0 && index < _item.Chapters.Count ? _item.Chapters[index] : null;
            ChapterChanged?.Invoke(this, new(Chapter, previous));
        }
    }

    private static TimeSpan Seconds(JsonNode node)
    {
        double value = node.GetValue<double>();
        return double.IsFinite(value) && value >= 0 ? TimeSpan.FromSeconds(Math.Min(value, TimeSpan.MaxValue.TotalSeconds / 2)) : TimeSpan.Zero;
    }

    private void UpdateTracks(JsonArray tracks)
    {
        _updatingTracks = true;
        try
        {
            _item!.AudioTracks.ReplaceMpv(tracks.Where(t => t?["type"]?.GetValue<string>() == "audio").Select(t => new AudioTrack(t!)));
            _item.VideoTracks.ReplaceMpv(tracks.Where(t => t?["type"]?.GetValue<string>() == "video").Select(t => new VideoTrack(t!)));
            _item.SubtitleTracks.ReplaceMpv(tracks.Where(t => t?["type"]?.GetValue<string>() == "sub").Select(t => new SubtitleTrack(t!)));
            SelectCurrent(_item.AudioTracks, tracks, "audio");
            SelectCurrent(_item.VideoTracks, tracks, "video");
            SelectCurrent(_item.SubtitleTracks, tracks, "sub");
        }
        finally { _updatingTracks = false; }
    }

    private static void SelectCurrent<T>(SingleSelectTrackList<T> target, JsonArray source, string type) where T : IMediaTrack
    {
        string? id = source.FirstOrDefault(t => t?["type"]?.GetValue<string>() == type && t?["selected"]?.GetValue<bool>() == true)?["id"]?.ToJsonString();
        int index = -1;
        for (int i = 0; i < target.Count; i++) if (target[i].Id == id) { index = i; break; }
        target.SelectedIndex = index;
    }
}
