using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Screenbox.Core.Playback;

/// <summary>Owns cancellable subtitle reads and keeps late loads from replacing a new selection.</summary>
public sealed partial class ExternalSubtitleSession : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, Task<SubtitleDocument>> _loads = new(StringComparer.OrdinalIgnoreCase);
    private string? _selected;
    private bool _disposed;

    /// <summary>Changes selection immediately, including while a file is still loading.</summary>
    public void Select(string? key)
    {
        lock (_gate) { if (!_disposed) _selected = key; }
    }

    /// <summary>Shares duplicate requests and performs all file reading and parsing on a worker.</summary>
    public Task<SubtitleDocument> LoadAsync(string key, string extension, Func<CancellationToken, Task<Stream>> open)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loads.TryGetValue(key, out Task<SubtitleDocument>? existing) && !existing.IsFaulted && !existing.IsCanceled) return existing;
            CancellationToken lifetime = _lifetime.Token;
            Task<SubtitleDocument> load = Task.Run(async () =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using Stream input = await open(timeout.Token).ConfigureAwait(false);
                return await SubtitleDocument.ReadAsync(input, extension, timeout.Token).ConfigureAwait(false);
            }, lifetime);
            _loads[key] = load;
            return load;
        }
    }

    /// <summary>Looks up the selected caption at the current clock position, also after seeks.</summary>
    public string GetText(TimeSpan position, TimeSpan delay)
    {
        SubtitleDocument? document;
        lock (_gate)
        {
            if (_disposed || _selected is null || !_loads.TryGetValue(_selected, out var load) || !load.IsCompletedSuccessfully) return string.Empty;
            document = load.Result;
        }

        return document.GetText(position, delay);
    }

    /// <summary>Cancels in-flight reads and discards the captions of the previous video.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _selected = null;
            _loads.Clear();
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

