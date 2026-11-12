using MuxStudio.Core.Tests.Support;

namespace MuxStudio.Core.Tests;

/// <summary>§6.3 — PGS (.sup) không cần OCR. Port <c>tests/test_pgs.py</c>.</summary>
public class PgsTests
{
    [Fact]
    public void Show_hide_pairs()
    {
        var data = Synth.PgsStream((100, "show"), (102, "hide"), (1900, "show"), (1903.5, "hide"));
        Assert.Equal([(100.0, 102.0), (1900.0, 1903.5)], Pgs.PgsCues(data));
    }

    [Fact]
    public void Palette_only_pcs_is_ignored()
    {
        var data = Synth.PgsStream((10, "show"), (11, "palette"), (12, "hide"));
        Assert.Equal([(10.0, 12.0)], Pgs.PgsCues(data));
    }

    [Fact]
    public void Open_cue_closed_by_next_show()
    {
        var data = Synth.PgsStream((10, "show"), (13, "show"), (15, "hide"));
        Assert.Equal([(10.0, 13.0), (13.0, 15.0)], Pgs.PgsCues(data));
    }

    [Fact]
    public void Short_cues_and_unclosed_tail_dropped()
    {
        var data = Synth.PgsStream((10, "show"), (10.05, "hide"), (20, "show"), (21, "hide"), (30, "show"));
        Assert.Equal([(20.0, 21.0)], Pgs.PgsCues(data));
    }

    [Fact]
    public void Non_pcs_segments_skipped_by_size()
    {
        var data = Synth.PgsStream((5, "other"), (6, "show"), (7, "other"), (8, "hide"));
        Assert.Equal([(6.0, 8.0)], Pgs.PgsCues(data));
    }

    [Fact]
    public void Garbage_stops_parsing()
    {
        byte[] data = [.. Synth.PgsStream((5, "show"), (6, "hide")), (byte)'X', (byte)'X', .. Synth.PgsStream((9, "show"), (10, "hide"))];
        Assert.Equal([(5.0, 6.0)], Pgs.PgsCues(data));
        Assert.Empty(Pgs.PgsCues("PG"u8));
    }

    [Fact]
    public void Timing_srt_file_hours_correct()
    {
        // TĐ-01: PGS ở 1900 s / 2700 s phải ra 00:31:40 / 00:45:00 (bản .bat: 01:31:40 / 01:45:00).
        var data = Synth.PgsStream((100, "show"), (102, "hide"), (1900, "show"), (1902, "hide"),
            (2700, "show"), (2702, "hide"), (4000, "show"), (4002, "hide"));
        using var tmp = new TempDir();
        var sup = tmp["[Judas] Phim.sup"];
        var outp = tmp["[Judas] Phim.srt"];
        File.WriteAllBytes(sup, data);
        Assert.Equal(4, Pgs.PgsToTimingSrt(sup, outp, "*"));
        var txt = Py.Utf8Strict.GetString(File.ReadAllBytes(outp));
        Assert.Contains("00:31:40,000 --> 00:31:42,000", txt, StringComparison.Ordinal);
        Assert.Contains("00:45:00,000 --> 00:45:02,000", txt, StringComparison.Ordinal);
        Assert.Contains("01:06:40,000 --> 01:06:42,000", txt, StringComparison.Ordinal);
        Assert.Equal(4, txt.Split("\r\n*\r\n").Length - 1);
        Assert.Equal([100.0, 1900.0, 2700.0, 4000.0], Srt.CueTimes(outp));
    }

    [Fact]
    public void Timing_srt_failure_returns_zero()
    {
        using var tmp = new TempDir();
        var sup = tmp["x.sup"];
        File.WriteAllBytes(sup, "not pgs at all, nope"u8.ToArray());
        Assert.Equal(0, Pgs.PgsToTimingSrt(sup, tmp["x.srt"]));
        Assert.Equal(0, Pgs.PgsToTimingSrt(tmp["khong-co.sup"], tmp["y.srt"]));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.5)]
    [InlineData(3599.99)]
    [InlineData(5400.123)]
    public void Pts_90khz(double t)
    {
        var data = Synth.PgsStream((t, "show"), (t + 1, "hide"));
        var (s, e) = Pgs.PgsCues(data)[0];
        Assert.True(Math.Abs(s - t) <= 1 / 90000.0);
        Assert.True(Math.Abs(e - (t + 1)) <= 1 / 90000.0);
    }
}
