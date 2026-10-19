using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Screenbox.Core.Playback;

/// <summary>
/// Reads Dolby Vision configuration from ISO BMFF visual sample descriptions.
/// It does not infer playback support from a container name or codec label.
/// </summary>
public static class DolbyVisionDetector
{
    private const int MaximumBoxes = 16_384;

    /// <summary>
    /// Reads an MP4 configuration without reading media payloads and restores the stream position.
    /// Returns null for non-seekable streams, absent metadata, unsupported versions or malformed boxes.
    /// I/O failures and cancellation propagate to the caller.
    /// </summary>
    public static async Task<DolbyVisionConfiguration?> ReadConfigurationAsync(
        Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            return null;
        }

        long originalPosition = stream.Position;
        try
        {
            var scanner = new Scanner(stream, cancellationToken);
            return await scanner.ScanAsync(0, stream.Length, Container.Root).ConfigureAwait(false);
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    private enum Container
    {
        Root,
        Movie,
        Track,
        Media,
        MediaInformation,
        SampleTable,
        SampleDescriptions,
        VisualSample
    }

    private readonly record struct Box(uint Type, long Payload, long End);

    // Share a budget across all levels so crafted files cannot cause an unbounded box walk.
    private sealed class Scanner(Stream stream, CancellationToken cancellationToken)
    {
        private int _remainingBoxes = MaximumBoxes;
        private readonly byte[] _header = new byte[16];

        public async Task<DolbyVisionConfiguration?> ScanAsync(
            long start, long end, Container container, uint maximumEntries = uint.MaxValue)
        {
            long position = start;
            while (position <= end - 8 && maximumEntries-- > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (--_remainingBoxes < 0)
                {
                    return null;
                }

                Box? candidate = await ReadBoxAsync(position, end).ConfigureAwait(false);
                if (candidate is not { } box)
                {
                    return null;
                }

                DolbyVisionConfiguration? result = null;
                Container? child = GetChildContainer(container, box.Type);
                if (child is { } childContainer)
                {
                    result = await ScanAsync(box.Payload, box.End, childContainer).ConfigureAwait(false);
                }
                else if (container == Container.SampleTable && box.Type == FourCc("stsd"))
                {
                    if (box.End - box.Payload < 8)
                    {
                        return null;
                    }

                    // Respect the FullBox version and declared count instead of scanning trailing bytes.
                    stream.Position = box.Payload;
                    if (!await ReadExactAsync(_header.AsMemory(0, 8)).ConfigureAwait(false) ||
                        _header[0] != 0)
                    {
                        return null;
                    }

                    uint count = BinaryPrimitives.ReadUInt32BigEndian(_header.AsSpan(4));
                    result = await ScanAsync(box.Payload + 8, box.End, Container.SampleDescriptions, count)
                        .ConfigureAwait(false);
                }
                else if (container == Container.SampleDescriptions && IsVisualSample(box.Type))
                {
                    if (box.End - box.Payload < 78)
                    {
                        return null;
                    }

                    result = await ScanAsync(box.Payload + 78, box.End, Container.VisualSample)
                        .ConfigureAwait(false);
                }
                else if (container == Container.VisualSample &&
                         (box.Type == FourCc("dvcC") || box.Type == FourCc("dvvC")))
                {
                    if (box.End - box.Payload < 24)
                    {
                        return null;
                    }

                    byte[] data = new byte[24];
                    stream.Position = box.Payload;
                    if (!await ReadExactAsync(data).ConfigureAwait(false) || data[0] != 1)
                    {
                        return null;
                    }

                    result = new DolbyVisionConfiguration(
                        data[2] >> 1,
                        ((data[2] & 1) << 5) | (data[3] >> 3),
                        (data[3] & 4) != 0,
                        (data[3] & 2) != 0,
                        (data[3] & 1) != 0,
                        data[4] >> 4);
                }

                if (result is not null)
                {
                    return result;
                }

                position = box.End;
            }

            return null;
        }

        // Read sizes as unsigned values before converting to signed stream offsets.
        private async Task<Box?> ReadBoxAsync(long position, long parentEnd)
        {
            stream.Position = position;
            if (!await ReadExactAsync(_header.AsMemory(0, 8)).ConfigureAwait(false))
            {
                return null;
            }

            ulong size = BinaryPrimitives.ReadUInt32BigEndian(_header);
            uint type = BinaryPrimitives.ReadUInt32BigEndian(_header.AsSpan(4));
            int headerSize = 8;
            if (size == 1)
            {
                if (parentEnd - position < 16 ||
                    !await ReadExactAsync(_header.AsMemory(8, 8)).ConfigureAwait(false))
                {
                    return null;
                }

                size = BinaryPrimitives.ReadUInt64BigEndian(_header.AsSpan(8));
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = (ulong)(parentEnd - position);
            }

            if (size < (ulong)headerSize || size > (ulong)(parentEnd - position))
            {
                return null;
            }

            return new Box(type, position + headerSize, position + (long)size);
        }

        // Stream.ReadAsync may return fewer bytes than requested even before EOF.
        private async Task<bool> ReadExactAsync(Memory<byte> buffer)
        {
            int read = 0;
            while (read < buffer.Length)
            {
                int count = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    return false;
                }

                read += count;
            }

            return true;
        }
    }

    private static Container? GetChildContainer(Container parent, uint type)
    {
        return parent switch
        {
            Container.Root when type == FourCc("moov") => Container.Movie,
            Container.Movie when type == FourCc("trak") => Container.Track,
            Container.Track when type == FourCc("mdia") => Container.Media,
            Container.Media when type == FourCc("minf") => Container.MediaInformation,
            Container.MediaInformation when type == FourCc("stbl") => Container.SampleTable,
            _ => null
        };
    }

    private static bool IsVisualSample(uint type)
    {
        return type == FourCc("dvhe") || type == FourCc("dvh1") ||
               type == FourCc("hvc1") || type == FourCc("hev1") ||
               type == FourCc("avc1") || type == FourCc("avc3") ||
               type == FourCc("dvav") || type == FourCc("dva1") ||
               type == FourCc("av01");
    }

    private static uint FourCc(string text)
    {
        return ((uint)text[0] << 24) | ((uint)text[1] << 16) |
               ((uint)text[2] << 8) | text[3];
    }
}

/// <summary>Describes an MP4 Dolby Vision configuration record.</summary>
/// <param name="Profile">Dolby Vision profile.</param>
/// <param name="Level">Dolby Vision level.</param>
/// <param name="HasRpu">Whether the RPU metadata stream is declared.</param>
/// <param name="HasEnhancementLayer">Whether an enhancement layer is declared.</param>
/// <param name="HasBaseLayer">Whether a base layer is declared.</param>
/// <param name="BaseLayerCompatibilityId">Base-layer signal compatibility identifier.</param>
public sealed record DolbyVisionConfiguration(
    int Profile,
    int Level,
    bool HasRpu,
    bool HasEnhancementLayer,
    bool HasBaseLayer,
    int BaseLayerCompatibilityId)
{
    /// <summary>Identifies the single-layer Profile 5 case that needs Dolby Vision color processing.</summary>
    public bool IsProfile5SingleLayer =>
        Profile == 5 && HasRpu && HasBaseLayer && !HasEnhancementLayer &&
        BaseLayerCompatibilityId == 0;
}
