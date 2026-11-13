using System.Globalization;
using MuxStudio.Core.Tests.Support;
using static MuxStudio.Core.Tests.Support.PyData;
using static MuxStudio.Core.Tests.Support.SubprepData;

namespace MuxStudio.Core.Tests;

/// <summary>
/// Bước ② đầu–cuối với công cụ THẬT: ffmpeg / ffprobe / mkvmerge / mkvextract (+ alass nếu tìm thấy). Port <c>tests/test_e2e_subprep.py</c>.
/// Thiếu công cụ nào thì test cần nó tự bỏ qua (thoát sớm — xUnit 2 không có bỏ qua động). Công cụ được tìm như nút "Tự tìm"
/// (Desktop\mkvtoolnix, Desktop\alass-windows64\bin\alass…).
/// </summary>
[Collection(GlobalStateCollection.Name)]
public class SubprepE2ETests
{
    private static readonly Lazy<Dictionary<string, string?>> T = new(() =>
        new[] { "mkvmerge", "mkvextract", "ffmpeg", "ffprobe", "alass" }
            .ToDictionary(n => n, n => Py.Which(n) ?? Tools.FindTool(n, new Config())));

    private static bool NeedMkv => T.Value["mkvmerge"] is not null && T.Value["mkvextract"] is not null && T.Value["ffmpeg"] is not null;

    private static bool NeedFf => T.Value["mkvmerge"] is not null && T.Value["ffmpeg"] is not null && T.Value["ffprobe"] is not null;

    private static bool NeedAlass => NeedFf && T.Value["alass"] is not null;

    private const string Stem = "[Judas] Show - S01E01 [1080p]";

    private static readonly Lazy<int> MkvMajor = new(() => Tools.MkvmergeVersion(T.Value["mkvmerge"]!).Major);

    /// <summary><c>make_mkv</c>: <paramref name="subs"/> = (file, ngôn ngữ, tên track, forced); audio: eng (không Default) + cze (Default).</summary>
    private static void MakeMkv(string path, (string File, string Lang, string Name, bool Forced)[]? subs = null, int dur = 3, bool audio = true)
    {
        var @base = Path.Combine(Path.GetDirectoryName(path)!, "base.mkv");
        var d = dur.ToString(CultureInfo.InvariantCulture);
        List<string> cmd = [T.Value["ffmpeg"]!, "-v", "error", "-y", "-f", "lavfi", "-i", $"color=c=black:s=64x36:r=24000/1001:d={d}"];
        if (audio)
        {
            cmd.AddRange(["-f", "lavfi", "-i", $"sine=frequency=440:duration={d}", "-f", "lavfi", "-i", $"sine=frequency=880:duration={d}",
                          "-map", "0", "-map", "1", "-map", "2", "-c:a", "aac",
                          "-metadata:s:a:0", "language=eng", "-metadata:s:a:1", "language=cze",
                          "-disposition:a:0", "0", "-disposition:a:1", "default"]);
        }
        cmd.AddRange(["-c:v", "mpeg4", @base]);
        var r = Tools.Run(cmd, timeout: TimeSpan.FromMinutes(2));
        Assert.True(r.Code == 0, r.Output);
        List<string> mk = [T.Value["mkvmerge"]!, "-q", "-o", path, @base];
        foreach (var (f, lang, name, forced) in subs ?? [])
        {
            mk.AddRange(["--language", $"0:{lang}"]);
            if (name.Length > 0)
                mk.AddRange(["--track-name", $"0:{name}"]);
            if (forced)
                mk.AddRange([MkvMajor.Value >= 74 ? "--forced-display-flag" : "--forced-track", "0:1"]);
            mk.Add(f);
        }
        var m = Tools.Run(mk, timeout: TimeSpan.FromMinutes(2));
        Assert.True(m.Code is 0 or 1, m.Output);
        File.Delete(@base);
    }

    private static PrepSession Sess(string root, FakeAlass? fa = null, IEnumerable<KeyValuePair<string, object?>>? cfg = null)
    {
        var t = T.Value;
        var alass = fa?.Cmd ?? (t["alass"] is { } a ? [a] : null);
        return new PrepSession(root, new Config(cfg), new Job(), new ToolPaths(t["mkvmerge"], t["mkvextract"], t["ffmpeg"], t["ffprobe"], alass));
    }

    private static void Near(IReadOnlyList<double> expected, IReadOnlyList<double>? actual, double abs)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
            Assert.True(Math.Abs(expected[i] - actual[i]) <= abs, $"[{i}] {expected[i]} != {actual[i]}");
    }

    [Fact]
    public void Extract_refs_prefers_english_non_sdh()
    {
        if (!NeedMkv)
            return; // thiếu mkvmerge/mkvextract/ffmpeg
        using var tmp = new TempDir();
        var movie = tmp.MovieDir();
        var t = new[] { "en", "sdh", "cs", "forced" }.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => Synth.MakeRefTimes(200, seed: x.i));
        var files = t.ToDictionary(kv => kv.Key, kv => WriteSrt(Path.Combine(movie, $"_{kv.Key}.srt"), kv.Value));
        MakeMkv(Path.Combine(movie, $"{Stem}.mkv"), [(files["forced"], "eng", "Forced", true), (files["cs"], "cze", "", false),
                                                     (files["sdh"], "eng", "English SDH", false), (files["en"], "eng", "", false)]);
        foreach (var f in files.Values)
            File.Delete(f);
        using (var s = Sess(movie))
        {
            var rows = s.PlanExtractRefs();
            Assert.False(Directory.Exists(Path.Combine(movie, "srt_goc"))); // lập kế hoạch không ghi gì
            var row = Assert.Single(rows);
            Assert.True(row.Ok && Path.GetFileName(row.Final) == $"{Stem}.srt" && !row.Track!.Sdh);
            var rep = s.ExtractRefs(rows);
            Assert.Equal(1, rep.Ok);
        }
        Near(t["en"], Srt.CueTimes(Path.Combine(movie, "srt_goc", $"{Stem}.srt")), 0.0011);
        using (var s = Sess(movie, cfg: [new("RefSubLangs", L("cs", "en"))]))
            Assert.Equal("đã có mốc", Assert.Single(s.PlanExtractRefs()).Reason); // SkipExistingRef
    }

    [Fact]
    public void Extract_refs_czech_priority_and_pgs_only()
    {
        // TĐ-04: track 'cze' khớp 'cs'. MKV chỉ có PGS → đọc mốc thời gian, không cần OCR
        if (!NeedMkv)
            return;
        using var tmp = new TempDir();
        var movie = tmp.MovieDir();
        var t = Synth.MakeRefTimes(150, seed: 3);
        var cs = WriteSrt(Path.Combine(movie, "_cs.srt"), t);
        var en = WriteSrt(Path.Combine(movie, "_en.srt"), Synth.MakeRefTimes(150, seed: 4));
        MakeMkv(Path.Combine(movie, "A.mkv"), [(en, "eng", "", false), (cs, "cze", "", false)], audio: false);
        var sup = Path.Combine(movie, "_p.sup");
        File.WriteAllBytes(sup, Synth.PgsStream([.. t.SelectMany(x => new[] { (x, "show"), (x + 1.0, "hide") })]));
        MakeMkv(Path.Combine(movie, "B.mkv"), [(sup, "eng", "", false)], audio: false);
        foreach (var f in new[] { cs, en, sup })
            File.Delete(f);
        using (var s = Sess(movie, cfg: [new("RefSubLangs", L("cs", "en"))]))
        {
            var rows = s.PlanExtractRefs().ToDictionary(r => Path.GetFileName(r.Video));
            Assert.True(rows["B.mkv"].Track!.IsPgs);
            Assert.Equal("B.srt", Path.GetFileName(rows["B.mkv"].Final));
            s.ExtractRefs([.. rows.Values]);
        }
        Near(t, Srt.CueTimes(Path.Combine(movie, "srt_goc", "A.srt")), 0.0011);
        Near(t, Srt.CueTimes(Path.Combine(movie, "srt_goc", "B.srt")), 0.0011);
    }

    [Fact]
    public void Audio_mode_always_extracts_wav_and_retry_order()
    {
        // §6.5 + TĐ-19: mốc audio → LUÔN trích WAV (mono 16 kHz PCM 16 bit, cả chế độ tự động);
        // thứ tự thử: WAV+cờ → WAV không cờ → MKV+cờ → MKV không cờ
        if (!NeedFf)
            return;
        using var tmp = new TempDir();
        using var fa = new FakeAlass();
        var movie = tmp.MovieDir();
        MakeMkv(Path.Combine(movie, $"{Stem}.mkv"));
        WriteSrt(Path.Combine(movie, "srt_can_sync", "vi", $"{Stem}.vi.srt"), Synth.MakeRefTimes(60, seed: 1));
        fa.Set(("fail", L(D(("ref", "any"), ("flags", true)), D(("ref", "any"), ("flags", false)))));
        SyncReport rep;
        using (var s = Sess(movie, fa))
        {
            var p = Assert.Single(s.PlanSync("audio", new AudioChoice("auto")));
            Assert.Equal(1, p.AudioIndex); // track Default (cze), không phải track đầu
            Assert.Contains("(Default)", p.AudioDesc, StringComparison.Ordinal);
            Assert.Contains("-map 0:a:1 -vn -ac 1 -ar 16000 -c:a pcm_s16le", s.PreviewCommands(p)[0], StringComparison.Ordinal);
            rep = s.ExecuteSync([p]);
        }
        var calls = fa.Calls();
        Assert.Equal([("wav", true), ("wav", false), ("mkv", true), ("mkv", false)],
                     calls.Select(c => (((string)c["ref"]!).EndsWith(".wav", StringComparison.Ordinal) ? "wav" : "mkv", FakeAlass.Flags(c).Count > 0)));
        var wav = (OrderedDictionary<string, object?>)calls[0]["wav"]!;
        Assert.Equal((1L, 16000L, 16L), ((long)wav["channels"]!, (long)wav["rate"]!, (long)wav["bits"]!));
        Assert.Equal(["--no-split", "--disable-fps-guessing"], FakeAlass.Flags(calls[0]));
        Assert.Equal(1, rep.Errors);
    }

    [Fact]
    public void Audio_choice_index_and_lang()
    {
        if (!NeedFf)
            return;
        using var tmp = new TempDir();
        using var fa = new FakeAlass();
        var movie = tmp.MovieDir();
        MakeMkv(Path.Combine(movie, $"{Stem}.mkv"));
        WriteSrt(Path.Combine(movie, "srt_can_sync", "vi", $"{Stem}.vi.srt"), Synth.MakeRefTimes(60, seed: 1));
        using (var s = Sess(movie, fa))
        {
            Assert.Equal(["eng", "cze"], s.SampleAudioStreams().Streams.Select(a => a.Lang));
            Assert.Equal(0, s.PlanSync("audio", new AudioChoice("lang", "en"))[0].AudioIndex);
        }
        using (var s = Sess(movie, fa))
            Assert.Equal(0, s.PlanSync("audio", new AudioChoice("index", 0))[0].AudioIndex);
        using (var s = Sess(movie, fa)) // số thứ tự không có -> audio Default, vẫn trích WAV
        {
            var p = s.PlanSync("audio", new AudioChoice("index", 5))[0];
            Assert.Equal(1, p.AudioIndex);
            Assert.StartsWith("không có #5", p.AudioDesc, StringComparison.Ordinal);
            s.ExecuteSync([p]);
        }
        Assert.EndsWith(".wav", (string)fa.Calls()[0]["ref"]!, StringComparison.Ordinal);
    }

    [Fact]
    public void All_audio_streams_lists_every_video()
    {
        // TĐ-35: audio track của MỌI file phim (không chỉ file mẫu), kèm nhãn tập
        if (!NeedFf)
            return;
        using var tmp = new TempDir();
        using var fa = new FakeAlass();
        var movie = tmp.MovieDir();
        MakeMkv(Path.Combine(movie, "Show [x] S01E01.mkv"));
        MakeMkv(Path.Combine(movie, "Show [x] S01E02.mkv"), audio: false);
        List<VideoAudio> files;
        using (var s = Sess(movie, fa))
            files = s.AllAudioStreams();
        Assert.Equal([("Show [x] S01E01.mkv", "S01E01", "eng,cze"), ("Show [x] S01E02.mkv", "S01E02", "")],
                     files.Select(f => (Path.GetFileName(f.Video), f.EpLabel, string.Join(',', f.Streams.Select(a => a.Lang)))));
        Assert.Equal([("eng", "aac", 1), ("cze", "aac", 1)], files[0].Layout);
        Assert.Equal([1, 1], Subprep.AudioLayoutGroups(files).Select(g => g.Count));
    }

    [Fact]
    public void Real_alass_sub_mode_recovers_offset()
    {
        if (!NeedAlass)
            return; // không tìm thấy alass thật
        using var tmp = new TempDir();
        var movie = tmp.MovieDir();
        var @ref = Synth.MakeRefTimes(500, seed: 11, minGap: 1.5, maxGap: 8);
        // phim phải DÀI bằng phụ đề (~40 phút): phim 3 s -> sức khoẻ báo đúng "dòng cuối vượt thời lượng phim" (mức 2)
        MakeMkv(Path.Combine(movie, $"{Stem}.mkv"), audio: false, dur: (int)@ref[^1] + 30);
        WriteSrt(Path.Combine(movie, "srt_goc", $"{Stem}.srt"), @ref);
        WriteSrt(Path.Combine(movie, "srt_can_sync", "vi", $"{Stem}.vi.srt"), [.. @ref.Select(r => r + 7.5)]);
        using var s = Sess(movie);
        var pairs = s.PlanSync("sub");
        var rep = s.ExecuteSync(pairs);
        Assert.True(rep.Ok == 1, string.Join("\n", s.Job.Lines.TakeLast(10).Select(l => l.Message)));
        Near(@ref, Srt.CueTimes(Path.Combine(movie, $"{Stem}.vi.srt")), 0.1);
        Assert.Equal(0, pairs[0].Result!.Health!.Level);
    }

    [Fact]
    public void Real_alass_audio_mode_runs_on_wav()
    {
        // alass thật nhận WAV do ffmpeg trích (không lỗi 'codec_long_name'); chỉ kiểm tra chạy được
        if (!NeedAlass)
            return;
        using var tmp = new TempDir();
        var movie = tmp.MovieDir();
        MakeMkv(Path.Combine(movie, $"{Stem}.mkv"), dur: 20);
        WriteSrt(Path.Combine(movie, "srt_can_sync", "vi", $"{Stem}.vi.srt"), [.. Enumerable.Range(0, 15).Select(i => 1.0 + i)]);
        using var s = Sess(movie, cfg: [new("MaxShiftSeconds", 0L)]);
        var p = Assert.Single(s.PlanSync("audio"));
        s.ExecuteSync([p]);
        Assert.True(p.Result!.Code == 0 && p.Result.Attempt == 0, string.Join("\n", p.Result.OutputTail));
    }
}
