using System.Globalization;
using System.Numerics;
using JDict = System.Collections.Generic.OrderedDictionary<string, object?>;

namespace MuxStudio.Core;

/// <summary>Chạy một lệnh (= <c>tools.run</c>). Phiên bước ③ nhận hàm này để test thay mkvmerge / ffmpeg giả (Python:
/// <c>monkeypatch.setattr(tools, "run", …)</c>) mà không đụng biến tĩnh dùng chung.</summary>
public delegate RunResult ToolRunner(IReadOnlyList<string> args, Job? job, TimeSpan? timeout);

/// <summary>Phân số chính xác (<c>fractions.Fraction</c>) — chỉ phần <see cref="Donor.StretchFilter"/> cần (TĐ-34: không được dùng
/// <c>double</c>).</summary>
public readonly record struct Fraction(BigInteger Numerator, BigInteger Denominator)
{
    /// <summary><c>Fraction(float)</c>: giá trị nhị phân CHÍNH XÁC của số thực (vd 1.1 → 2476979795053773/2251799813685248).</summary>
    public static Fraction FromDouble(double x)
    {
        if (double.IsNaN(x) || double.IsInfinity(x))
            throw new ArgumentException("cannot convert NaN/inf to Fraction", nameof(x));
        if (x == 0)
            return new Fraction(0, 1);
        var bits = BitConverter.DoubleToInt64Bits(x);
        var neg = bits < 0;
        var exp = (int)((bits >> 52) & 0x7FF);
        var mant = bits & 0xFFFFFFFFFFFFFL;
        if (exp == 0)
            exp = 1; // so khong chuan hoa
        else
            mant |= 1L << 52;
        exp -= 1075; // x = mant * 2^exp
        BigInteger n = mant, d = BigInteger.One;
        if (exp > 0)
            n <<= exp;
        else
            d <<= -exp;
        var g = BigInteger.GreatestCommonDivisor(n, d);
        return new Fraction(neg ? -n / g : n / g, d / g);
    }

    /// <summary><c>limit_denominator(max_denominator)</c>: phân số gần nhất có mẫu ≤ <paramref name="maxDenominator"/> (liên phân số;
    /// bằng nhau thì lấy cận <c>p1/q1</c> như Python).</summary>
    public Fraction LimitDenominator(long maxDenominator = 1000000)
    {
        if (maxDenominator < 1)
            throw new ArgumentOutOfRangeException(nameof(maxDenominator));
        if (Denominator <= maxDenominator)
            return this;
        BigInteger p0 = 0, q0 = 1, p1 = 1, q1 = 0, n = Numerator, d = Denominator;
        while (true)
        {
            var a = BigInteger.Divide(n, d);
            if (n.Sign < 0 && a * d != n)
                a -= 1; // n // d (lam tron xuong)
            var q2 = q0 + a * q1;
            if (q2 > maxDenominator)
                break;
            (p0, q0, p1, q1) = (p1, q1, p0 + a * p1, q2);
            (n, d) = (d, n - a * d);
        }
        var k = (maxDenominator - q0) / q1;
        return 2 * d * (q0 + k * q1) <= Denominator ? new Fraction(p1, q1) : new Fraction(p0 + k * p1, q0 + k * q1);
    }

    /// <summary><c>float(fr)</c> (làm tròn đúng — tử và mẫu ở đây nhỏ nên phép chia <c>double</c> là chính xác).</summary>
    public double ToDouble() => (double)Numerator / (double)Denominator;
}

/// <summary><c>donor.TrackTimes</c> = <c>Get-TrackCueTimes</c>: trích track phụ đề (<c>mkvextract</c>; PGS → SRT mốc) → mốc thời
/// gian. Có bộ nhớ đệm.</summary>
public class TrackTimes(string? mkvextract, string temp, Job job, string placeholder = "*", ToolRunner? runner = null)
{
    private readonly Dictionary<(string File, int Id), List<double>?> _cache = [];

    public Job Job { get; set; } = job;

    public ToolRunner Runner { get; set; } = runner ?? ((a, j, t) => Tools.Run(a, j, t));

    public virtual List<double>? Get(string file, SubRef trk)
    {
        var key = (Py.NormCase(Py.AbsPath(file)), trk.Id);
        if (_cache.TryGetValue(key, out var hit))
            return hit;
        _cache[key] = null;
        if (string.IsNullOrEmpty(mkvextract))
            return null;
        Directory.CreateDirectory(temp);
        var raw = Py.PathJoin(temp, $"s_{Guid.NewGuid():N}"[..10] + trk.Ext);
        Runner([mkvextract, "tracks", file, $"{trk.Id.ToString(CultureInfo.InvariantCulture)}:{raw}"], Job, null);
        if (!File.Exists(raw))
            return null;
        var path = raw;
        if (trk.Ext == ".sup")
        {
            path = Py.SplitExt(raw).Root + ".srt";
            if (Pgs.PgsToTimingSrt(raw, path, placeholder) == 0)
                return null;
        }
        var t = Srt.CueTimes(path);
        _cache[key] = t;
        return t;
    }
}

/// <summary>Hàm thay <see cref="TrackTimes"/> (test: bảng mốc cho sẵn — <c>times_fn</c> của Python).</summary>
public sealed class TrackTimesFn(Func<string, SubRef, List<double>?> fn) : TrackTimes(null, "", Job.Null)
{
    public override List<double>? Get(string file, SubRef trk) => fn(file, trk);
}

/// <summary>Độ lệch đo được (port <c>donor.DonorTiming</c>): <c>phim_của_bạn ≈ nguồn × Scale + Offset</c> (giây).</summary>
public sealed class DonorTiming(double scale, double offset, int votes, string name, string how, bool sure, bool close = false)
{
    public double Scale { get; set; } = scale;
    public double Offset { get; set; } = offset;
    public int Votes { get; set; } = votes;

    /// <summary>Tên tỉ lệ FPS.</summary>
    public string Name { get; set; } = name;

    /// <summary>Cách đo (hiện trên bảng).</summary>
    public string How { get; set; } = how;

    public bool Sure { get; set; } = sure;

    /// <summary>Không đo được nhưng thời lượng khớp.</summary>
    public bool Close { get; set; } = close;
}

/// <summary>Một audio ghép thêm vào file của bạn (port <c>donor.DonorTrack</c>).</summary>
public sealed class DonorTrack(string source, int tid, int aIndex, string code, string name, int offsetMs)
{
    public string Source { get; } = source;

    /// <summary>ID track trong file nguồn.</summary>
    public int Tid { get; } = tid;

    /// <summary>Thứ tự audio trong file nguồn (<c>0:a:N</c> cho ffmpeg).</summary>
    public int AIndex { get; } = aIndex;

    public string Code { get; } = code;

    /// <summary>Tên track.</summary>
    public string Name { get; set; } = name;

    public int OffsetMs { get; set; } = offsetMs;
    public bool Stretch { get; set; }

    /// <summary><c>asetrate=…,aresample=…</c> (khi khác FPS).</summary>
    public string Filter { get; set; } = "";

    public int OutChannels { get; set; }
    public string Bitrate { get; set; } = "";

    /// <summary><c>.mka</c> tạm sau ffmpeg (lúc thực thi).</summary>
    public string? Prepared { get; set; }

    public string InputPath => Stretch && Prepared is not null ? Prepared : Source;

    public int InputTid => Stretch ? 0 : Tid;

    public List<string> FfmpegArgs(string ffmpeg, string outp) =>
    [
        ffmpeg, "-y", "-v", "error", "-i", Source, "-map", $"0:a:{AIndex.ToString(CultureInfo.InvariantCulture)}", "-vn", "-sn",
        "-dn", "-af", Filter, "-ac", OutChannels.ToString(CultureInfo.InvariantCulture), "-c:a", "eac3", "-b:a", Bitrate, outp,
    ];
}

/// <summary>Kế hoạch ghép audio của một file (port <c>donor.DonorPlan</c>).</summary>
public sealed class DonorPlan
{
    public string? Donor { get; set; }
    public DonorTiming? Timing { get; set; }
    public List<DonorTrack> Tracks { get; set; } = [];

    /// <summary>Lý do không ghép / mô tả (cột "Audio ghép").</summary>
    public string Note { get; set; } = "";

    public bool Manual { get; set; }

    public string Summary
    {
        get
        {
            if (Tracks.Count == 0)
                return Note;
            var t = Tracks[0];
            var sign = t.OffsetMs >= 0 ? "+" : "";
            var langs = string.Join(", ", Tracks.Select(x => x.Code));
            var fps = t.Stretch ? " (đổi tốc độ)" : "";
            return string.Create(CultureInfo.InvariantCulture, $"{langs} lệch {sign}{t.OffsetMs} ms{fps}");
        }
    }
}

/// <summary>
/// Ghép audio từ file phim khác (bước ③, §6.9) — port <c>core/donor.py</c>. Thay <c>Get-DonorTmpDir</c>, <c>Get-DonorFiles</c>,
/// <c>Find-DonorFile</c>, <c>Get-TrackCueTimes</c>, <c>Measure-DonorTiming</c>, <c>Get-DonorPlan</c>, <c>Remove-DonorTemp</c> (MuxTool).
/// <list type="number">
/// <item>Ghép tập với tập (<c>S16E643</c> ↔ <c>- 0643</c>), phim lẻ theo tên + năm.</item>
/// <item>Đo độ lệch + chênh FPS bằng PHỤ ĐỀ CHUNG của hai bản (ưu tiên tiếng Anh): <c>phim_của_bạn ≈ nguồn × scale + offset</c>.</item>
/// <item>Cùng FPS → mkvmerge ghép thẳng (<c>--sync TID:ms</c>). Khác FPS → ffmpeg đổi tốc độ CHÍNH XÁC (<see cref="StretchFilter"/>, TĐ-34).</item>
/// <item>Không đo được → chỉ ghép khi thời lượng hai bản chênh ≤ <c>DonorMaxDurationDiff</c>.</item>
/// </list>
/// </summary>
public static class Donor
{
    public static readonly IReadOnlyList<string> DonorExts = [".mkv", ".mka", ".mp4", ".m4v", ".ts"];

    /// <summary><c>donor_files</c> = <c>Get-DonorFiles</c>: <c>.mkv .mka .mp4 .m4v .ts</c> trong thư mục nguồn (đệ quy).</summary>
    public static List<string> DonorFiles(string targetDir, string? folder) =>
        string.IsNullOrEmpty(folder) ? [] : Tools.WalkFiles(Py.PathJoin(targetDir, folder), true, DonorExts);

    /// <summary><c>find_donor_file</c> = <c>Find-DonorFile</c>: phim bộ theo tập (<see cref="Episodes.SameEpisode"/>); phim lẻ theo tên +
    /// năm; chỉ có MỘT file nguồn phim lẻ → lấy luôn.</summary>
    public static string? FindDonorFile(string videoStem, IReadOnlyList<string> pool)
    {
        if (pool.Count == 0)
            return null;
        var ep = Episodes.ParseEpisode(videoStem);
        if (ep is not null)
            return pool.FirstOrDefault(d => Episodes.SameEpisode(ep, Episodes.ParseEpisode(Subprep.Stem(d))));
        var me = Naming.SplitReleaseName(videoStem);
        foreach (var d in pool)
        {
            var ds = Subprep.Stem(d);
            if (Episodes.ParseEpisode(ds) is not null)
                continue;
            var dn = Naming.SplitReleaseName(ds);
            if (Naming.NormTitle(dn.Title) != Naming.NormTitle(me.Title))
                continue;
            if (me.Year is { } my && my != 0 && dn.Year is { } dy && dy != 0 && my != dy)
                continue;
            return d;
        }
        if (pool.Count == 1 && Episodes.ParseEpisode(Subprep.Stem(pool[0])) is null)
            return pool[0];
        return null;
    }

    private static readonly string[] RefSubExts = [".srt", ".ass", ".ssa"];

    /// <summary><c>external_ref_subs</c>: phụ đề ngoài cạnh phim của bạn (đã đồng bộ đúng với phim) — mốc tốt khi đo audio ghép.</summary>
    public static List<(string Code, string Path)> ExternalRefSubs(string video)
    {
        var outp = new List<(string, string)>();
        var vstem = Subprep.Stem(video);
        var pre = (vstem + ".").ToLowerInvariant();
        List<FileSystemInfo> entries;
        try
        {
            entries = [.. new DirectoryInfo(Subprep.Parent(video)).EnumerateFileSystemInfos().OrderBy(e => e.Name.ToLowerInvariant(), StringComparer.Ordinal)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return outp;
        }
        foreach (var e in entries)
        {
            if ((e.Attributes & FileAttributes.Directory) != 0 || !File.Exists(e.FullName))
                continue;
            var p = Py.PathJoin(Subprep.Parent(video), e.Name);
            var stem = Subprep.Stem(p);
            if (!RefSubExts.Contains(Subprep.Suffix(p).ToLowerInvariant()) || !stem.ToLowerInvariant().StartsWith(pre, StringComparison.Ordinal))
                continue;
            var suffix = stem[vstem.Length..];
            if (suffix.Contains("forced", StringComparison.OrdinalIgnoreCase))
                continue;
            var code = Naming.LangOfSuffix(suffix); // TD-25: bang ngon ngu chung
            if (code.Length > 0)
                outp.Add((code, p));
        }
        return outp;
    }

    /// <summary><c>measure_timing</c> = <c>Measure-DonorTiming</c>: ngôn ngữ chung (ưu tiên <c>en</c>); mốc phía bạn: sub trong MKV,
    /// rồi sub ngoài cạnh phim; ≥ <paramref name="minVotes"/> mốc khớp. Không đo được → đoán theo FPS + thời lượng.</summary>
    public static DonorTiming MeasureTiming(string video, JDict? j, string donor, JDict? dj, TrackTimes times, int minVotes = 20,
                                            double maxDurationDiff = 1.0, int nRef = 400, int nTgt = 100,
                                            IReadOnlyList<(string Code, string Path)>? extRefs = null)
    {
        var mine = Probe.RefSubTracks(j);
        var theirs = Probe.RefSubTracks(dj);
        var ext = extRefs ?? ExternalRefSubs(video);
        var langs = new[] { "en" }.Concat(theirs.Select(t => t.Code)).Where(c => c.Length > 0 && c != "und").Distinct(StringComparer.Ordinal).ToList();
        foreach (var lg in langs)
        {
            var dt = theirs.FirstOrDefault(t => t.Code == lg);
            if (dt is null)
                continue;
            List<double>? mineTimes = null;
            var src = "";
            var mt = mine.FirstOrDefault(t => t.Code == lg);
            if (mt is not null)
            {
                mineTimes = times.Get(video, mt);
                src = $"sub {Naming.LangName(lg)} trong MKV";
            }
            if (mineTimes is not { Count: > 0 })
            {
                var er = ext.FirstOrDefault(x => x.Code == lg);
                if (er.Path is not null)
                {
                    mineTimes = Srt.CueTimes(er.Path);
                    src = $"sub {Naming.LangName(lg)} ngoài";
                }
            }
            if (mineTimes is not { Count: > 0 })
                continue;
            var donorTimes = times.Get(donor, dt);
            if (donorTimes is not { Count: > 0 })
                continue;
            var rel = Timing.TimingRelationOf(mineTimes, donorTimes, nRef, nTgt);
            if (rel is not null && rel.Votes >= minVotes)
            {
                return new DonorTiming(rel.Scale, rel.Offset, rel.Votes, rel.Name,
                                       string.Create(CultureInfo.InvariantCulture, $"đo bằng {src} ({rel.Votes} mốc khớp)"), true);
            }
        }
        var fm = Probe.VideoFpsExact(j) ?? 0.0;
        var fd = Probe.VideoFpsExact(dj) ?? 0.0;
        var scale = 1.0;
        if (fm > 0 && fd > 0 && Math.Abs(fm / fd - 1.0) > 0.001)
            scale = fd / fm;
        var dm = Probe.Duration(j);
        var dd = Probe.Duration(dj) * scale;
        var diff = Math.Abs(dm - dd);
        var close = dm > 0 && dd > 0 && diff <= maxDurationDiff;
        var how = close ? $"ƯỚC TÍNH: không có phụ đề chung, thời lượng khớp (chênh {Py.Fixed(diff, 1)}s)"
                        : $"không đo được (thời lượng chênh {Py.Fixed(diff, 1)}s)";
        var name = Timing.FpsRatios.FirstOrDefault(fr => Math.Abs(fr.Ratio - scale) < 1e-6)?.Name ?? "";
        return new DonorTiming(scale, 0.0, 0, name, how, false, close);
    }

    /// <summary><c>manual_offset</c>: <c>DonorOffsetMs</c> — khoá = tên file, <c>S01E03</c>, <c>tap #643</c> hoặc <c>643</c> (không phân
    /// biệt hoa thường).</summary>
    public static double? ManualOffset(string videoStem, IEnumerable<KeyValuePair<string, object?>> offsets)
    {
        var ep = Episodes.ParseEpisode(videoStem);
        var keys = new HashSet<string>(StringComparer.Ordinal) { videoStem.ToLowerInvariant() };
        if (ep is not null)
        {
            keys.Add(ep.RefLabel.ToLowerInvariant());
            keys.Add(ep.Episode.ToString(CultureInfo.InvariantCulture));
        }
        foreach (var (k, v) in offsets)
        {
            if (keys.Contains(Py.StripWs(k).ToLowerInvariant()))
                return Timing.ToDouble(v);
        }
        return null;
    }

    /// <summary>
    /// <c>stretch_filter</c>: bộ lọc ffmpeg đổi tốc độ audio sao cho thời lượng nhân đúng <paramref name="scale"/> (PAL 25 → 23.976…).
    /// TĐ-34: viết <paramref name="scale"/> thành phân số p/q (25/23.976 = 3125/2997), đổi tần số trung gian R = p·k gần
    /// <paramref name="sr"/> rồi <c>asetrate</c> = q·k — số nguyên CHÍNH XÁC, không cần <c>atempo</c>. Tỉ lệ không viết gọn được thành phân
    /// số → dùng lại chuỗi cũ của .bat.
    /// </summary>
    public static string StretchFilter(int sr, double scale)
    {
        var fr = Fraction.FromDouble(scale).LimitDenominator(100000);
        if (Math.Abs(fr.ToDouble() - scale) <= 1e-9 * Math.Abs(scale))
        {
            var p = (long)fr.Numerator;
            var q = (long)fr.Denominator;
            var k = Math.Max(1L, (long)Math.Round((double)sr / p));
            var r = p * k;
            if (sr / 4.0 <= r && r <= sr * 4.0)
                return string.Create(CultureInfo.InvariantCulture, $"aresample={r},asetrate={q * k},aresample={sr}");
        }
        var nRate = (long)Math.Round(sr / scale);
        var flt = string.Create(CultureInfo.InvariantCulture, $"asetrate={nRate},aresample={sr}");
        var fix = sr / (nRate * scale);
        if (Math.Abs(fix - 1.0) > 0.0000001)
            flt += ",atempo=" + Timing.FmtInv(fix, 10);
        return flt;
    }

    /// <summary><c>stretch_factor</c>: hệ số thời lượng mà chuỗi lọc tạo ra (để kiểm chứng).</summary>
    public static double StretchFactor(string flt, int sr)
    {
        double rate = sr, factor = 1.0;
        foreach (var part in flt.Split(','))
        {
            var i = part.IndexOf('=', StringComparison.Ordinal);
            var k = i >= 0 ? part[..i] : part;
            var x = Timing.ToDouble(i >= 0 ? part[(i + 1)..] : "");
            switch (k)
            {
                case "aresample":
                    rate = x;
                    break;
                case "asetrate":
                    factor *= rate / x;
                    rate = x;
                    break;
                case "atempo":
                    factor /= x;
                    break;
            }
        }
        return factor;
    }

    /// <summary><c>plan_donor</c> = <c>Get-DonorPlan</c> — chỉ lập kế hoạch (không chạy ffmpeg). <paramref name="overrideMs"/>: độ lệch
    /// bạn sửa tay trong bảng (chỉ cho lượt chạy này, ưu tiên hơn <c>DonorOffsetMs</c>).</summary>
    public static DonorPlan PlanDonor(string video, JDict? j, IReadOnlyList<string> haveLangs, IReadOnlyList<string> pool, Config cfg,
                                      Func<string, JDict?> donorJson, TrackTimes times, bool ffmpegOk, double? overrideMs = null, Job? job = null)
    {
        job ??= Job.Null;
        var plan = new DonorPlan();
        if (!cfg.Bool("UseDonorAudio"))
            return plan;
        var langs = cfg.List("DonorLangs").Select(x => x.ToLowerInvariant()).ToList();
        var want = langs.Where(lg => !(cfg.Bool("DonorSkipIfPresent") && haveLangs.Contains(lg))).ToList();
        if (want.Count == 0)
        {
            if (pool.Count > 0)
                plan.Note = "file đã có " + string.Join(", ", langs.Select(Naming.LangName)) + " → bỏ qua";
            return plan;
        }
        var vstem = Subprep.Stem(video);
        var d = FindDonorFile(vstem, pool);
        if (d is null)
        {
            if (pool.Count > 0)
                plan.Note = $"không có file nguồn khớp với file này trong '{cfg.Str("DonorFolder")}'";
            return plan;
        }
        plan.Donor = d;
        var dj = donorJson(d);
        if (dj is null || dj.Count == 0)
        {
            plan.Note = $"không đọc được {Subprep.Name(d)}";
            return plan;
        }
        var dAudio = Probe.Tracks(dj, "audio");
        var picks = new List<(JDict T, string Lg)>();
        foreach (var lg in want)
        {
            var c = dAudio.Where(t => Probe.TrackLang(t) == lg && !Probe.IsCommentary(t.GetDict("properties"))
                                      && !Probe.IsDescriptive(t.GetDict("properties"))).ToList();
            if (c.Count == 0)
                continue;
            // nhieu track cung ngon ngu -> nhieu kenh nhat, roi bitrate cao nhat
            var best = c.OrderBy(t => -Py.ToInt(Timing.ToDouble(t.GetDict("properties").Get("audio_channels"))))
                        .ThenBy(t => -Probe.AudioBitrate(t).Bps).First();
            picks.Add((best, lg));
        }
        if (picks.Count == 0)
        {
            plan.Note = $"{Subprep.Name(d)} không có audio " + string.Join("/", want.Select(Naming.LangName));
            return plan;
        }
        var tm = MeasureTiming(video, j, d, dj, times, cfg.Int("DonorMinVotes"), cfg.Double("DonorMaxDurationDiff"),
                               cfg.Int("FpsSampleRef"), cfg.Int("FpsSampleTgt"));
        var manual = overrideMs ?? ManualOffset(vstem, cfg.Dict("DonorOffsetMs"));
        if (manual is { } mv)
        {
            (tm.Offset, tm.Sure) = (mv / 1000.0, true);
            tm.How = "chỉnh tay" + (overrideMs is not null ? " (trong bảng)" : " (DonorOffsetMs)");
            plan.Manual = true;
        }
        plan.Timing = tm;
        if (!tm.Sure && !(tm.Close && cfg.Bool("DonorAcceptDurationMatch")))
        {
            plan.Note = $"{tm.How} → KHÔNG ghép để tránh lệch tiếng. Thêm phụ đề (vd tiếng Anh) cho cả hai bản, hoặc nhập độ lệch tay.";
            return plan;
        }
        var offMs = (int)Math.Round(tm.Offset * 1000);
        var stretch = Math.Abs(tm.Scale - 1.0) > 0.0002;
        foreach (var (t, lg) in picks)
        {
            var p = t.GetDict("properties");
            var label = string.Join(" ", new[] { Naming.LangName(lg), Probe.AudioCodecName(t), Probe.ChannelName(p.Get("audio_channels")) }
                                         .Where(x => x.Length > 0));
            var tid = Py.ToInt(t.Get("id", -2L));
            var aIndex = dAudio.FindIndex(x => Py.ToInt(x.Get("id", -1L)) == tid);
            var dt = new DonorTrack(d, Py.ToInt(t.Get("id", 0L)), aIndex < 0 ? 0 : aIndex, lg, label, offMs);
            if (stretch)
            {
                if (!ffmpegOk)
                {
                    job.Error($"      [!] Cần ffmpeg để đổi tốc độ audio nhưng không tìm thấy → bỏ qua {Naming.LangName(lg)}.");
                    continue;
                }
                var sr = Py.ToInt(Timing.ToDouble(p.Get("audio_sampling_frequency")));
                if (sr == 0)
                    sr = 48000;
                var ch = Py.ToInt(Timing.ToDouble(p.Get("audio_channels")));
                if (ch == 0)
                    ch = 2;
                dt.Stretch = true;
                dt.OutChannels = Math.Min(ch, 6);
                dt.Bitrate = dt.OutChannels >= 6 ? "640k" : "256k";
                dt.Filter = StretchFilter(sr, tm.Scale);
                dt.Name = string.Join(" ", new[] { Naming.LangName(lg), "E-AC-3", Probe.ChannelName((long)dt.OutChannels) }.Where(x => x.Length > 0));
            }
            plan.Tracks.Add(dt);
        }
        if (plan.Tracks.Count == 0 && plan.Note.Length == 0)
            plan.Note = "không ghép được (thiếu ffmpeg)";
        return plan;
    }

    /// <summary><c>prepare</c>: lúc thực thi — ffmpeg đổi tốc độ audio ra <c>.mka</c> tạm (chỉ khi khác FPS).</summary>
    public static bool Prepare(DonorTrack track, string ffmpeg, string temp, Job job, ToolRunner? runner = null)
    {
        if (!track.Stretch)
            return true;
        Directory.CreateDirectory(temp);
        var outp = Py.PathJoin(temp, $"a_{Guid.NewGuid():N}"[..10] + ".mka");
        job.Dim($"      [~] ffmpeg đang đổi tốc độ {Naming.LangName(track.Code)} ({track.Filter})...");
        var r = (runner ?? ((a, j, t) => Tools.Run(a, j, t)))(track.FfmpegArgs(ffmpeg, outp), job, null);
        if (r.Code != 0 || !File.Exists(outp))
        {
            job.Error($"      [!] ffmpeg thất bại → bỏ qua {Naming.LangName(track.Code)}.");
            foreach (var ln in Py.SplitLines(r.Output).TakeLast(3))
                job.Dim($"         {ln}");
            return false;
        }
        track.Prepared = outp;
        return true;
    }
}
