using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace Screenbox.Core.Playback;

/// <summary>Owns a selected native decode stream and its StorageFile input.</summary>
internal sealed partial class ColorDecoder : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct StreamInfo
    {
        internal int Width, Height, SampleRate, Channels, FpsNumerator, FpsDenominator;
        internal long DurationTicks;
        internal uint ChannelMask;
        internal int Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Sample
    {
        internal IntPtr Data;
        internal int Length, Reserved;
        internal long TimestampTicks, DurationTicks;
    }

    private readonly Stream _input;
    private GCHandle _inputHandle;
    private IntPtr _decoder;
    private Exception? _inputError;
    internal StreamInfo Info { get; private set; }

    private ColorDecoder(Stream input)
    {
        _input = input;
        _inputHandle = GCHandle.Alloc(this);
    }

    /// <summary>Opens an independent StorageFile stream; null means the requested track is absent.</summary>
    internal static async Task<ColorDecoder?> OpenAsync(IStorageFile file, bool audio)
    {
        return Open(await file.OpenStreamForReadAsync().ConfigureAwait(false), audio);
    }

    /// <summary>Takes ownership of an already opened, independent seekable input.</summary>
    internal static ColorDecoder? Open(Stream input, bool audio)
    {
        ColorDecoder? owner = null;
        try
        {
            owner = new ColorDecoder(input);
            int result = owner.Open(audio);
            if (result == 1)
            {
                owner.Dispose();
                return null;
            }

            owner.Check(result);
            return owner;
        }
        catch
        {
            if (owner is null) input.Dispose();
            else owner.Dispose();
            throw;
        }
    }

    private unsafe int Open(bool audio)
    {
        if (AbiVersion() != 1) throw new InvalidOperationException("Unexpected native decoder ABI.");
        int result = OpenNative(&Read, &SeekInput, GCHandle.ToIntPtr(_inputHandle),
            audio ? 1 : 0, out _decoder, out StreamInfo info);
        Info = info;
        return result;
    }

    internal Sample? Next()
    {
        int result = NextNative(_decoder, out Sample sample);
        Check(result);
        return result == 0 ? null : sample;
    }

    internal void Seek(TimeSpan position)
    {
        Check(SeekNative(_decoder, position.Ticks));
    }

    private void Check(int result)
    {
        if (result < 0)
        {
            throw new IOException($"Native media source error ({result}).", _inputError);
        }
    }

    public void Dispose()
    {
        if (_decoder != IntPtr.Zero)
        {
            CloseNative(_decoder);
            _decoder = IntPtr.Zero;
        }

        if (_inputHandle.IsAllocated) _inputHandle.Free();
        _input.Dispose();
    }

    // No managed exception may cross the native callback boundary.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int Read(IntPtr user, byte* data, int length)
    {
        ColorDecoder owner = (ColorDecoder)GCHandle.FromIntPtr(user).Target!;
        try
        {
            return owner._input.Read(new Span<byte>(data, length));
        }
        catch (Exception error)
        {
            owner._inputError = error;
            return -1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static long SeekInput(IntPtr user, long offset, int origin)
    {
        ColorDecoder owner = (ColorDecoder)GCHandle.FromIntPtr(user).Target!;
        try
        {
            if ((origin & 0x10000) != 0) return owner._input.Length;
            origin &= ~0x20000;
            return origin is >= 0 and <= 2 ? owner._input.Seek(offset, (SeekOrigin)origin) : -1;
        }
        catch (Exception error)
        {
            owner._inputError = error;
            return -1;
        }
    }

    [LibraryImport("Screenbox.NativeColor.dll", EntryPoint = "sb_abi_version")]
    private static partial int AbiVersion();
    [LibraryImport("Screenbox.NativeColor.dll", EntryPoint = "sb_open")]
    private static unsafe partial int OpenNative(
        delegate* unmanaged[Cdecl]<IntPtr, byte*, int, int> read,
        delegate* unmanaged[Cdecl]<IntPtr, long, int, long> seek,
        IntPtr user, int kind, out IntPtr decoder, out StreamInfo info);
    [LibraryImport("Screenbox.NativeColor.dll", EntryPoint = "sb_next")]
    private static partial int NextNative(IntPtr decoder, out Sample sample);
    [LibraryImport("Screenbox.NativeColor.dll", EntryPoint = "sb_seek")]
    private static partial int SeekNative(IntPtr decoder, long ticks);
    [LibraryImport("Screenbox.NativeColor.dll", EntryPoint = "sb_close")]
    private static partial void CloseNative(IntPtr decoder);

    [LibraryImport("Screenbox.NativeColor.dll", EntryPoint = "sb_self_test")]
    internal static partial int SelfTest();
}
