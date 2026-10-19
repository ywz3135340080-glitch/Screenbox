using System;
using System.IO;
using Windows.Storage;

namespace Screenbox.Helpers;

/// <summary>Records local startup failures before dependency injection and telemetry are available.</summary>
internal static class StartupDiagnostics
{
    private static readonly object Gate = new();

    /// <summary>Writes only application stages and exception details to the preview's local state.</summary>
    public static void Write(string stage, Exception? exception = null)
    {
        try
        {
            string path = Path.Combine(ApplicationData.Current.LocalFolder.Path, "startup.log");
            lock (Gate)
            {
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024)
                {
                    File.Move(path, path + ".previous", true);
                }
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {stage}{Environment.NewLine}{exception}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Diagnostics must never prevent the UI from starting or obscure the original error.
        }
    }
}
