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

    public static string SrtText(IReadOnlyList<double> times, double dur = 2.0, IReadOnlyList<string>? texts = null)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 1; i <= times.Count; i++)
        {
            var t = times[i - 1];
            var txt = texts is not null ? texts[i - 1] : $"Dòng {i}";
            sb.Append($"{i}{Srt.NewLine}{Srt.FormatStamp(t)} --> {Srt.FormatStamp(t + dur)}{Srt.NewLine}{txt}{Srt.NewLine}{Srt.NewLine}");
        }
        return sb.ToString();
    }

    public static byte[] PgsSegment(double t, byte segType, byte[] payload)
    {
        var pts = (uint)((long)Math.Round(t * 90000) & 0xFFFFFFFF);
        var head = new List<byte> { (byte)'P', (byte)'G' };
        head.AddRange([(byte)(pts >> 24), (byte)(pts >> 16), (byte)(pts >> 8), (byte)pts]);
        head.AddRange([0, 0, 0, 0, segType, (byte)(payload.Length >> 8), (byte)payload.Length]);
        head.AddRange(payload);
        return [.. head];
    }

    /// <summary>PCS: rộng(2) cao(2) fps(1) số(2) trạng thái(1) cờ_bảng_màu(1) id_bảng_màu(1) số_đối_tượng(1) [+8 byte/đối tượng].</summary>
    public static byte[] PcsPayload(int nobj, bool paletteOnly = false)
    {
        var body = new byte[11 + 8 * nobj];
        body[0] = 1920 >> 8;
        body[1] = 1920 & 0xFF;
        body[2] = 1080 >> 8;
        body[3] = 1080 & 0xFF;
        body[4] = 0x10;
        body[8] = paletteOnly ? (byte)0x80 : (byte)0x00;
        body[10] = (byte)nobj;
        return body;
    }

    /// <summary>events: (giây, loại) với loại ∈ show | hide | palette | other.</summary>
    public static byte[] PgsStream(params (double T, string Kind)[] events)
    {
        var outp = new List<byte>();
        foreach (var (t, kind) in events)
        {
            switch (kind)
            {
                case "show":
                    outp.AddRange(PgsSegment(t, 0x16, PcsPayload(1)));
                    outp.AddRange(PgsSegment(t, 0x17, new byte[10])); // WDS
                    outp.AddRange(PgsSegment(t, 0x14, new byte[20])); // PDS
                    outp.AddRange(PgsSegment(t, 0x15, new byte[30])); // ODS
                    outp.AddRange(PgsSegment(t, 0x80, [])); // END
                    break;
                case "hide":
                    outp.AddRange(PgsSegment(t, 0x16, PcsPayload(0)));
                    outp.AddRange(PgsSegment(t, 0x80, []));
                    break;
                case "palette":
                    outp.AddRange(PgsSegment(t, 0x16, PcsPayload(1, paletteOnly: true)));
                    outp.AddRange(PgsSegment(t, 0x80, []));
                    break;
                default:
                    outp.AddRange(PgsSegment(t, 0x14, new byte[20]));
                    break;
            }
        }
        return [.. outp];
    }
}
