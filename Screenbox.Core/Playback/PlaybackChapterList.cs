using System.Text.Json.Nodes;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using LibVLCSharp.Shared.Structures;
using Windows.Media.Core;

namespace Screenbox.Core.Playback;

public sealed partial class PlaybackChapterList : ReadOnlyCollection<ChapterCue>
{
    private readonly List<ChapterCue> _chapters;
    private readonly PlaybackItem _item;

    internal PlaybackChapterList(PlaybackItem item) : base(new List<ChapterCue>())
    {
        _item = item;
        _chapters = (List<ChapterCue>)Items;
    }

    public void Load(IMediaPlayer player)
    {
        if (player is not VlcMediaPlayer vlcPlayer || player.PlaybackItem != _item)
            return;

        if (vlcPlayer.VlcPlayer.ChapterCount > 0)
        {
            List<ChapterDescription> chapterDescriptions = new();
            for (int i = 0; i < vlcPlayer.VlcPlayer.TitleCount; i++)
            {
                chapterDescriptions.AddRange(vlcPlayer.VlcPlayer.FullChapterDescriptions(i));
            }

            Load(chapterDescriptions);
        }
        else
        {
            Load(vlcPlayer.VlcPlayer.FullChapterDescriptions());
        }

        vlcPlayer.Chapter = _chapters.FirstOrDefault();
    }

    /// <summary>Updates chapter navigation from mpv's chapter list.</summary>
    internal void ReplaceMpv(JsonArray chapters, TimeSpan duration)
    {
        _chapters.Clear();
        for (int i = 0; i < chapters.Count; i++)
        {
            double start = chapters[i]?["time"]?.GetValue<double>() ?? 0;
            double end = i + 1 < chapters.Count ? chapters[i + 1]?["time"]?.GetValue<double>() ?? start : duration.TotalSeconds;
            _chapters.Add(new ChapterCue
            {
                Title = chapters[i]?["title"]?.GetValue<string>() ?? (i + 1).ToString(),
                StartTime = TimeSpan.FromSeconds(Math.Max(0, start)),
                Duration = TimeSpan.FromSeconds(Math.Max(0, end - start))
            });
        }
    }

    private void Load(IEnumerable<ChapterDescription> vlcChapters)
    {
        IEnumerable<ChapterCue> chapterCues = vlcChapters.Select(c => new ChapterCue
        {
            Title = c.Name ?? string.Empty,
            Duration = TimeSpan.FromMilliseconds(c.Duration),
            StartTime = TimeSpan.FromMilliseconds(c.TimeOffset)
        });

        _chapters.Clear();
        _chapters.AddRange(chapterCues);
    }
}
