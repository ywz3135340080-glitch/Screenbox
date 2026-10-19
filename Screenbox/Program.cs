using System;
using System.Threading;
using Screenbox.Helpers;
using Windows.ApplicationModel.Core;
using Windows.System;
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
                DispatcherQueue? queue = DispatcherQueue.GetForCurrentThread();
                // A UWP view always has a CoreDispatcher. The separate composition queue can be unavailable this early.
                SynchronizationContext context = queue is not null
                    ? new DispatcherQueueSynchronizationContext(queue)
                    : new CoreDispatcherContext(CoreApplication.GetCurrentView().Dispatcher);
                StartupDiagnostics.Write(queue is null ? "Using CoreDispatcher startup context" : "Using DispatcherQueue startup context");
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
