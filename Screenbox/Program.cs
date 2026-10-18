using Windows.UI.Xaml;

namespace Screenbox;

/// <summary>Starts one UI instance so in-process app service callbacks reach the active player.</summary>
public static class Program
{
    /// <summary>Initializes the UWP dispatcher and application.</summary>
    public static void Main(string[] args)
    {
        Application.Start(_ =>
        {
            var context = new Windows.System.DispatcherQueueSynchronizationContext(Windows.System.DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }
}
