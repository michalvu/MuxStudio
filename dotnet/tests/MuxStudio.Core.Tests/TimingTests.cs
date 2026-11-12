using System.Globalization;
using MuxStudio.Core.Tests.Support;

namespace MuxStudio.Core.Tests;

/// <summary>§6.4 — So khớp thời gian (lõi của mọi phép đo). Port <c>tests/test_timing.py</c>.</summary>
public class TimingTests
{
    private static readonly double[] Offsets = [0.0, 12.3, -30.45, 95.1];

    public static TheoryData<int, double> RelationCases()
    {
        var d = new TheoryData<int, double>();
        foreach (var off in Offsets)
            for (var i = 0; i < Timing.FpsRatios.Count; i++)
                d.Add(i, off);
        return d;
    }

    [Theory]
    [MemberData(nameof(RelationCases))]
    public void Relation_finds_known_scale_and_offset(int ratioIndex, double offset)
    {
        var ratio = Timing.FpsRatios[ratioIndex];
        var refT = Synth.MakeRefTimes(700, seed: 11);
        var tgt = Synth.TransformTimes(refT, ratio.Ratio, offset, seed: (int)(Math.Abs(offset) * 10) + 3);
        var rel = Timing.TimingRelationOf(refT, tgt);
        Assert.NotNull(rel);
        Assert.True(rel.Scale == ratio.Ratio, $"chọn {rel.Name}, đúng phải là {ratio.Name}");
        Assert.True(Math.Abs(rel.Offset - offset) < 0.1);
        Assert.True(rel.Votes >= 20);
        Assert.Equal(ratio.Ratio != 1.0, rel.FpsMismatch);
    }

    [Fact]
    public void Relation_other_movie_is_below_threshold()
    {
        // Phụ đề của "phim khác" → dưới ngưỡng 20 mốc (khớp sai chỉ 5–15 phiếu).
        var worst = 0;
        for (var seed = 0; seed < 12; seed++)
        {
            var refT = Synth.MakeRefTimes(700, seed: 100 + seed);
            var other = Synth.MakeRefTimes(650, seed: 500 + seed);
            var rel = Timing.TimingRelationOf(refT, other);
            Assert.NotNull(rel);
            worst = Math.Max(worst, rel.Votes);
        }
        Assert.True(worst < 20, $"phim khác đạt {worst} phiếu");
    }

    [Fact]
    public void Relation_needs_30_cues()
    {
        var refT = Synth.MakeRefTimes(100);
        Assert.Null(Timing.TimingRelationOf(refT, refT[..29]));
        Assert.Null(Timing.TimingRelationOf(refT[..29], refT));
        Assert.Null(Timing.TimingRelationOf(null, refT));
        Assert.Null(Timing.TimingRelationOf(refT, []));
        Assert.NotNull(Timing.TimingRelationOf(refT, refT[..30]));
    }

    [Fact]
    public void Relation_drift_uses_last_target_cue()
    {
        var refT = Synth.MakeRefTimes(400, seed: 5);
        var r = Timing.FpsRatios[3]; // PAL 25 -> 23.976
        var tgt = Synth.TransformTimes(refT, r.Ratio, 0.0, seed: 6);
        var rel = Timing.TimingRelationOf(refT, tgt);
        Assert.NotNull(rel);
        Approx.Equal(Math.Abs((r.Ratio - 1.0) * tgt[^1]), rel.Drift);
    }

    [Fact]
    public void Even_sample_rounds_half_to_even_like_powershell()
    {
        var arr = Enumerable.Range(0, 1000).ToList();
        var s = Timing.EvenSample(arr, 400); // step 2.5: [int]2.5 = 2, [int]7.5 = 8
        Assert.Equal([0, 2, 5, 8, 10, 12, 15, 18, 20], s[..9]);
        Assert.Equal([1, 2, 3], Timing.EvenSample([1, 2, 3], 5));
        Assert.Equal(400, Timing.EvenSample(Enumerable.Range(0, 401).ToList(), 400).Count);
    }

    [Fact]
    public void Offset_vote_tie_keeps_first_inserted_bin()
    {
        // tgt=0: d = 1.5 (ô 10) và 3.0 (ô 20) -> mỗi ô 1 phiếu; ô gặp trước (10) thắng
        var v = Timing.OffsetVoteOf([1.5, 3.0], [0.0]);
        Assert.NotNull(v);
        Assert.Equal(1, v.Votes);
        Approx.Equal(1.5, v.Offset);
    }

    private static List<double> SortedUniform(PyRandom rng, int n, double hi)
    {
        var l = new List<double>(n);
        for (var i = 0; i < n; i++)
            l.Add(rng.Uniform(0, hi));
        l.Sort();
        return l;
    }

    [Fact]
    public void Offset_vote_window_equals_full_scan()
    {
        // Bản duyệt cửa sổ (tìm nhị phân) cho kết quả GIỐNG HỆT bản duyệt hết của .bat.
        var rng = new PyRandom(7);
        for (var i = 0; i < 20; i++)
        {
            var refT = SortedUniform(rng, 400, 6000);
            var tgt = SortedUniform(rng, 100, 6000);
            var fast = Timing.OffsetVoteOf(refT, tgt);
            var rev = refT.ToList();
            rev.Reverse();
            var slow = Timing.OffsetVoteOf(rev, tgt); // không sắp xếp -> duyệt hết
            Assert.Equal(slow!.Votes, fast!.Votes);
        }
        // cùng thứ tự: so sánh với cài đặt duyệt hết viết tay
        var ref2 = SortedUniform(rng, 400, 6000);
        var tgt2 = SortedUniform(rng, 100, 6000);
        var votes = new OrderedDictionary<long, int>();
        foreach (var t in tgt2)
            foreach (var r in ref2)
            {
                var d = r - t;
                if (d >= -400 && d <= 400)
                {
                    var k = (long)Math.Round(d / 0.15);
                    votes[k] = votes.TryGetValue(k, out var c0) ? c0 + 1 : 1;
                }
            }
        long bk = 0;
        var bv = -1;
        foreach (var (k, c0) in votes)
            if (c0 > bv)
            {
                bv = c0;
                bk = k;
            }
        var c = bk * 0.15;
        var s = 0.0;
        var n = 0;
        foreach (var t in tgt2)
            foreach (var r in ref2)
                if (Math.Abs((r - t) - c) <= 0.35)
                {
                    s += r - t;
                    n++;
                }
        var got = Timing.OffsetVoteOf(ref2, tgt2);
        Assert.Equal(bv, got!.Votes);
        Assert.Equal(n > 0 ? s / n : c, got.Offset); // bằng nhau từng bit
    }

    [Fact]
    public void Quick_offset_true_previews_accepted_random_rejected()
    {
        // Preview đúng: nhận 60/60; preview ngẫu nhiên: lọt 0/60 (như ghi chú trong .bat).
        var rng = new PyRandom(42);
        var ok = 0;
        var bad = 0;
        for (var i = 0; i < 60; i++)
        {
            var refT = Synth.MakeRefTimes(700, seed: 1000 + i);
            var off = PyRandom.Round(rng.Uniform(-170, 170), 1);
            var start = rng.RandRange(0, 50);
            var pv = refT.Skip(start).Take(5).Select(r => r - off).Where(t => t > 0).ToList();
            if (pv.Count < 3)
                pv = refT[100..105].Select(r => r - off).ToList();
            var q = Timing.QuickOffsetOf(refT, pv);
            if (q is not null && Math.Abs(q.Offset - off) <= 0.125)
                ok++;
            var rnd = SortedUniform(rng, 5, 5000);
            if (Timing.QuickOffsetOf(refT, rnd) is not null)
                bad++;
        }
        Assert.Equal(60, ok);
        Assert.Equal(0, bad);
    }

    [Fact]
    public void Quick_offset_with_jitter_inside_bin()
    {
        var refT = Synth.MakeRefTimes(500, seed: 9);
        double[] jit = [0.03, -0.02, 0.01, -0.03, 0.02];
        var pv = refT[10..15].Select((r, i) => r - 12.5 + jit[i]).ToList();
        var q = Timing.QuickOffsetOf(refT, pv);
        Assert.NotNull(q);
        Assert.Equal(12.5, q.Offset);
        Assert.Equal(5, q.Total);
    }

    [Fact]
    public void Quick_offset_needs_three_cues()
    {
        var refT = Synth.MakeRefTimes(100);
        Assert.Null(Timing.QuickOffsetOf(refT, refT[..2]));
        Assert.Null(Timing.QuickOffsetOf(null, refT[..5]));
    }

    [Fact]
    public void Ref_bins_matches_linear_scan()
    {
        var refT = Synth.MakeRefTimes(800, seed: 3);
        var bins = new RefBins(refT);
        var rng = new PyRandom(4);
        for (var i = 0; i < 2000; i++)
        {
            var x = rng.Uniform(0, 7000);
            var c = (long)Math.Floor(x);
            var linear = refT.Any(r =>
            {
                var b = (long)Math.Floor(r);
                return b == c || b == c - 1 || b == c + 1 || b == c - 2;
            });
            Assert.Equal(linear, bins.Near(x));
        }
    }

    public static TheoryData<object?, double> ToDoubleCases() => new()
    {
        { "23.976", 23.976 }, { "23,976", 23.976 }, { " 25 ", 25.0 }, { "1e3", 1000.0 },
        { "", 0.0 }, { null, 0.0 }, { "abc", 0.0 }, { "1,234.5", 0.0 }, { 24, 24.0 }, { true, 0.0 },
    };

    [Theory]
    [MemberData(nameof(ToDoubleCases))]
    public void To_double_locale_independent(object? s, double v) => Assert.Equal(v, Timing.ToDouble(s));

    [Fact]
    public void To_double_under_czech_locale()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            foreach (var name in new[] { "cs-CZ", "vi-VN", "de-DE" })
            {
                CultureInfo.CurrentCulture = new CultureInfo(name);
                Assert.Equal(23.976, Timing.ToDouble("23.976"));
                Assert.Equal("23.976", Timing.FmtInv(23.976));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = old;
        }
    }

    public static TheoryData<double, string> FmtInvCases() => new()
    {
        { 25 / 23.976, "1.042709" }, { 1.0, "1" }, { 0.5, "0.5" }, { -0.0000001, "0" }, { 48000.0, "48000" },
    };

    [Theory]
    [MemberData(nameof(FmtInvCases))]
    public void Fmt_inv_dot_decimal(double x, string s)
    {
        Assert.Equal(s, Timing.FmtInv(x));
        Assert.DoesNotContain(",", Timing.FmtInv(x), StringComparison.Ordinal);
    }

    [Fact]
    public void Fps_ratio_table_order_matches_bat()
    {
        double[] expected = [1.0, 24 / 23.976, 23.976 / 24, 25 / 23.976, 23.976 / 25, 25.0 / 24, 24.0 / 25];
        Assert.Equal(expected.Select(x => PyRandom.Round(x, 6)), Timing.FpsRatios.Select(r => PyRandom.Round(r.Ratio, 6)));
    }

    [Fact]
    public void PyRandom_matches_python()
    {
        // Giá trị in từ Python: random.Random(1).random(), uniform(1.2, 12.0), randrange(0, 50) với seed 42
        var r = new PyRandom(1);
        Assert.Equal(0.13436424411240122, r.Random());
        var r42 = new PyRandom(42);
        Assert.Equal(0.6394267984578837, r42.Random());
        Assert.Equal(new[] { 30.0, 32.651, 43.003 }, Synth.MakeRefTimes(3, seed: 1));
        var big = new PyRandom(123456789012345); // seed > 32 bit: nhiều khối khoá
        Assert.Equal(0.6744750084019356, big.Random());
        Assert.Equal(18, big.RandRange(0, 50));
        Assert.Equal(3, big.RandRange(0, 7));
    }
}
