using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.Background;
using Windows.Foundation.Collections;
using Windows.System;

namespace Screenbox.Core.Playback;

/// <summary>Coordinates the packaged UI and the native mpv renderer without routing video through an SDR surface.</summary>
public sealed class MpvConnection
{
    /// <summary>Gets the renderer connection owned by this application instance.</summary>
    public static MpvConnection Current { get; } = new();

    /// <summary>Notifies the player adapter about mpv state and lifecycle changes on the UI thread.</summary>
    public event Action<string, JsonNode?>? EventReceived;

    private readonly TaskCompletionSource<AppServiceConnection> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _attached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _sendGate = new(1);
    private DispatcherQueue? _dispatcher;
    private BackgroundTaskDeferral? _deferral;

    private MpvConnection() { }

    /// <summary>Sets the UI dispatcher before a playback adapter is initialized on a worker thread.</summary>
    public void SetDispatcher(DispatcherQueue dispatcher) => _dispatcher = dispatcher;

    /// <summary>Accepts an app service connection from the worker in the same installed package.</summary>
    public void Accept(AppServiceConnection connection, BackgroundTaskDeferral deferral)
    {
        _deferral = deferral;
        connection.RequestReceived += OnRequestReceived;
        connection.ServiceClosed += (_, _) =>
        {
            _deferral?.Complete();
            _deferral = null;
            Publish("error", JsonValue.Create("The mpv renderer connection closed."));
        };
        _connected.TrySetResult(connection);
    }

    /// <summary>Starts the renderer using an explicitly selected mpv installation.</summary>
    public async Task AttachAsync(string executable, int processId)
    {
        if (_attached.Task.IsCompletedSuccessfully) return;
        try
        {
            await FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync("MpvBridge");
            var connection = await _connected.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await SendRawAsync(connection, new JsonObject
            {
                ["kind"] = "attach", ["executable"] = executable, ["processId"] = processId
            });
            _attached.TrySetResult(true);
        }
        catch (Exception exception)
        {
            _attached.TrySetException(exception);
            Publish("error", JsonValue.Create(exception.Message));
            throw;
        }
    }

    /// <summary>Sends layout changes after renderer startup has completed.</summary>
    public async Task SetBoundsAsync(int x, int y, int width, int height, bool visible, JsonArray? holes = null)
    {
        await _attached.Task;
        await SendRawAsync(await _connected.Task, new JsonObject
        {
            ["kind"] = "bounds", ["x"] = x, ["y"] = y, ["width"] = width,
            ["height"] = height, ["visible"] = visible, ["holes"] = holes
        });
    }

    /// <summary>Queues a structured mpv command, preserving Unicode paths and argument boundaries.</summary>
    public async Task CommandAsync(params string[] arguments)
    {
        await _attached.Task;
        var command = new JsonArray();
        foreach (string argument in arguments) command.Add(argument);
        if (arguments.Length >= 3 && arguments[0] == "set_property")
        {
            if (arguments[1] is "pause" or "mute") command[2] = arguments[2] == "yes";
            else if (arguments[1] is "volume" or "speed" or "audio-delay" or "sub-delay" or "video-aspect-override")
                command[2] = double.Parse(arguments[2], System.Globalization.CultureInfo.InvariantCulture);
        }
        await SendRawAsync(await _connected.Task, new JsonObject
        {
            ["kind"] = "command", ["payload"] = new JsonObject { ["command"] = command }
        });
    }

    private async Task SendRawAsync(AppServiceConnection connection, JsonObject request)
    {
        await _sendGate.WaitAsync();
        try
        {
            AppServiceResponse response = await connection.SendMessageAsync(new ValueSet { ["json"] = request.ToJsonString() });
            if (response.Status != AppServiceResponseStatus.Success ||
                !response.Message.TryGetValue("ok", out object? value) || value is not true)
            {
                string message = response.Message.TryGetValue("error", out object? error) ? error?.ToString() ?? "" : response.Status.ToString();
                throw new InvalidOperationException(message);
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async void OnRequestReceived(AppServiceConnection sender, AppServiceRequestReceivedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            if (args.Request.Message["json"] is string raw && JsonNode.Parse(raw) is JsonObject message)
                Publish(message["kind"]?.GetValue<string>() ?? "", message["data"]?.DeepClone());
            await args.Request.SendResponseAsync(new ValueSet { ["ok"] = true });
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void Publish(string kind, JsonNode? data)
    {
        _dispatcher?.TryEnqueue(() => EventReceived?.Invoke(kind, data));
    }
}
