using System.Text;
using MuxStudio.Core.Tests.Support;

namespace MuxStudio.Core.Tests;

/// <summary>§6.3 — Đọc mốc thời gian; TĐ-01 — lỗi giờ của Format-SrtStamp. Port <c>tests/test_srt.py</c>.</summary>
public class SrtTests
{
    [Theory]
    [InlineData(0.0, "00:00:00,000")]
    [InlineData(10.5, "00:00:10,500")]
    [InlineData(1799.0, "00:29:59,000")]
    [InlineData(2700.0, "00:45:00,000")] // .bat: 01:45:00,000
    [InlineData(3599.9, "00:59:59,900")] // .bat: 01:59:59,900
    [InlineData(3600.0, "01:00:00,000")]
    [InlineData(5400.0, "01:30:00,000")] // .bat: 02:30:00,000
    [InlineData(6000.25, "01:40:00,250")] // .bat: 02:40:00,250
    [InlineData(0.0005, "00:00:00,001")] // làm tròn mili-giây như .NET Framework
    [InlineData(59.9996, "00:01:00,000")]
    [InlineData(-3.0, "00:00:00,000")]
    [InlineData(36000.0, "10:00:00,000")]
    public void Format_stamp_hours_are_floor_not_rounded(double sec, string txt) => Assert.Equal(txt, Srt.FormatStamp(sec));

    [Fact]
    public void Stamp_roundtrip_every_minute_for_three_hours()
    {
        for (var m = 0; m < 180; m++)
        {
            var s = m * 60 + 0.123;
            Approx.Equal(s, Srt.ParseStamp(Srt.FormatStamp(s)), rel: 1e-6, abs: 1e-9);
        }
    }

    [Theory]
    [InlineData("01:02:03,456", 3723.456)]
    [InlineData("01:02:03.456", 3723.456)]
    [InlineData("0:00:01,50", 1.5)]
    [InlineData("00:00:01,05", 1.05)]
    public void Parse_stamp(string t, double v) => Approx.Equal(v, Srt.ParseStamp(t));

    [Fact]
    public void Cue_times_srt_sorted()
    {
        using var tmp = new TempDir();
        var p = tmp["[Judas] x.srt"];
        File.WriteAllText(p, Synth.SrtText([30.0, 10.0, 2700.5]), Py.Utf8NoBom);
        Assert.Equal([10.0, 30.0, 2700.5], Srt.CueTimes(p));
    }

    [Fact]
    public void Cue_times_srt_dot_separator_and_two_digits()
    {
        const string txt = "1\n00:00:01.50 --> 00:00:02.00\nA\n\n2\n00:00:03,250 --> 00:00:04,000\nB\n";
        Assert.Equal([1.5, 3.25], Srt.CueTimesFromText(txt));
    }

    [Fact]
    public void Cue_times_ass_numeric_layer_and_marked()
    {
        const string ass =
            "[Script Info]\nTitle: x\n\n[Events]\n" +
            "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n" +
            "Dialogue: 0,0:00:05.20,0:00:07.00,Default,,0,0,0,,Xin chào\n" +
            "Dialogue: Marked=0,0:01:02.34,0:01:03.00,Default,,0,0,0,,SSA v4\n" +
            "Comment: 0,0:00:09.00,0:00:10.00,Default,,0,0,0,,bỏ qua\n";
        Assert.Equal([5.2, 62.34], Srt.CueTimesFromText(ass));
    }

    [Fact]
    public void Cue_times_vtt_with_and_without_hours()
    {
        const string vtt = "WEBVTT\n\n00:01.000 --> 00:04.000\nA\n\n01:02:03.500 --> 01:02:05.000\nB\n\n00:10.250 --> 00:11.000\nC\n";
        Assert.Equal([1.0, 10.25, 3723.5], Srt.CueTimesFromText(vtt));
    }

    [Fact]
    public void Cue_times_utf16_bom()
    {
        using var tmp = new TempDir();
        var p = tmp["u16.srt"];
        File.WriteAllBytes(p, [0xFF, 0xFE, .. new UnicodeEncoding(false, false).GetBytes(Synth.SrtText([1.0, 2.0]))]);
        Assert.Equal([1.0, 2.0], Srt.CueTimes(p));
    }

    [Fact]
    public void Cue_times_none_when_nothing()
    {
        using var tmp = new TempDir();
        var p = tmp["empty.srt"];
        File.WriteAllText(p, "", Py.Utf8NoBom);
        Assert.Null(Srt.CueTimes(p));
        Assert.Null(Srt.CueTimes(tmp["khong-co.srt"]));
        Assert.Null(Srt.CueTimesFromText("hello"));
    }

    [Fact]
    public void Preview_times()
    {
        const string pv = "1\n00:00:12,000 --> 00:00:14,000\nHi\n\n2\n00:00:03,500 --> 00:00:05,000\nHo";
        Assert.Equal([3.5, 12.0], Srt.PreviewTimes(pv));
        Assert.Empty(Srt.PreviewTimes(null));
    }

    [Fact]
    public void Blocks_roundtrip_keeps_hours_text_and_tail()
    {
        double[] times = [5.0, 1900.0, 2700.0, 3599.0, 4000.0, 6000.25];
        var texts = Enumerable.Range(0, times.Length).Select(i => $"Dòng {i}\nhai dòng").ToList();
        using var tmp = new TempDir();
        var p = tmp["a.srt"];
        File.WriteAllText(p, Synth.SrtText(times, texts: texts), Py.Utf8NoBom);
        var blocks = Srt.ReadBlocks(p)!;
        blocks[1].Tail = " X1:10 X2:20";
        var outp = tmp["b.srt"];
        Assert.True(Srt.WriteBlocks(outp, blocks));
        var again = Srt.ReadBlocks(outp)!;
        Assert.Equal(times, again.Select(b => b.Start));
        Assert.Equal(texts, again.Select(b => b.Text.Replace("\r\n", "\n", StringComparison.Ordinal)));
        Assert.Equal(" X1:10 X2:20", again[1].Tail);
        var raw = File.ReadAllBytes(outp);
        Assert.False(raw.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])); // UTF-8 không BOM
        var s = Encoding.Latin1.GetString(raw);
        Assert.Contains("\r\n", s, StringComparison.Ordinal); // CRLF như .bat
        Assert.Contains("00:45:00,000 --> ", s, StringComparison.Ordinal);
        Assert.DoesNotContain("01:45:00", s, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_blocks_without_trailing_newline_and_renumbering()
    {
        using var tmp = new TempDir();
        var p = tmp["c.srt"];
        File.WriteAllText(p, "7\n00:00:01,000 --> 00:00:02,000\nA\n\n9\n00:00:03,000 --> 00:00:04,000\nB", Py.Utf8NoBom);
        var bl = Srt.ReadBlocks(p)!;
        Assert.Equal(["A", "B"], bl.Select(b => b.Text));
        var outp = tmp["d.srt"];
        Srt.WriteBlocks(outp, bl);
        Assert.StartsWith("1\r\n00:00:01,000", Py.Utf8Strict.GetString(File.ReadAllBytes(outp)), StringComparison.Ordinal);
    }

    [Fact]
    public void Read_blocks_returns_none_for_ass()
    {
        using var tmp = new TempDir();
        var p = tmp["x.ass"];
        File.WriteAllText(p, "[Events]\nDialogue: 0,0:00:05.20,0:00:07.00,Default,,0,0,0,,X\n", Py.Utf8NoBom);
        Assert.Null(Srt.ReadBlocks(p)); // bước gọi bỏ qua, KHÔNG đổi định dạng (Q7)
    }

    [Fact]
    public void Max_shift()
    {
        Assert.Equal(5, Srt.MaxShift([10, 20, 30], [12, 21, 35]));
        Assert.Equal(6, Srt.MaxShift([10, 20], [4, 21]));
        Assert.Null(Srt.MaxShift(null, [1]));
    }

    // ---- thêm cho bản C#: \Z của Python (cuối chuỗi tuyệt đối) ≠ \Z của .NET ----
    [Fact]
    public void Block_text_with_trailing_single_newline_is_kept_whole()
    {
        // Khối cuối kết thúc bằng một "\n": Python \Z không khớp trước "\n" -> lookahead "\r?\n?\Z" ăn "\n" rồi khớp.
        var bl = Srt.ReadBlocksText("1\n00:00:01,000 --> 00:00:02,000\nA\nB\n")!;
        Assert.Single(bl);
        Assert.Equal("A\nB", bl[0].Text);
    }

    [Fact]
    public void Cue_spans_formats()
    {
        Assert.Equal("srt", Srt.CueSpansFromText("1\n00:00:01,000 --> 00:00:02,500\nA\n")!.Value.Format);
        Assert.Equal([(1.0, 2.5)], Srt.CueSpansFromText("1\n00:00:01,000 --> 00:00:02,500\nA\n")!.Value.Spans);
        var vtt = Srt.CueSpansFromText("WEBVTT\n\n00:01.000 --> 00:04.000\nA\n")!.Value;
        Assert.Equal(("vtt", 1.0, 4.0), (vtt.Format, vtt.Spans[0].Start, vtt.Spans[0].End));
        var ass = Srt.CueSpansFromText("[Events]\nDialogue: 0,0:00:05.20,0:00:07.00,Default,,0,0,0,,X\n")!.Value;
        Assert.Equal("ass", ass.Format);
        Approx.Equal(5.2, ass.Spans[0].Start);
        Approx.Equal(7.0, ass.Spans[0].End);
        Assert.Null(Srt.CueSpansFromText("hello"));
    }
}
