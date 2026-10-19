using System;
using System.Threading;
using Screenbox.Helpers;
using Windows.ApplicationModel.Core;
using Windows.UI.Xaml;

namespace Screenbox;

/// <summary>Starts one UI instance so in-process app service callbacks reach the active player.</summary>
public static class Program
{
    /// <summary>Initializes the UWP dispatcher and application.</summary>
    public static void Main(string[] args)
    {
        StartupDiagnostics.Write("Program entered");
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            StartupDiagnostics.Write("AppDomain.UnhandledException", eventArgs.ExceptionObject as Exception);

        Application.Start(_ =>
        {
            try
            {
                StartupDiagnostics.Write("Application initialization callback entered");
                // CoreApplication has already created the UWP view dispatcher for this callback.
                // The separate composition queue is not required until the XAML application is initialized.
                var context = new CoreDispatcherContext(CoreApplication.GetCurrentView().Dispatcher);
                StartupDiagnostics.Write("Using the UWP view dispatcher for startup");
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            }
            catch (Exception exception)
            {
                StartupDiagnostics.Write("Application initialization failed", exception);
                throw;
            }
        });
    }
}
