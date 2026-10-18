using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Windows.ApplicationModel;
using Windows.ApplicationModel.AppService;
using Windows.Foundation.Collections;

namespace Screenbox.MpvBridge;

/// <summary>Runs mpv in a child window and forwards playback state to the packaged Screenbox UI.</summary>
internal sealed class Program : IDisposable
{
    private readonly AppServiceConnection _connection = new()
    {
        AppServiceName = "Screenbox.Mpv",
        PackageFamilyName = Package.Current.Id.FamilyName
    };
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _writeGate = new(1);
    private readonly SemaphoreSlim _sendGate = new(1);
    private readonly object _stateLock = new();
    private JsonObject _state = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _requests = new();
    private long _nextRequest;
    private NativeVideoWindow? _window;
    private Process? _process;
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test")
        {
            try { await NativeVideoWindow.RunSelfTestAsync(); return 0; }
            catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        }
        using var program = new Program();
        await program.RunAsync();
        return 0;
    }

    private async Task RunAsync()
    {
        _connection.RequestReceived += OnRequestReceived;
        _connection.ServiceClosed += (_, _) => _shutdown.Cancel();
        if (await _connection.OpenAsync() != AppServiceConnectionStatus.Success) return;
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                await Task.Delay(150, _shutdown.Token);
                if (_window is not null && !_window.ParentExists) break;
                JsonObject snapshot;
                lock (_stateLock)
                {
                    snapshot = _state;
                    _state = new JsonObject();
                }
                if (snapshot.Count > 0) await SendEventAsync("state", snapshot);
            }
        }
        catch (OperationCanceledException)
        {
            // Closing Screenbox also closes this worker and its owned mpv process.
        }
    }

    private async void OnRequestReceived(AppServiceConnection sender, AppServiceRequestReceivedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            if (args.Request.Message["json"] is not string raw) throw new InvalidDataException("Missing bridge request.");
            var request = JsonNode.Parse(raw)?.AsObject() ?? throw new InvalidDataException("Invalid bridge request.");
            switch (request["kind"]?.GetValue<string>())
            {
                case "attach":
                    await AttachAsync(request);
                    break;
                case "bounds":
                    _window?.SetBounds(Number(request, "x"), Number(request, "y"), Number(request, "width"), Number(request, "height"),
                        request["visible"]?.GetValue<bool>() ?? false, request["holes"] as JsonArray);
                    break;
                case "command":
                    await ExecuteCommandAsync(request["payload"]?.AsObject() ?? throw new InvalidDataException("Missing mpv command."));
                    break;
                case "close":
                    _shutdown.Cancel();
                    break;
                default:
                    throw new InvalidDataException("Unsupported bridge request.");
            }
            await args.Request.SendResponseAsync(new ValueSet { ["ok"] = true });
        }
        catch (Exception exception)
        {
            await args.Request.SendResponseAsync(new ValueSet { ["ok"] = false, ["error"] = exception.Message });
            await SendEventAsync("error", JsonValue.Create(exception.Message)!);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static int Number(JsonObject node, string name) => node[name]?.GetValue<int>() ?? 0;

    private async Task AttachAsync(JsonObject request)
    {
        if (_process is not null) return;
        string executable = request["executable"]?.GetValue<string>() ?? "";
        if (!File.Exists(executable) || !Path.GetFileName(executable).Equals("mpv.exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("Select an installed mpv.exe to use this preview.", executable);
        int processId = Number(request, "processId");
        using var parent = Process.GetProcessById(processId);
        string parentPath = parent.MainModule?.FileName ?? "";
        string packagePath = Package.Current.InstalledLocation.Path + Path.DirectorySeparatorChar;
        if (!parentPath.StartsWith(packagePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Video can only be embedded in this Screenbox package.");
        _window = new NativeVideoWindow(processId);
        nint window = await _window.Handle.WaitAsync(TimeSpan.FromSeconds(10));
        string pipeName = "LOCAL\\screenbox-mpv-" + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        string[] options =
        [
            "--idle=yes", "--keep-open=no", "--force-window=yes",
            "--vo=gpu-next", "--osc=no", "--load-scripts=no", "--terminal=no",
            "--input-default-bindings=no", "--input-vo-keyboard=no", "--input-cursor=no",
            "--wid=" + unchecked((uint)window).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--input-ipc-server=\\\\.\\pipe\\" + pipeName
        ];
        foreach (string option in options) start.ArgumentList.Add(option);
        _process = Process.Start(start) ?? throw new InvalidOperationException("mpv did not start.");
        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await _pipe.ConnectAsync(15000, _shutdown.Token);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        _ = ReadEventsAsync();
        string[] properties =
        [
            "time-pos", "duration", "pause", "volume", "mute", "speed", "seekable", "video-params",
            "track-list", "aid", "vid", "sid", "chapter-list", "chapter", "paused-for-cache", "demuxer-cache-state"
        ];
        for (int i = 0; i < properties.Length; i++)
            await WriteCommandAsync(new JsonObject { ["command"] = new JsonArray("observe_property", i + 1, properties[i]) });
    }

    private async Task WriteCommandAsync(JsonObject command)
    {
        if (_writer is null) throw new InvalidOperationException("mpv is not ready.");
        await _writeGate.WaitAsync(_shutdown.Token);
        try { await _writer.WriteLineAsync(command.ToJsonString().AsMemory(), _shutdown.Token); }
        finally { _writeGate.Release(); }
    }

    private async Task ExecuteCommandAsync(JsonObject command)
    {
        long id = Interlocked.Increment(ref _nextRequest);
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests[id] = completion;
        command["request_id"] = id;
        try
        {
            await WriteCommandAsync(command);
            JsonObject response = await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), _shutdown.Token);
            string? error = response["error"]?.GetValue<string>();
            if (error != "success") throw new InvalidOperationException("mpv: " + error);
        }
        finally
        {
            _requests.TryRemove(id, out _);
        }
    }

    private async Task ReadEventsAsync()
    {
        try
        {
            using var reader = new StreamReader(_pipe!, Encoding.UTF8, false, 4096, leaveOpen: true);
            while (!_shutdown.IsCancellationRequested && await reader.ReadLineAsync(_shutdown.Token) is { } line)
            {
                var message = JsonNode.Parse(line)?.AsObject();
                if (message?["request_id"] is { } requestId &&
                    _requests.TryRemove(requestId.GetValue<long>(), out var completion))
                {
                    completion.TrySetResult(message);
                    continue;
                }
                string? kind = message?["event"]?.GetValue<string>();
                if (kind == "property-change" && message!["name"]?.GetValue<string>() is { } property)
                {
                    lock (_stateLock) _state[property] = message["data"]?.DeepClone();
                }
                else if (kind is "start-file" or "file-loaded" or "end-file" or "shutdown")
                {
                    await SendEventAsync(kind, message!);
                }
            }
            if (!_shutdown.IsCancellationRequested)
                await SendEventAsync("error", JsonValue.Create("The mpv connection closed.")!);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or System.Text.Json.JsonException)
        {
            if (!_shutdown.IsCancellationRequested) await SendEventAsync("error", JsonValue.Create(exception.Message)!);
        }
    }

    private async Task SendEventAsync(string kind, JsonNode data)
    {
        try
        {
            await _sendGate.WaitAsync();
            try
            {
                var json = new JsonObject { ["kind"] = kind, ["data"] = data }.ToJsonString();
                await _connection.SendMessageAsync(new ValueSet { ["json"] = json });
            }
            finally { _sendGate.Release(); }
        }
        catch (Exception)
        {
            _shutdown.Cancel();
        }
    }

    /// <summary>Releases the worker, pipe and only the mpv process started by this instance.</summary>
    public void Dispose()
    {
        _shutdown.Cancel();
        _writer?.Dispose();
        _pipe?.Dispose();
        if (_process is not null)
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            _process.Dispose();
        }
        _window?.Dispose();
        _connection.Dispose();
        _shutdown.Dispose();
    }
}
