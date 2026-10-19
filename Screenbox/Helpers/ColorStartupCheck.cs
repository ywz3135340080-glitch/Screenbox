using System;
using System.IO;
using System.Threading.Tasks;
using Screenbox.Core.Playback;
using Windows.Storage;
using Windows.UI.Xaml;

namespace Screenbox.Helpers;

internal static class ColorStartupCheck
{
    internal static void Observe(FrameworkElement root)
    {
        if (root.IsLoaded) Ready();
        else root.Loaded += RootLoaded;
    }

    private static void RootLoaded(object sender, RoutedEventArgs args)
    {
        ((FrameworkElement)sender).Loaded -= RootLoaded;
        Ready();
    }

    private static void Ready()
    {
        try
        {
            string folder = ApplicationData.Current.LocalFolder.Path;
            File.AppendAllText(Path.Combine(folder, "startup.log"),
                $"{DateTimeOffset.Now:o} Main window ready\n");
            if (!File.Exists(Path.Combine(folder, "color-self-test.request"))) return;
            _ = Task.Run(() =>
            {
                string report;
                try { report = $"Native color self-test: {ColorRuntimeDiagnostics.RunSelfTest()}"; }
                catch (Exception error) { report = error.ToString(); }
                File.WriteAllText(Path.Combine(folder, "color-self-test.log"), report);
            });
        }
        catch (Exception)
        {
            // Diagnostics must not change app startup behavior.
        }
    }
}
