using System;
using Screenbox.Helpers;
using Windows.UI.Xaml;

namespace Screenbox;

/// <summary>Starts one UI instance so in-process app service callbacks reach the active player.</summary>
public static class Program
{
    /// <summary>Initializes the UWP dispatcher and application.</summary>
    public static void Main(string[] args)
    {
        StartupDiagnostics.Write("Program entered");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            StartupDiagnostics.Write("AppDomain.UnhandledException", args.ExceptionObject as Exception);
        Application.Start(_ =>
        {
            var context = new Windows.System.DispatcherQueueSynchronizationContext(Windows.System.DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                new App();
            }
            catch (Exception exception)
            {
                StartupDiagnostics.Write("App construction failed", exception);
                throw;
            }
        });
    }
}
