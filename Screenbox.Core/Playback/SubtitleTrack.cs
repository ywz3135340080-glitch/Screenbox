using System.Text.Json.Nodes;
using CommunityToolkit.Diagnostics;
using LibVLCSharp.Shared;
using Windows.Media.Core;

namespace Screenbox.Core.Playback;

public sealed partial class SubtitleTrack : MediaTrack
{
    internal int VlcSpu { get; set; }

    /// <summary>Creates an embedded or external subtitle entry discovered by mpv.</summary>
    internal SubtitleTrack(JsonNode track) : base(MediaTrackKind.TimedMetadata, track["lang"]?.GetValue<string>() ?? "")
    {
        Id = track["id"]!.ToJsonString();
        Label = track["title"]?.GetValue<string>() ?? track["lang"]?.GetValue<string>() ?? Id;
    }

    public SubtitleTrack(string language = "") : base(MediaTrackKind.TimedMetadata, language)
    {
    }

    public SubtitleTrack(LibVLCSharp.Shared.MediaTrack textTrack) : base(textTrack)
    {
        Guard.IsTrue(textTrack.TrackType == TrackType.Text, nameof(textTrack.TrackType));
        VlcSpu = textTrack.Id;
    }

    public SubtitleTrack(TimedMetadataTrack track) : base(track)
    {
    }
}
