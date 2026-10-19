using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Screenbox.Core.Playback;

/// <summary>Indexes external text captions independently of the audio/video playback graph.</summary>
public sealed class SubtitleDocument
{
    /// <summary>The maximum accepted subtitle file size; media payloads are never buffered here.</summary>
    public const int MaximumBytes = 32 * 1024 * 1024;
    private const int MaximumCues = 200_000;
    private readonly Cue[] _cues;
    private readonly long[] _maximumEnd;
    private sealed record Cue(long Start, long End, string Text);

    /// <summary>Gets the number of valid captions.</summary>
    public int Count => _cues.Length;

    private SubtitleDocument(List<Cue> cues)
    {
        _cues = cues.OrderBy(cue => cue.Start).ToArray();
        _maximumEnd = new long[Math.Max(1, _cues.Length * 4)];
        if (_cues.Length > 0) BuildIndex(1, 0, _cues.Length - 1);
    }

    /// <summary>Identifies the text formats that can be loaded without reconfiguring video.</summary>
    public static bool Supports(string extension) => extension.ToLowerInvariant() is ".srt" or ".vtt" or ".ass" or ".ssa";

    /// <summary>Reads a bounded subtitle stream and supports UTF-8, Unicode and legacy Chinese text.</summary>
    public static async Task<SubtitleDocument> ReadAsync(Stream input, string extension, CancellationToken cancellationToken = default)
    {
        using var data = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (data.Length + count > MaximumBytes) throw new InvalidDataException("The subtitle file exceeds 32 MB.");
            data.Write(buffer, 0, count);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Parse(Decode(data.ToArray()), extension, cancellationToken);
    }

    /// <summary>Parses SRT, WebVTT or ASS/SSA dialogue; malformed rows are skipped.</summary>
    public static SubtitleDocument Parse(string text, string extension, CancellationToken cancellationToken = default)
    {
        if (!Supports(extension)) throw new NotSupportedException("This is not a supported text subtitle format.");
        string[] lines = text.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var cues = new List<Cue>();
        bool ass = extension.Equals(".ass", StringComparison.OrdinalIgnoreCase) || extension.Equals(".ssa", StringComparison.OrdinalIgnoreCase);
        int startField = 1, endField = 2, textField = 9, fieldCount = 10;
        bool events = false;
        for (int i = 0; i < lines.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string line = lines[i].Trim();
            if (ass)
            {
                if (line.StartsWith('[')) { events = line.Equals("[Events]", StringComparison.OrdinalIgnoreCase); continue; }
                if (!events) continue;
                if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
                {
                    string[] fields = line[7..].Split(',', StringSplitOptions.TrimEntries);
                    startField = Array.FindIndex(fields, x => x.Equals("Start", StringComparison.OrdinalIgnoreCase));
                    endField = Array.FindIndex(fields, x => x.Equals("End", StringComparison.OrdinalIgnoreCase));
                    textField = Array.FindIndex(fields, x => x.Equals("Text", StringComparison.OrdinalIgnoreCase));
                    fieldCount = fields.Length;
                    continue;
                }

                if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase) || startField < 0 || endField < 0 || textField < 0) continue;
                string[] values = line[9..].Split(',', fieldCount);
                if (values.Length < fieldCount) continue;
                if (TryTime(values[startField], out long start) && TryTime(values[endField], out long end))
                    Add(cues, start, end, Clean(values[textField], true));
            }
            else
            {
                int arrow = line.IndexOf("-->", StringComparison.Ordinal);
                if (arrow < 0) continue;
                string ending = line[(arrow + 3)..].TrimStart();
                int setting = ending.IndexOfAny([' ', '\t']);
                if (setting >= 0) ending = ending[..setting];
                if (!TryTime(line[..arrow], out long start) || !TryTime(ending, out long end)) continue;
                var caption = new StringBuilder();
                while (i + 1 < lines.Length && !string.IsNullOrWhiteSpace(lines[i + 1]))
                {
                    if (caption.Length > 0) caption.Append('\n');
                    caption.Append(lines[++i]);
                }

                Add(cues, start, end, Clean(caption.ToString(), false));
            }
        }

        if (cues.Count == 0) throw new InvalidDataException("No valid timed captions were found in this file.");
        return new SubtitleDocument(cues);
    }

    /// <summary>Returns active captions after applying delay; positive values show subtitles later.</summary>
    public string GetText(TimeSpan position, TimeSpan delay)
    {
        long time = position.Ticks - delay.Ticks;
        if (time < 0 || _cues.Length == 0) return string.Empty;
        var active = new List<string>(2);
        Collect(1, 0, _cues.Length - 1, time, active);
        return active.Count switch { 0 => string.Empty, 1 => active[0], _ => string.Join("\n", active) };
    }

    private long BuildIndex(int node, int left, int right)
    {
        if (left == right) return _maximumEnd[node] = _cues[left].End;
        int middle = (left + right) / 2;
        return _maximumEnd[node] = Math.Max(BuildIndex(node * 2, left, middle), BuildIndex(node * 2 + 1, middle + 1, right));
    }

    private void Collect(int node, int left, int right, long time, List<string> active)
    {
        if (active.Count >= 16 || _maximumEnd[node] <= time || _cues[left].Start > time) return;
        if (left == right) { active.Add(_cues[left].Text); return; }
        int middle = (left + right) / 2;
        Collect(node * 2, left, middle, time, active);
        Collect(node * 2 + 1, middle + 1, right, time, active);
    }

    private static void Add(List<Cue> cues, long start, long end, string text)
    {
        if (start < 0 || end <= start || string.IsNullOrWhiteSpace(text)) return;
        if (cues.Count >= MaximumCues) throw new InvalidDataException("The subtitle contains too many captions.");
        cues.Add(new Cue(start, end, text.Length > 8192 ? text[..8192] : text));
    }

    private static bool TryTime(string value, out long ticks)
    {
        ticks = 0;
        string[] parts = value.Trim().Replace(',', '.').Split(':');
        if (parts.Length is not (2 or 3)) return false;
        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long first) || first < 0) return false;
        long hours = parts.Length == 3 ? first : 0;
        long minutes = first;
        if (parts.Length == 3 && !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minutes)) return false;
        if (minutes is < 0 or >= 60 || hours > 24 * 30) return false;
        if (!decimal.TryParse(parts[^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal seconds) || seconds is < 0 or >= 60) return false;
        ticks = (hours * 3600 + minutes * 60) * TimeSpan.TicksPerSecond + (long)(seconds * TimeSpan.TicksPerSecond);
        return true;
    }

    private static string Clean(string text, bool ass)
    {
        var result = new StringBuilder(text.Length);
        bool drawing = false;
        for (int i = 0; i < text.Length; i++)
        {
            char value = text[i];
            if (ass && value == '{')
            {
                int end = text.IndexOf('}', i + 1);
                if (end >= 0)
                {
                    string commands = text[(i + 1)..end];
                    if (commands.Contains("\\p0", StringComparison.Ordinal)) drawing = false;
                    else if (commands.Contains("\\p1", StringComparison.Ordinal)) drawing = true;
                    i = end;
                    continue;
                }
            }

            if (!ass && value == '<')
            {
                int end = text.IndexOf('>', i + 1);
                if (end >= 0)
                {
                    if (text.AsSpan(i, end - i + 1).StartsWith("<br", StringComparison.OrdinalIgnoreCase)) result.Append('\n');
                    i = end;
                    continue;
                }
            }

            if (drawing) continue;
            if (ass && value == '\\' && i + 1 < text.Length && text[i + 1] is 'N' or 'n' or 'h')
            {
                result.Append(text[++i] == 'h' ? ' ' : '\n');
                continue;
            }

            result.Append(value);
        }

        return WebUtility.HtmlDecode(result.ToString()).Trim();
    }

    private static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xfe && bytes[2] == 0 && bytes[3] == 0)
            return Encoding.UTF32.GetString(bytes);
        if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe) return Encoding.Unicode.GetString(bytes);
        if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff) return Encoding.BigEndianUnicode.GetString(bytes);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(54936).GetString(bytes);
        }
    }
}

