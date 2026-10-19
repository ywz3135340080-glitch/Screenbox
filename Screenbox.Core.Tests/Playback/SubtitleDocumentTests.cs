using System.Text;
using Screenbox.Core.Playback;

namespace Screenbox.Core.Tests.Playback;

public partial class SubtitleDocumentTests
{
    private const string Srt = "1\n00:00:01,000 --> 00:00:03,000\n你好\nHello\n\n2\n00:00:10,000 --> 00:00:12,000\nSecond\n";

    [Test]
    public async Task Timing_PositiveDelay_ShouldShowCaptionLater()
    {
        var document = SubtitleDocument.Parse(Srt, ".srt");
        await Assert.That(document.GetText(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1))).IsEqualTo("");
        await Assert.That(document.GetText(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1))).IsEqualTo("你好\nHello");
        await Assert.That(document.GetText(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(1))).IsEqualTo("");
    }

    [Test]
    public async Task Timing_NegativeDelay_ShouldShowCaptionEarlier()
    {
        var document = SubtitleDocument.Parse(Srt, ".srt");
        await Assert.That(document.GetText(TimeSpan.Zero, TimeSpan.FromSeconds(-1))).IsEqualTo("你好\nHello");
        await Assert.That(document.GetText(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(-1))).IsEqualTo("");
    }

    [Test]
    public async Task Timing_SeekForwardThenBackward_ShouldFindCurrentCaption()
    {
        var document = SubtitleDocument.Parse(Srt, ".srt");
        await Assert.That(document.GetText(TimeSpan.FromSeconds(11), TimeSpan.Zero)).IsEqualTo("Second");
        await Assert.That(document.GetText(TimeSpan.FromSeconds(2), TimeSpan.Zero)).IsEqualTo("你好\nHello");
        await Assert.That(document.GetText(TimeSpan.FromSeconds(7), TimeSpan.Zero)).IsEqualTo("");
    }

    [Test]
    public async Task Timing_AdjustWhilePaused_ShouldChangeCaptionWithoutChangingPosition()
    {
        var document = SubtitleDocument.Parse(Srt, ".srt");
        TimeSpan paused = TimeSpan.FromSeconds(5);
        await Assert.That(document.GetText(paused, TimeSpan.Zero)).IsEqualTo("");
        await Assert.That(document.GetText(paused, TimeSpan.FromSeconds(3))).IsEqualTo("你好\nHello");
    }

    [Test]
    public async Task Parse_WebVttSettingsAndMarkup_ShouldKeepReadableBilingualText()
    {
        var document = SubtitleDocument.Parse("WEBVTT\n\ncaption-id\n00:01.000 --> 00:03.000 align:center\n<i>Hello</i> &amp; <b>world</b>\n你好\n", ".vtt");
        await Assert.That(document.GetText(TimeSpan.FromSeconds(2), TimeSpan.Zero)).IsEqualTo("Hello & world\n你好");
    }

    [Test]
    public async Task Parse_AssDialogueAndOverlaps_ShouldKeepLineBreaksAndCommas()
    {
        const string ass = "[Events]\nFormat: Start, End, Text\nDialogue: 0:00:01.00,0:00:03.00,{\\an8}你好\\NHello, world\nDialogue: 0:00:02.00,0:00:04.00,Other\nComment: 0:00:01.00,0:00:03.00,Ignore";
        var document = SubtitleDocument.Parse(ass, ".ass");
        await Assert.That(document.Count).IsEqualTo(2);
        await Assert.That(document.GetText(TimeSpan.FromSeconds(2.5), TimeSpan.Zero)).IsEqualTo("你好\nHello, world\nOther");
    }

    [Test]
    public async Task Read_Gb18030_ShouldDecodeChinese()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var input = new MemoryStream(Encoding.GetEncoding(54936).GetBytes(Srt));
        var document = await SubtitleDocument.ReadAsync(input, ".srt");
        await Assert.That(document.GetText(TimeSpan.FromSeconds(2), TimeSpan.Zero)).IsEqualTo("你好\nHello");
    }

    [Test]
    public async Task Read_Utf16Bom_ShouldDecodeChinese()
    {
        using var input = new MemoryStream([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(Srt)]);
        var document = await SubtitleDocument.ReadAsync(input, ".srt");
        await Assert.That(document.GetText(TimeSpan.FromSeconds(2), TimeSpan.Zero)).IsEqualTo("你好\nHello");
    }

    [Test]
    public async Task Parse_InvalidRowsAndUnsortedCaptions_ShouldSkipInvalidAndIndexValid()
    {
        var document = SubtitleDocument.Parse("0\n00:00:03,000 --> 00:00:01,000\nBad\n\n" + Srt.Replace("你好", "中文"), ".srt");
        await Assert.That(document.Count).IsEqualTo(2);
        await Assert.That(document.GetText(TimeSpan.FromSeconds(2), TimeSpan.Zero)).IsEqualTo("中文\nHello");
    }

    [Test]
    public async Task Parse_LargeTranscriptWithLongLivedCue_ShouldFindOverlappingCaptionsAfterSeek()
    {
        var text = new StringBuilder("[Events]\nFormat: Start, End, Text\nDialogue: 0:00:00.00,10:00:00.00,Long\n");
        for (int i = 0; i < 30_000; i++)
        {
            TimeSpan start = TimeSpan.FromSeconds(i);
            TimeSpan end = start + TimeSpan.FromMilliseconds(500);
            text.Append($"Dialogue: {start:hh\\:mm\\:ss\\.ff},{end:hh\\:mm\\:ss\\.ff},{i}\n");
        }

        var document = SubtitleDocument.Parse(text.ToString(), ".ass");
        await Assert.That(document.Count).IsEqualTo(30_001);
        await Assert.That(document.GetText(TimeSpan.FromSeconds(25_000.25), TimeSpan.Zero)).IsEqualTo("Long\n25000");
        await Assert.That(document.GetText(TimeSpan.FromSeconds(2.25), TimeSpan.Zero)).IsEqualTo("Long\n2");
    }

    [Test]
    public async Task Read_Cancellation_ShouldStopWithoutParsing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(Srt));
        bool canceled = false;
        try { await SubtitleDocument.ReadAsync(input, ".srt", cancellation.Token); }
        catch (OperationCanceledException) { canceled = true; }
        await Assert.That(canceled).IsTrue();
    }

    [Test]
    public async Task Session_DuplicateLoads_ShouldShareReadAndNotChangeLatestSelection()
    {
        using var session = new ExternalSubtitleSession();
        var slow = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        session.Select("first");
        Task<SubtitleDocument> first = session.LoadAsync("first", ".srt", _ => { Interlocked.Increment(ref reads); return slow.Task; });
        Task<SubtitleDocument> same = session.LoadAsync("first", ".srt", _ => throw new InvalidOperationException("Duplicate read"));
        session.Select("second");
        await session.LoadAsync("second", ".srt", _ => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(Srt.Replace("你好", "新字幕")))));
        slow.SetResult(new MemoryStream(Encoding.UTF8.GetBytes(Srt)));
        await first;
        await Assert.That(ReferenceEquals(first, same)).IsTrue();
        await Assert.That(reads).IsEqualTo(1);
        await Assert.That(session.GetText(TimeSpan.FromSeconds(2), TimeSpan.Zero)).IsEqualTo("新字幕\nHello");
    }

    [Test]
    public async Task Session_DisableWhileLoading_ShouldStayDisabledAfterReadCompletes()
    {
        using var session = new ExternalSubtitleSession();
        var slow = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Select("file");
        Task<SubtitleDocument> load = session.LoadAsync("file", ".srt", _ => slow.Task);
        session.Select(null);
        slow.SetResult(new MemoryStream(Encoding.UTF8.GetBytes(Srt)));
        await load;
        await Assert.That(session.GetText(TimeSpan.FromSeconds(2), TimeSpan.Zero)).IsEqualTo("");
    }

    [Test]
    public async Task Session_VideoClosed_ShouldCancelPendingRead()
    {
        using var session = new ExternalSubtitleSession();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<SubtitleDocument> load = session.LoadAsync("file", ".srt", async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Cancellation expected");
        });
        await entered.Task;
        session.Dispose();
        bool canceled = false;
        try { await load; }
        catch (OperationCanceledException) { canceled = true; }
        await Assert.That(canceled).IsTrue();
    }
}

