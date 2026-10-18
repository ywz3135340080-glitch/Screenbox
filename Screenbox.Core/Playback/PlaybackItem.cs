using System;
using LibVLCSharp.Shared;

namespace Screenbox.Core.Playback;

public class PlaybackItem
{
    internal Media Media { get; }

    public object OriginalSource { get; }

    public bool IsDisabledInPlaybackList { get; set; }

    public PlaybackAudioTrackList AudioTracks { get; }

    public PlaybackVideoTrackList VideoTracks { get; }

    public PlaybackSubtitleTrackList SubtitleTracks { get; }

    public PlaybackChapterList Chapters { get; }

    public TimeSpan StartTime { get; set; }

    internal bool IsMpvBacked { get; }

    internal TimeSpan? MpvDuration { get; set; }

    public TimeSpan? Duration => IsMpvBacked ? MpvDuration : Media.Duration > 0 ? TimeSpan.FromMilliseconds(Media.Duration) : null;

    /// <summary>Creates an mpv playback item whose tracks are supplied by the renderer.</summary>
    internal PlaybackItem(object source)
    {
        OriginalSource = source;
        IsMpvBacked = true;
        Media = null!;
        AudioTracks = new PlaybackAudioTrackList();
        VideoTracks = new PlaybackVideoTrackList();
        SubtitleTracks = new PlaybackSubtitleTrackList();
        Chapters = new PlaybackChapterList(this);
    }

    internal PlaybackItem(object source, Media media)
    {
        OriginalSource = source;
        Media = media;
        AudioTracks = new PlaybackAudioTrackList(media);
        VideoTracks = new PlaybackVideoTrackList(media);
        SubtitleTracks = new PlaybackSubtitleTrackList(media);
        Chapters = new PlaybackChapterList(this);
        StartTime = TimeSpan.Zero;
    }
}
