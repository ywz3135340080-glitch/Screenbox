using System.Text.Json.Nodes;
using CommunityToolkit.Diagnostics;
using LibVLCSharp.Shared;

namespace Screenbox.Core.Playback;

public sealed partial class AudioTrack : MediaTrack
{
    internal int VlcTrackId { get; }

    public string Name { get; }

    /// <summary>Creates a track from mpv's stable track identifier and display metadata.</summary>
    internal AudioTrack(JsonNode track) : base(Windows.Media.Core.MediaTrackKind.Audio, track["lang"]?.GetValue<string>() ?? "")
    {
        Id = track["id"]!.ToJsonString();
        Name = track["title"]?.GetValue<string>() ?? track["lang"]?.GetValue<string>() ?? Id;
        Label = Name;
    }

    public AudioTrack(LibVLCSharp.Shared.MediaTrack audioTrack) : base(audioTrack)
    {
        Guard.IsTrue(audioTrack.TrackType == TrackType.Audio, nameof(audioTrack.TrackType));
        VlcTrackId = audioTrack.Id;
        Name = audioTrack.Description ?? audioTrack.Language ?? audioTrack.Id.ToString();
    }

    public AudioTrack(Windows.Media.Core.AudioTrack audioTrack) : base(audioTrack)
    {
        Name = audioTrack.Name;
    }
}
