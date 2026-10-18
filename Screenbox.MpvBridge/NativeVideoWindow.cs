using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Screenbox.MpvBridge;

/// <summary>Owns the child window used by mpv's native gpu-next renderer.</summary>
internal sealed unsafe partial class NativeVideoWindow : IDisposable
{
    private readonly ConcurrentQueue<Action> _pending = new();
    private readonly TaskCompletionSource<nint> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private readonly nint _parent;
    private uint _threadId;
    private nint _handle;

    internal NativeVideoWindow(int processId)
    {
        _parent = FindCoreWindow(processId);
        if (_parent == 0) throw new InvalidOperationException("The Screenbox video window is unavailable.");
        _thread = new Thread(Run) { IsBackground = true, Name = "Screenbox mpv window" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    internal Task<nint> Handle => _ready.Task;

    internal bool ParentExists => IsWindow(_parent) != 0;

    internal void SetBounds(int x, int y, int width, int height, bool visible)
    {
        _pending.Enqueue(() =>
        {
            SetWindowPos(_handle, 0, x, y, Math.Max(1, width), Math.Max(1, height), 0x0014);
            ShowWindow(_handle, visible && width > 0 && height > 0 ? 4 : 0);
        });
        PostThreadMessageW(_threadId, 0x8001, 0, 0);
    }

    private void Run()
    {
        try
        {
            _threadId = GetCurrentThreadId();
            _handle = CreateWindowExW(0x08000000, "STATIC", "Screenbox mpv video", 0x46000000,
                0, 0, 1, 1, _parent, 0, 0, 0);
            if (_handle == 0) throw new InvalidOperationException("Cannot create the embedded video window.");
            _ready.TrySetResult(_handle);
            while (GetMessageW(out Message message, 0, 0, 0) > 0)
            {
                if (message.Id == 0x8001)
                {
                    while (_pending.TryDequeue(out Action? action)) action();
                }
                else
                {
                    TranslateMessage(in message);
                    DispatchMessageW(in message);
                }
            }
        }
        catch (Exception exception)
        {
            _ready.TrySetException(exception);
        }
        finally
        {
            if (_handle != 0) DestroyWindow(_handle);
        }
    }

    private sealed class WindowSearch(int processId)
    {
        internal int ProcessId { get; } = processId;
        internal nint Found;
    }

    private static nint FindCoreWindow(int processId)
    {
        var search = new WindowSearch(processId);
        GCHandle state = GCHandle.Alloc(search);
        try
        {
            EnumWindows(&VisitTopLevel, GCHandle.ToIntPtr(state));
            return search.Found;
        }
        finally
        {
            state.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int VisitTopLevel(nint window, nint state)
    {
        CheckCandidate(window, state);
        EnumChildWindows(window, &VisitChild, state);
        return ((WindowSearch)GCHandle.FromIntPtr(state).Target!).Found == 0 ? 1 : 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int VisitChild(nint window, nint state)
    {
        CheckCandidate(window, state);
        return ((WindowSearch)GCHandle.FromIntPtr(state).Target!).Found == 0 ? 1 : 0;
    }

    private static void CheckCandidate(nint window, nint state)
    {
        var search = (WindowSearch)GCHandle.FromIntPtr(state).Target!;
        GetWindowThreadProcessId(window, out uint processId);
        if (processId != search.ProcessId) return;
        char* className = stackalloc char[256];
        int length = GetClassNameW(window, className, 256);
        if (new string(className, 0, length) == "Windows.UI.Core.CoreWindow") search.Found = window;
    }

    /// <summary>Closes only the child window owned by this renderer host.</summary>
    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessageW(_threadId, 0x0012, 0, 0);
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        internal nint Window;
        internal uint Id;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal int X;
        internal int Y;
        internal uint Private;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [LibraryImport("user32.dll")] private static partial int GetMessageW(out Message message, nint window, uint min, uint max);
    [LibraryImport("user32.dll")] private static partial int TranslateMessage(in Message message);
    [LibraryImport("user32.dll")] private static partial nint DispatchMessageW(in Message message);
    [LibraryImport("user32.dll")] private static partial int PostThreadMessageW(uint thread, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32.dll")] private static partial int DestroyWindow(nint window);
    [LibraryImport("user32.dll")] private static partial int SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [LibraryImport("user32.dll")] private static partial int ShowWindow(nint window, int command);
    [LibraryImport("user32.dll")] private static partial int IsWindow(nint window);
    [LibraryImport("user32.dll")] private static partial uint GetWindowThreadProcessId(nint window, out uint processId);
    [LibraryImport("user32.dll")] private static partial int GetClassNameW(nint window, char* className, int capacity);
    [LibraryImport("user32.dll")] private static partial int EnumWindows(delegate* unmanaged[Stdcall]<nint, nint, int> callback, nint state);
    [LibraryImport("user32.dll")] private static partial int EnumChildWindows(nint parent, delegate* unmanaged[Stdcall]<nint, nint, int> callback, nint state);
    [LibraryImport("kernel32.dll")] private static partial uint GetCurrentThreadId();
}
