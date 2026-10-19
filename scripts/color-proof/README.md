# Internal color-path proof

This is a developer verification tool, not a Screenbox installer or completed playback backend.
It links FFmpeg's HEVC decoder to the independent libplacebo Direct3D 11 renderer.
It does not launch or link an external player.

The workflow compiles a limited decoder from a pinned FFmpeg revision, packages only transitive native dependencies,
then renders a generated neutral frame using Direct3D 11 and checks its pixels.
The native archive contains the dependency versions, hashes and license notices.

For a locally supplied Dolby Vision video, the tool requires parsed Dolby Vision metadata on each decoded frame
and requires the renderer to accept it. It writes an SDR comparison, an ordinary-YCbCr control, and 16-bit BT.2020/PQ pixels.
The optional frame count validates consecutive decoded frames and records the elapsed time for the frames after the first.
Input files are read only. The workflow does not accept or upload users' videos.

Offscreen PQ pixels do not demonstrate that Windows has activated HDR on a display.
This tool does not validate audio, synchronization, real-time presentation, seeking in a player, subtitles,
UWP dependency loading, app startup, or the Screenbox UI.
Those remain integration gates before a user-facing installer can be considered ready.
