using System.Buffers.Binary;
using System.Text;
using Screenbox.Core.Playback;

namespace Screenbox.Core.Tests.Playback;

public partial class DolbyVisionDetectorTests
{
    [Test]
    public async Task ReadConfiguration_Profile5_ShouldReadFlagsAndRestorePosition()
    {
        using var stream = new MemoryStream(Movie(Configuration(5, 6, 5, 0)));
        stream.Position = 3;
        var result = await DolbyVisionDetector.ReadConfigurationAsync(stream);
        await Assert.That(result is { Profile: 5, Level: 6, HasRpu: true,
            HasBaseLayer: true, HasEnhancementLayer: false, BaseLayerCompatibilityId: 0 }).IsTrue();
        await Assert.That(result is { IsProfile5SingleLayer: true }).IsTrue();
        await Assert.That(stream.Position).IsEqualTo(3L);
    }

    [Test]
    public async Task ReadConfiguration_Profile8_ShouldNotSelectProfile5Processing()
    {
        using var stream = new MemoryStream(Movie(Configuration(8, 9, 5, 1), "hvc1"));
        var result = await DolbyVisionDetector.ReadConfigurationAsync(stream);
        await Assert.That(result is { Profile: 8, Level: 9, BaseLayerCompatibilityId: 1 }).IsTrue();
        await Assert.That(result is { IsProfile5SingleLayer: false }).IsTrue();
    }

    [Test]
    public async Task ReadConfiguration_OrdinaryHevc_ShouldNotInferDolbyFromCodec()
    {
        using var stream = new MemoryStream(Movie([], "hvc1"));
        await Assert.That(await DolbyVisionDetector.ReadConfigurationAsync(stream)).IsNull();
    }

    [Test]
    public async Task ReadConfiguration_FakeMetadataInsideMediaPayload_ShouldBeIgnored()
    {
        using var stream = new MemoryStream(Box("mdat", Movie(Configuration(5, 6, 5, 0))));
        await Assert.That(await DolbyVisionDetector.ReadConfigurationAsync(stream)).IsNull();
    }

    [Test]
    public async Task ReadConfiguration_ExtendedSizeMovie_ShouldReadConfiguration()
    {
        byte[] movie = Movie(Configuration(5, 6, 5, 0));
        byte[] large = new byte[movie.Length + 8];
        BinaryPrimitives.WriteUInt32BigEndian(large, 1);
        "moov"u8.CopyTo(large.AsSpan(4));
        BinaryPrimitives.WriteUInt64BigEndian(large.AsSpan(8), (ulong)large.Length);
        movie.AsSpan(8).CopyTo(large.AsSpan(16));
        using var stream = new MemoryStream(large);
        await Assert.That((await DolbyVisionDetector.ReadConfigurationAsync(stream))?.Profile).IsEqualTo(5);
    }

    [Test]
    public async Task ReadConfiguration_PartialReads_ShouldReadCompleteRecord()
    {
        using var stream = new ShortReadStream(Movie(Configuration(5, 6, 5, 0)));
        await Assert.That((await DolbyVisionDetector.ReadConfigurationAsync(stream))?.Profile).IsEqualTo(5);
    }

    [Test]
    public async Task ReadConfiguration_BoxOutsideParent_ShouldReturnNull()
    {
        byte[] movie = Movie(Configuration(5, 6, 5, 0));
        BinaryPrimitives.WriteUInt32BigEndian(movie.AsSpan(8), uint.MaxValue);
        using var stream = new MemoryStream(movie);
        await Assert.That(await DolbyVisionDetector.ReadConfigurationAsync(stream)).IsNull();
    }

    [Test]
    public async Task ReadConfiguration_TruncatedRecord_ShouldReturnNull()
    {
        using var stream = new MemoryStream(Movie(Configuration(5, 6, 5, 0)[..4]));
        await Assert.That(await DolbyVisionDetector.ReadConfigurationAsync(stream)).IsNull();
    }

    [Test]
    public async Task ReadConfiguration_UnsupportedVersion_ShouldReturnNull()
    {
        byte[] config = Configuration(5, 6, 5, 0);
        config[0] = 2;
        using var stream = new MemoryStream(Movie(config));
        await Assert.That(await DolbyVisionDetector.ReadConfigurationAsync(stream)).IsNull();
    }

    [Test]
    public async Task ReadConfiguration_Cancellation_ShouldRestorePosition()
    {
        using var stream = new MemoryStream(Movie(Configuration(5, 6, 5, 0)));
        stream.Position = 5;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool canceled = false;
        try
        {
            await DolbyVisionDetector.ReadConfigurationAsync(stream, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }

        await Assert.That(canceled).IsTrue();
        await Assert.That(stream.Position).IsEqualTo(5L);
    }

    [Test]
    public async Task ReadConfiguration_LargeMediaPayload_ShouldSeekPastIt()
    {
        byte[] movie = Movie(Configuration(5, 6, 5, 0));
        using var stream = new SparseMediaStream(movie);
        var result = await DolbyVisionDetector.ReadConfigurationAsync(stream);
        await Assert.That(result?.Profile).IsEqualTo(5);
        await Assert.That(stream.BytesRead < 1024).IsTrue();
    }

    private static byte[] Configuration(int profile, int level, int flags, int compatibility)
    {
        byte[] record = new byte[24];
        record[0] = 1;
        record[2] = (byte)((profile << 1) | (level >> 5));
        record[3] = (byte)((level << 3) | flags);
        record[4] = (byte)(compatibility << 4);
        return record;
    }

    private static byte[] Movie(byte[] configuration, string codec = "dvhe")
    {
        byte[] visual = Box(codec, [.. new byte[78],
            .. (configuration.Length > 0 ? Box("dvcC", configuration) : Array.Empty<byte>())]);
        byte[] stsd = Box("stsd", [0, 0, 0, 0, 0, 0, 0, 1, .. visual]);
        return Box("moov", Box("trak", Box("mdia", Box("minf", Box("stbl", stsd)))));
    }

    private static byte[] Box(string type, byte[] payload)
    {
        byte[] data = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(data, (uint)data.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(data, 4);
        payload.CopyTo(data, 8);
        return data;
    }

    private sealed partial class ShortReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], token);
    }

    private sealed partial class SparseMediaStream(byte[] movie) : Stream
    {
        private const long MediaSize = 4L * 1024 * 1024 * 1024;
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => MediaSize + movie.Length;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            byte[] header = new byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(header, 1);
            "mdat"u8.CopyTo(header.AsSpan(4));
            BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(8), (ulong)MediaSize);
            int read = 0;
            while (read < count && Position < Length)
            {
                if (Position >= 16 && Position < MediaSize)
                {
                    throw new InvalidOperationException("Media payload must not be read.");
                }

                buffer[offset + read++] = Position < 16
                    ? header[(int)Position] : movie[(int)(Position - MediaSize)];
                Position++;
            }

            BytesRead += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            byte[] bytes = new byte[buffer.Length];
            int read = Read(bytes, 0, bytes.Length);
            bytes.AsMemory(0, read).CopyTo(buffer);
            return ValueTask.FromResult(read);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            return Position;
        }

        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
