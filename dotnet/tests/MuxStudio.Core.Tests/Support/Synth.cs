namespace MuxStudio.Core.Tests.Support;

/// <summary>Sinh dữ liệu phụ đề giả lập (không cần phim thật) — port <c>tests/synth.py</c>.</summary>
public static partial class Synth
{
    /// <summary>Mốc phụ đề "gốc" của một phim: khoảng cách ngẫu nhiên giữa các câu thoại.</summary>
    public static List<double> MakeRefTimes(int n = 700, double start = 30.0, long seed = 1,
        double minGap = 1.2, double maxGap = 12.0)
    {
        var rng = new PyRandom(seed);
        var t = start;
        var outp = new List<double>(n);
        for (var i = 0; i < n; i++)
        {
            outp.Add(PyRandom.Round(t, 3));
            t += rng.Uniform(minGap, maxGap);
        }
        return outp;
    }

    /// <summary>
    /// Mốc của "phụ đề cần đo" sao cho <c>ref ≈ tgt × scale + offset</c>.
    /// Bỏ ngẫu nhiên một phần câu (<paramref name="keep"/>), thêm nhiễu <paramref name="jitter"/> giây, thêm <paramref name="extra"/> câu lạ.
    /// </summary>
    public static List<double> TransformTimes(IReadOnlyList<double> refTimes, double scale, double offset,
        double keep = 0.9, double jitter = 0.03, int extra = 15, long seed = 2)
    {
        var rng = new PyRandom(seed);
        var outp = new List<double>();
        foreach (var r in refTimes)
        {
            if (rng.Random() > keep)
                continue;
            var t = (r - offset) / scale + rng.Uniform(-jitter, jitter);
            if (t > 0)
                outp.Add(t);
        }
        var hi = outp.Count > 0 ? outp.Max() : 100.0;
        for (var i = 0; i < extra; i++)
            outp.Add(rng.Uniform(0, hi));
        outp.Sort();
        return outp;
    }
}
