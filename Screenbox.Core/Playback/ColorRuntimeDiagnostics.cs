namespace Screenbox.Core.Playback;

/// <summary>Provides a packaged-app check for the native color component.</summary>
public static class ColorRuntimeDiagnostics
{
    /// <summary>Renders a neutral frame to P010 and validates its layout and levels.</summary>
    public static int RunSelfTest() => ColorDecoder.SelfTest();
}
