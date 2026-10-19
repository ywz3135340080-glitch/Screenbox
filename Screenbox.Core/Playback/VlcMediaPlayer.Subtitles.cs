using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.AccessCache;

namespace Screenbox.Core.Playback;

public sealed partial class VlcMediaPlayer
{
    /// <summary>Raised when the external text caption displayed over the video changes.</summary>
    public event EventHandler? SubtitleTextChanged;

    /// <summary>Gets the caption rendered independently from the media decoding graph.</summary>
    public string SubtitleText => Volatile.Read(ref _subtitleText);

    private readonly object _subtitleRequestLock = new();
    private readonly object _nativeSubtitleLock = new();
    private readonly Dictionary<string, Task> _subtitleRequests = new(StringComparer.OrdinalIgnoreCase);
    private ExternalSubtitleSession _textSubtitleSession = new();
    private Timer? _subtitleTimer;
    private Task _subtitleRoute = Task.CompletedTask;
    private string _subtitleText = string.Empty;
    private double _subtitleDelay;
    private int _subtitleTickActive;

    private static string SubtitleKey(IStorageFile file) => string.IsNullOrEmpty(file.Path) ? file.Name : file.Path;

    /// <summary>Loads a subtitle asynchronously; duplicate selections share their pending request.</summary>
    public Task LoadSubtitleAsync(IStorageFile file, bool select = true)
    {
        PlaybackItem? item = PlaybackItem;
        if (item is null || _closed) return Task.CompletedTask;
        int generation = Volatile.Read(ref _colorGeneration);
        string key = generation + ":" + SubtitleKey(file) + ":" + select;
        lock (_subtitleRequestLock)
        {
            if (_subtitleRequests.TryGetValue(key, out Task? existing) && !existing.IsFaulted && !existing.IsCanceled) return existing;
            Task route = _subtitleRoute;
            ExternalSubtitleSession session = _textSubtitleSession;
            Task request = Task.Run(async () =>
            {
                await route.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                if (_closed || PlaybackItem != item || generation != Volatile.Read(ref _colorGeneration)) return;
                if (SubtitleDocument.Supports(file.FileType))
                {
                    await session.LoadAsync(SubtitleKey(file), file.FileType, async token =>
                    {
                        var input = await file.OpenAsync(FileAccessMode.Read).AsTask(token).ConfigureAwait(false);
                        return input.AsStreamForRead();
                    }).WaitAsync(TimeSpan.FromSeconds(12)).ConfigureAwait(false);
                    if (!ReferenceEquals(session, _textSubtitleSession) || PlaybackItem != item || generation != Volatile.Read(ref _colorGeneration)) return;
                    EnsureSubtitleTimer();
                }
                else if (_colorPlayer is not null)
                {
                    await AddColorSubtitleAsync(file, select).ConfigureAwait(false);
                }
                else
                {
                    string mrl = "winrt://" + StorageApplicationPermissions.FutureAccessList.Add(file, "subtitle");
                    lock (_nativeSubtitleLock)
                    {
                        if (_closed || PlaybackItem != item || generation != Volatile.Read(ref _colorGeneration)) return;
                        if (!VlcPlayer.AddSlave(LibVLCSharp.Shared.MediaSlaveType.Subtitle, mrl, select))
                            throw new IOException("The subtitle could not be loaded.");
                    }
                }
            });
            _subtitleRequests[key] = request;
            return request;
        }
    }

    private async Task ObserveSubtitleLoadAsync(IStorageFile file, bool select)
    {
        try { await LoadSubtitleAsync(file, select).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception error) { LogColorFailure(error); }
    }

    private void ResetExternalSubtitles()
    {
        ExternalSubtitleSession previous;
        lock (_subtitleRequestLock)
        {
            previous = _textSubtitleSession;
            _textSubtitleSession = new ExternalSubtitleSession();
            _subtitleRequests.Clear();
            _subtitleTimer?.Dispose();
            _subtitleTimer = null;
            SetSubtitleText(string.Empty);
        }
        previous.Dispose();
    }

    private void SelectExternalTextSubtitle(IStorageFile? file)
    {
        _textSubtitleSession.Select(file is not null && SubtitleDocument.Supports(file.FileType) ? SubtitleKey(file) : null);
        SetSubtitleText(string.Empty);
        EnsureSubtitleTimer();
    }

    private void EnsureSubtitleTimer()
    {
        lock (_subtitleRequestLock)
        {
            if (_closed) return;
            _subtitleTimer ??= new Timer(_ => RefreshSubtitleText(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(50));
            _subtitleTimer.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(50));
        }
    }

    private void RefreshSubtitleText()
    {
        if (Interlocked.Exchange(ref _subtitleTickActive, 1) != 0) return;
        try
        {
            if (_closed) return;
            TimeSpan position;
            if (_colorPlayer is { } player) position = player.PlaybackSession.Position;
            else
            {
                lock (_nativeSubtitleLock)
                {
                    if (_closed) return;
                    position = TimeSpan.FromMilliseconds(Math.Max(0, VlcPlayer.Time));
                }
            }

            ExternalSubtitleSession session = _textSubtitleSession;
            string text = session.GetText(position, TimeSpan.FromMilliseconds(Volatile.Read(ref _subtitleDelay)));
            lock (_subtitleRequestLock)
            {
                if (ReferenceEquals(session, _textSubtitleSession)) SetSubtitleText(text);
            }
        }
        catch (Exception)
        {
            // A closing native player can reject the final timer tick.
        }
        finally { Volatile.Write(ref _subtitleTickActive, 0); }
    }

    private void SetSubtitleText(string value)
    {
        if (Interlocked.Exchange(ref _subtitleText, value) != value) SubtitleTextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyNativeSubtitleDelay()
    {
        _ = Task.Run(() =>
        {
            lock (_nativeSubtitleLock)
            {
                if (!_closed) VlcPlayer.SetSpuDelay(ConvertMillisecondsToMicroseconds(Volatile.Read(ref _subtitleDelay)));
            }
        });
    }
}

