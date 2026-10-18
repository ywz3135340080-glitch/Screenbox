using System.Text.Json.Nodes;
using CommunityToolkit.Diagnostics;
using LibVLCSharp.Shared;

namespace Screenbox.Core.Playback;

public sealed partial class VideoTrack : MediaTrack
{
    internal int VlcTrackId { get; }

    public string Name { get; }

    /// <summary>Creates a track from mpv's stable track identifier and display metadata.</summary>
    internal VideoTrack(JsonNode track) : base(Windows.Media.Core.MediaTrackKind.Video, track["lang"]?.GetValue<string>() ?? "")
    {
        Id = track["id"]!.ToJsonString();
        Name = track["title"]?.GetValue<string>() ?? track["lang"]?.GetValue<string>() ?? Id;
        Label = Name;
    }

    public VideoTrack(LibVLCSharp.Shared.MediaTrack videoTrack) : base(videoTrack)
    {
        Guard.IsTrue(videoTrack.TrackType == TrackType.Video, nameof(videoTrack.TrackType));
        VlcTrackId = videoTrack.Id;
        Name = videoTrack.Description ?? videoTrack.Language ?? videoTrack.Id.ToString();
    }

    public VideoTrack(Windows.Media.Core.VideoTrack videoTrack) : base(videoTrack)
    {
        Name = videoTrack.Name;
    }
}
