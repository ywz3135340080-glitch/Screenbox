using CommunityToolkit.Mvvm.Messaging;
using Screenbox.Core.Enums;
using Screenbox.Core.Messages;
using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Screenbox.Core.Playback;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;

namespace Screenbox.Controls;

/// <summary>Positions the native mpv video surface inside Screenbox's existing player layout.</summary>
public sealed partial class MpvVideoView : FrameworkElement
{
    /// <summary>Signals that the Screenbox playback adapter can be initialized.</summary>
    public event EventHandler<VideoViewInitializedEventArgs>? Initialized;

    private readonly DispatcherQueueTimer _layoutTimer;
    private bool _started;
    private bool _initialized;
    private bool _starting;
    private bool _loaded;
    private bool _updating;
    private string _lastBounds = "";

    /// <summary>Creates a layout host while retaining Screenbox's original controls.</summary>
    public MpvVideoView()
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        MpvConnection.Current.SetDispatcher(dispatcher);
        _layoutTimer = dispatcher.CreateTimer();
        _layoutTimer.Interval = TimeSpan.FromMilliseconds(120);
        _layoutTimer.Tick += (_, _) => _ = UpdateBoundsAsync();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _loaded = true;
        MpvConnection.Current.InitializationRequested -= OnInitializationRequested;
        MpvConnection.Current.InitializationRequested += OnInitializationRequested;
        if (!_initialized)
        {
            _initialized = true;
            Initialized?.Invoke(this, new VideoViewInitializedEventArgs(Array.Empty<string>()));
        }
        if (MpvConnection.Current.IsAttached) { _started = true; _layoutTimer.Start(); }
        else if (ApplicationData.Current.LocalSettings.Values.ContainsKey("MpvExecutable"))
            _ = EnsureRendererAsync();
    }

    private async void OnInitializationRequested()
    {
        if (_loaded) await EnsureRendererAsync();
    }

    private async Task EnsureRendererAsync()
    {
        if (_starting || MpvConnection.Current.IsAttached) return;
        _starting = true;
        try
        {
            var settings = ApplicationData.Current.LocalSettings;
            string? executable = settings.Values.TryGetValue("MpvExecutable", out object? saved) ? saved as string : null;
            if (string.IsNullOrWhiteSpace(executable))
            {
                var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
                picker.FileTypeFilter.Add(".exe");
                StorageFile? file = await picker.PickSingleFileAsync();
                if (file is null || !file.Name.Equals("mpv.exe", StringComparison.OrdinalIgnoreCase))
                {
                    MpvConnection.Current.CancelInitialization();
                    return;
                }
                executable = file.Path;
                settings.Values["MpvExecutable"] = executable;
            }
            await MpvConnection.Current.AttachAsync(executable, Environment.ProcessId);
            _started = true;
            _layoutTimer.Start();
            await UpdateBoundsAsync();
        }
        catch (Exception exception)
        {
            ApplicationData.Current.LocalSettings.Values.Remove("MpvExecutable");
            WeakReferenceMessenger.Default.Send(new NotificationMessage(
                NotificationLevel.Error, NotificationKind.InitializationFailed, message: exception.Message));
        }
        finally
        {
            _starting = false;
        }
    }

    private async void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _loaded = false;
        MpvConnection.Current.InitializationRequested -= OnInitializationRequested;
        _layoutTimer.Stop();
        if (!_started) return;
        try { await MpvConnection.Current.SetBoundsAsync(0, 0, 0, 0, false); }
        catch (Exception) { }
    }

    [DynamicWindowsRuntimeCast(typeof(UIElement))]
    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    private async Task UpdateBoundsAsync()
    {
        if (_updating || Window.Current?.Content is not UIElement root) return;
        _updating = true;
        try
        {
            double scale = XamlRoot?.RasterizationScale ?? 1;
            Point position = TransformToVisual(root).TransformPoint(new Point());
            bool visible = ActualWidth > 0 && ActualHeight > 0;
            for (DependencyObject? ancestor = this; ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
            {
                if (ancestor is UIElement element && (element.Visibility != Visibility.Visible || element.Opacity <= 0))
                {
                    visible = false;
                    break;
                }
            }

            // Reserve the existing title and control rows. HWND video must not obscure XAML controls.
            double top = Math.Min(52, ActualHeight);
            double bottom = Math.Min(84, Math.Max(0, ActualHeight - top));
            int x = (int)Math.Round(position.X * scale);
            int y = (int)Math.Round((position.Y + top) * scale);
            int width = Math.Max(0, (int)Math.Round(ActualWidth * scale));
            int height = Math.Max(0, (int)Math.Round((ActualHeight - top - bottom) * scale));
            var holes = new JsonArray();
            foreach (var popup in VisualTreeHelper.GetOpenPopups(Window.Current))
            {
                if (!popup.IsOpen || popup.Child is not FrameworkElement child) continue;
                try
                {
                    Point point = child.TransformToVisual(root).TransformPoint(new Point());
                    holes.Add((JsonNode)new JsonObject
                    {
                        ["x"] = (int)Math.Floor(point.X * scale) - x - 6,
                        ["y"] = (int)Math.Floor(point.Y * scale) - y - 6,
                        ["width"] = (int)Math.Ceiling(child.ActualWidth * scale) + 12,
                        ["height"] = (int)Math.Ceiling(child.ActualHeight * scale) + 12
                    });
                }
                catch (ArgumentException)
                {
                    // A popup on another XAML root cannot be mapped into this video's coordinates.
                    visible = false;
                }
            }
            string bounds = new JsonObject
            {
                ["x"] = x, ["y"] = y, ["width"] = width, ["height"] = height,
                ["visible"] = visible, ["holes"] = holes
            }.ToJsonString();
            if (bounds == _lastBounds) return;
            _lastBounds = bounds;
            await MpvConnection.Current.SetBoundsAsync(x, y, width, height, visible, holes);
        }
        catch (Exception)
        {
            // Layout teardown can race with a pending renderer request.
        }
        finally
        {
            _updating = false;
        }
    }
}
