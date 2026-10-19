using CommunityToolkit.Diagnostics;
using LibVLCSharp.Shared;
using Windows.Media.Core;
using Windows.Storage;

namespace Screenbox.Core.Playback;

public sealed partial class SubtitleTrack : MediaTrack
{
    /// <summary>Gets the external file associated with this track, if any.</summary>
    public IStorageFile? ExternalFile { get; internal set; }

    internal int VlcSpu { get; set; }

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
