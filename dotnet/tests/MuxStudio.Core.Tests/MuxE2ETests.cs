using System.Globalization;
using System.Text.RegularExpressions;
using MuxStudio.Core.Tests.Support;
using static MuxStudio.Core.Tests.Support.PyData;

namespace MuxStudio.Core.Tests;

/// <summary>
/// Bước ③ đầu–cuối với ffmpeg / ffprobe / mkvmerge / mkvextract THẬT (thiếu thì test tự thoát sớm). Port <c>tests/test_e2e_mux.py</c>.
/// <list type="bullet">
/// <item>TIẾNG BÍP (§8): phim 23,976 fps có phụ đề tiếng Anh; "bản nguồn" 25 fps có audio tiếng Séc là tiếng bíp đặt đúng từng câu thoại,
/// lệch +1,5 s. Ghép → đo vị trí tiếng bíp trong file kết quả bằng <c>silencedetect</c>: sai số &lt; 20 ms ở đầu và cuối phim. Thêm ca
/// cùng FPS, lệch âm (không encode lại). Audio gốc để PCM (AAC có "đoạn mồi" ~21 ms tuỳ bản ffmpeg).</item>
/// <item>Mux thật: mã thoát ≥ 2 → xoá file dở dang.</item>
/// </list>
/// </summary>
[Collection(GlobalStateCollection.Name)]
public partial class MuxE2ETests
{
    private static readonly Lazy<Dictionary<string, string?>> T = new(() =>
        new[] { "mkvmerge", "mkvextract", "ffmpeg", "ffprobe" }.ToDictionary(n => n, n => Py.Which(n) ?? Tools.FindTool(n, new Config())));

    private static bool HaveAll => T.Value.Values.All(v => v is not null);

    private const int Sr = 48000;
    private const double Beep = 0.15;

    private static ToolPaths Paths => new(T.Value["mkvmerge"], T.Value["mkvextract"], T.Value["ffmpeg"], T.Value["ffprobe"]);

    private static void Run(List<string> cmd, params long[] ok)
    {
        var r = Tools.Run(cmd, timeout: TimeSpan.FromMinutes(5));
        Assert.True(ok.Contains(r.Code), r.Output);
    }

    /// <summary><c>beep_wav</c>: WAV mono 48 kHz: im lặng, tiếng bíp 1 kHz dài 150 ms tại mỗi mốc.</summary>
    private static void BeepWav(string path, IEnumerable<double> beeps, double dur)
    {
        var n = (int)(dur * Sr);
        var buf = new short[n];
        var tone = Enumerable.Range(0, (int)(Beep * Sr)).Select(i => (short)(int)(16000 * Math.Sin(2 * Math.PI * 1000 * i / Sr))).ToArray();
        foreach (var t in beeps)
        {
            var i = (int)Math.Round(t * Sr);
            var k = Math.Max(0, Math.Min(tone.Length, n - i));
            Array.Copy(tone, 0, buf, i, k);
        }
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        var data = n * 2;
        w.Write("RIFF"u8);
        w.Write(36 + data);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(Sr);
        w.Write(Sr * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(data);
        foreach (var s in buf)
            w.Write(s);
    }

    /// <summary><c>make_video</c>: video đen + audio tiếng Anh: im lặng (AAC), hoặc lấy từ <paramref name="audioWav"/> (giữ PCM).</summary>
    private static void MakeVideo(string path, string fps, double dur, string? audioWav = null)
    {
        var d = Py.Str(dur);
        List<string> src = audioWav is not null ? ["-i", audioWav] : ["-f", "lavfi", "-i", $"anullsrc=r={Sr}:cl=mono"];
        Run([T.Value["ffmpeg"]!, "-v", "error", "-y", "-f", "lavfi", "-i", $"color=c=black:s=64x36:r={fps}:d={d}", .. src, "-t", d,
             "-c:v", "mpeg4", "-c:a", audioWav is not null ? "pcm_s16le" : "aac", "-metadata:s:a:0", "language=eng", path], 0);
    }

    private static void MakeMkv(string outp, string video, params (string File, string Lang)[] extra)
    {
        List<string> cmd = [T.Value["mkvmerge"]!, "-q", "-o", outp, video];
        foreach (var (f, lang) in extra)
            cmd.AddRange(["--language", $"0:{lang}", f]);
        Run(cmd, 0, 1);
    }

    [GeneratedRegex(@"silence_end: ([0-9.]+)")]
    private static partial Regex SilenceEndRe();

    private static List<double> BeepOnsets(string mkv, int aIndex)
    {
        var r = Tools.Run([T.Value["ffmpeg"]!, "-v", "info", "-i", mkv, "-map", $"0:a:{aIndex}", "-af", "silencedetect=noise=-40dB:d=0.05",
                           "-f", "null", "-"], timeout: TimeSpan.FromMinutes(5));
        return [.. SilenceEndRe().Matches(r.Stderr).Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))];
    }

    /// <summary><c>build</c>: thư viện — phim của bạn (mốc M_i) + bản nguồn trong audio_nguon (mốc D_i = M_i / scale + shift).</summary>
    private static (string Stem, List<double> Times) Build(string root, string mineFps, double donorScale, double donorShift, int n = 45, long seed = 21)
    {
        var times = Synth.MakeRefTimes(n, start: 10.0, seed: seed, minGap: 4.0, maxGap: 8.0);
        var dur = times[^1] + 15;
        var work = Path.Combine(root, "_tao");
        Directory.CreateDirectory(work);
        var mineV = Path.Combine(work, "mine.mkv");
        var mineWav = Path.Combine(work, "en.wav");
        BeepWav(mineWav, times, dur); // audio goc cua phim: bip dung tung cau thoai
        MakeVideo(mineV, mineFps, dur, mineWav);
        var en = Path.Combine(work, "en.srt");
        File.WriteAllText(en, Synth.SrtText(times, 1.0), Py.Utf8NoBom);
        const string stem = "Phim.Bip.2020.1080p";
        MakeMkv(Path.Combine(root, stem + ".mkv"), mineV, (en, "eng"));
        var dTimes = times.Select(m => m / donorScale + donorShift).ToList();
        var dDur = dur / donorScale + donorShift + 5;
        var donorV = Path.Combine(work, "donor.mkv");
        MakeVideo(donorV, donorScale != 1.0 ? "25" : mineFps, dDur);
        var wav = Path.Combine(work, "cs.wav");
        BeepWav(wav, dTimes, dDur);
        var den = Path.Combine(work, "den.srt");
        File.WriteAllText(den, Synth.SrtText(dTimes, 1.0), Py.Utf8NoBom);
        Directory.CreateDirectory(Path.Combine(root, "audio_nguon"));
        // file nguon: video + audio Sec (tieng bip PCM) + phu de Anh cua chinh ban nguon (bo audio Anh cua video nen)
        Run([T.Value["mkvmerge"]!, "-q", "-o", Path.Combine(root, "audio_nguon", $"{stem}.CZ.mkv"), "--no-audio", donorV,
             "--language", "0:cze", wav, "--language", "0:eng", den], 0, 1);
        Directory.Delete(work, recursive: true);
        return (stem, times);
    }

    private static (MuxPlan Plan, MuxReport Rep) RunMux(string root)
    {
        using var s = new MuxSession(root, NamingDbTests.Cfg(("DonorLangs", L("cs"))), new Dictionary<string, string>(), new Job(), toolPaths: Paths);
        var plans = s.PlanAll("none");
        Assert.Single(plans);
        Assert.True(plans[0].Status == "ok", string.Join("; ", plans[0].Reasons));
        var rep = s.Execute(plans);
        return (plans[0], rep);
    }

    [Theory]
    [InlineData("pal_plus_1_5s")]
    [InlineData("same_fps_minus_2s")]
    public void Beep_alignment_under_20ms(string kase)
    {
        if (!HaveAll)
            return;
        using var tmp = new TempDir();
        var md = tmp.MovieDir();
        var (_, times) = kase == "pal_plus_1_5s" ? Build(md, "24000/1001", 25 / 23.976, 1.5) : Build(md, "24000/1001", 1.0, -2.0, seed: 22);
        var (plan, rep) = RunMux(md);
        Assert.True(rep.Ok == 1, string.Join("; ", plan.Notes));
        var dt = Assert.Single(plan.Donor.Tracks);
        if (kase == "pal_plus_1_5s")
        {
            Assert.True(dt.Stretch);
            Assert.Contains("asetrate=", dt.Filter, StringComparison.Ordinal);
            Assert.DoesNotContain("atempo", dt.Filter, StringComparison.Ordinal);
            Assert.True(plan.Donor.Timing!.Votes >= 20);
        }
        else
        {
            Assert.False(dt.Stretch);
            Assert.Equal(2000, dt.OffsetMs); // cung FPS: --sync, KHONG encode lai
        }
        var outp = plan.OutPath!;
        var (orig, dub) = (BeepOnsets(outp, 0), BeepOnsets(outp, 1)); // cung mot lan giai ma
        Assert.True(dub.Count >= times.Count - 2, string.Join(", ", dub.Take(5)));
        foreach (var want in new[] { times[0], times[^1] }) // dau phim va cuoi phim
        {
            var o = orig.MinBy(x => Math.Abs(x - want));
            var d = dub.MinBy(x => Math.Abs(x - want));
            Assert.True(Math.Abs(o - want) < 0.030, $"{want} {o}"); // bip cua audio goc dung cau thoai
            Assert.True(Math.Abs(d - o) < 0.020, $"{want} {o} {d}"); // yeu cau §8: < 20 ms
            Assert.True(Math.Abs(d - o) < 0.010, $"{want} {o} {d}"); // thuc te: ffmpeg moi < 2 ms; ffmpeg cu ~5,3 ms
        }
    }

    [Fact]
    public void Real_mux_exit_code_error_deletes_partial()
    {
        if (!HaveAll)
            return;
        using var tmp = new TempDir();
        var md = tmp.MovieDir();
        const string stem = "Phim.Loi.2020";
        var v = Path.Combine(md, "v.mkv");
        MakeVideo(v, "24000/1001", 2);
        File.Move(v, Path.Combine(md, stem + ".mkv"));
        var sub = Path.Combine(md, $"{stem}.vi.srt");
        File.WriteAllText(sub, Synth.SrtText([0.5], 0.5), Py.Utf8NoBom);
        MuxPlan plan;
        MuxReport rep;
        using (var s = new MuxSession(md, NamingDbTests.Cfg(("UseDonorAudio", false)), new Dictionary<string, string>(), new Job(), toolPaths: Paths))
        {
            plan = Assert.Single(s.PlanAll("none"));
            File.WriteAllBytes(sub, [0x00, 0x01, .. "khong phai phu de"u8]); // mkvmerge khong doc duoc -> ma thoat 2
            rep = s.Execute([plan]);
        }
        Assert.Equal(1, rep.Failed);
        Assert.True(plan.Result!.Code >= 2);
        Assert.DoesNotContain(Directory.GetFiles(Path.Combine(md, "Output")), p => Path.GetExtension(p) == ".mkv"); // khong de lai file do dang
    }
}
