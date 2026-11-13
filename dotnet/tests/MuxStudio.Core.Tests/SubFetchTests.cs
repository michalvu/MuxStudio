using System.Text;
using System.Text.RegularExpressions;
using MuxStudio.Core.Tests.Support;
using static MuxStudio.Core.Tests.Support.PyData;
using JDict = System.Collections.Generic.OrderedDictionary<string, object?>;

namespace MuxStudio.Core.Tests;

/// <summary>Bước ① đầu–cuối với API giả (§6.11, §8). Port <c>tests/test_subfetch.py</c>.</summary>
[Collection(GlobalStateCollection.Name)] // bang ngon ngu (RegisterLanguage / LangFromWord)
public sealed class SubFetchTests : IDisposable
{
    private static readonly JDict FpsJson = D(("tracks", L(D(("id", 0L), ("type", "video"), ("properties", D(("default_duration", 41708333L)))))));
    private const string Ep1 = "Show.S01E01.1080p.WEB-DL";
    private const string Ep2 = "Show.S01E02.1080p.WEB-DL";
    private const string Dune = "Dune.Part.Two.2024.2160p";

    private readonly TempDir _tmp = new();
    private readonly List<FetchSession> _sessions = [];

    public void Dispose()
    {
        foreach (var s in _sessions)
            s.Dispose();
        _tmp.Dispose();
        Naming.ResetLanguages();
    }

    private static Config Cfg(params (string K, object? V)[] kv) => NamingDbTests.Cfg(kv);

    private static FakeResponse Ok(object data) => FakeResponse.Json(200, data);

    private static void WriteText(string p, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text, Py.Utf8NoBom);
    }

    private static string AssText(IEnumerable<double> times)
    {
        var lines = new List<string> { "[Script Info]", "ScriptType: v4.00+", "", "[Events]",
                                        "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text" };
        foreach (var t in times)
        {
            var h = Math.Floor(t / 3600);
            var rem = t - h * 3600;
            var m = Math.Floor(rem / 60);
            var s = rem - m * 60;
            lines.Add($"Dialogue: 0,{(int)h}:{(int)m:00}:{Py.Fixed(s, 2).PadLeft(5, '0')},{(int)h}:{(int)m:00}:{Py.Fixed(s + 1, 2).PadLeft(5, '0')},Default,,0,0,0,,x");
        }
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>Thư viện phim giả: 2 tập phim bộ + 1 phim lẻ, phụ đề gốc trong srt_goc.</summary>
    private sealed class Lib
    {
        public Lib(string root)
        {
            Root = root;
            Ref = new Dictionary<string, List<double>>
            {
                [Ep1] = Synth.MakeRefTimes(600, seed: 1), [Ep2] = Synth.MakeRefTimes(600, seed: 2), [Dune] = Synth.MakeRefTimes(800, seed: 3),
            };
            foreach (var stem in Ref.Keys)
            {
                File.WriteAllBytes(Path.Combine(root, stem + ".mkv"), [0x1a, 0x45, 0xdf, 0xa3]);
                WriteText(Path.Combine(root, "srt_goc", stem + ".srt"), Synth.SrtText(Ref[stem]));
            }
            // video trong Output / audio_nguon / srt_pool: KHONG duoc tai (Q6)
            foreach (var d in new[] { @"Output\Show (2019) [tmdbid-1]\Season 01", "audio_nguon", "srt_pool" })
            {
                Directory.CreateDirectory(Path.Combine(root, d));
                File.WriteAllBytes(Path.Combine(root, d, "Show - S01E01.mkv"), []);
            }
        }

        public string Root { get; }
        public Dictionary<string, List<double>> Ref { get; }

        public List<double> Sub(string stem, double offset, double scale = 1.0, long seed = 9, double keep = 0.9) =>
            Synth.TransformTimes(Ref[stem], scale, offset, keep: keep, seed: seed);
    }

    private Lib NewLib() => new(_tmp.MovieDir());

    private static Dictionary<string, string> SsRoutes(FakeHttp http, Lib lib, bool packHasEp2 = true, params JDict[] extraSubs)
    {
        var ep1Vi = lib.Sub(Ep1, 2.5, seed: 11);
        var ep2Vi = lib.Sub(Ep2, -1.25, seed: 12);
        var pack = new Dictionary<string, string>
        {
            ["Show.S01E01.vi.srt"] = Synth.SrtText(ep1Vi), ["Show.S01E02.vi.forced.srt"] = Synth.SrtText(ep2Vi.Take(30).ToList()),
        };
        if (packHasEp2)
            pack["Show.S01E02.vi.srt"] = Synth.SrtText(ep2Vi);
        var preview = Synth.SrtText(ep1Vi.Order().Take(5).ToList());
        var subs = L(
            // ban nguoi dung tu dang: de TRONG hearingImpaired/foreignParts/framerate -> van phai duoc nhan
            D(("subtitleId", 1L), ("language", "vietnamese"), ("releaseInfo", L("Show.S01.1080p.WEB-DL")), ("framerate", ""),
              ("hearingImpaired", null), ("foreignParts", null), ("files", 2L), ("downloads", 50L), ("preview", preview)),
            D(("subtitleId", 2L), ("language", "english"), ("releaseInfo", L("Show.S01E01.1080p.WEB-DL-GRP")), ("framerate", "23.976"),
              ("files", 1L), ("downloads", 900L)),
            D(("subtitleId", 3L), ("language", "english"), ("releaseInfo", L("Show.S01E01.PAL.DVDRip")), ("framerate", "25"), ("files", 1L)),
            D(("subtitleId", 4L), ("language", "english"), ("releaseInfo", L("Show.S01E01.SDH")), ("framerate", "23.976"),
              ("hearingImpaired", true), ("files", 1L)));
        subs.AddRange(extraSubs);
        http.Add("GET", @"subsource\.net/api/v1/movies/search", call =>
        {
            var q = call.Params.GetValueOrDefault("q", "");
            if (q == "Show")
                return Ok(D(("data", L(D(("movieId", 100L), ("title", "Show"), ("season", 1L), ("subtitleCount", 4L))))));
            if (q.StartsWith("Dune", StringComparison.Ordinal))
                return Ok(D(("data", L(D(("movieId", 200L), ("title", "Dune: Part Two"), ("releaseYear", 2024L))))));
            return Ok(D(("data", L())));
        });
        http.Add("GET", @"subsource\.net/api/v1/subtitles$", call =>
            Ok(D(("data", call.Params["movieId"] == "100" ? subs : L()), ("pagination", D(("pages", 1L))))));
        http.Add("GET", @"subsource\.net/api/v1/subtitles/1/download",
                 FakeResponse.Bytes(200, ZipData.Of([.. pack.Select(kv => (kv.Key, (object)kv.Value))])));
        http.Add("GET", @"subsource\.net/api/v1/subtitles/2/download",
                 FakeResponse.Bytes(200, ZipData.Of(("Show.S01E01.en.ass", AssText(lib.Sub(Ep1, 0.4, seed: 13))))));
        return pack;
    }

    private static void OsRoutes(FakeHttp http, Lib lib, long remaining = 100, List<object?>? duneVi = null)
    {
        http.Add("POST", @"opensubtitles\.com/api/v1/login", Ok(D(("token", "tok"), ("base_url", "api.opensubtitles.com"),
            ("user", D(("allowed_downloads", 100L), ("vip", false), ("level", "Sub leecher"))))));
        http.Add("GET", @"opensubtitles\.com/api/v1/infos/user", Ok(D(("data", D(("remaining_downloads", remaining), ("allowed_downloads", 100L))))));
        var items = duneVi ?? L(
            D(("id", "m"), ("attributes", D(("release", "Dune.Part.Two.2024.2160p.WEB"), ("fps", 23.976), ("download_count", 9000L),
                                             ("machine_translated", true), ("files", L(D(("file_id", 501L))))))),
            D(("id", "n"), ("attributes", D(("release", "Dune.Part.Two.2024.1080p"), ("fps", 23.976), ("download_count", 10L),
                                             ("files", L(D(("file_id", 502L))))))));
        http.Add("GET", @"opensubtitles\.com/api/v1/subtitles", call =>
            call.Params.GetValueOrDefault("languages") == "vi" && call.Params.GetValueOrDefault("query", "").StartsWith("Dune", StringComparison.Ordinal)
                ? Ok(D(("data", items))) : Ok(D(("data", L()))));
        http.Add("POST", @"opensubtitles\.com/api/v1/download", call => Ok(D(
            ("link", $"https://dl.opensubtitles.com/{Py.Str(call.JsonDict!["file_id"])}"), ("remaining", remaining - 1), ("reset_time", "5 giờ"))));
        http.Add("GET", @"dl\.opensubtitles\.com/50[12]", FakeResponse.Text(200, Synth.SrtText(lib.Sub(Dune, 7.0, seed: 14))));
    }

    private static readonly Dictionary<string, string> Secrets = new()
    {
        ["SubSourceApiKey"] = "demo-ss", ["OsApiKey"] = "demo-os", ["OsUsername"] = "u", ["OsPassword"] = "p",
    };

    private static Dictionary<string, string> SecretsNoSs => Secrets.Where(kv => kv.Key != "SubSourceApiKey").ToDictionary();

    private (FetchSession S, Job J) Session(Lib lib, FakeHttp http, Config? cfg = null, Dictionary<string, string>? secrets = null,
                                            Func<string, JDict?>? probe = null, IReadOnlyDictionary<string, bool>? sources = null)
    {
        cfg ??= Cfg(("Languages", L("vi", "en")), ("OsExcludeMachine", false));
        var job = new Job();
        var s = new FetchSession(lib.Root, cfg, secrets ?? Secrets, job, http.Client, new Sleeper().Sleep, new ToolPaths(),
                                 probe ?? (_ => FpsJson), sources: sources);
        _sessions.Add(s);
        return (s, job);
    }

    private static FetchItem Item(FetchSession s, string stem, string lang) => s.Items.First(i => i.Video.Stem == stem && i.Lang == lang);

    private static bool Has(Job job, string s) => job.Lines.Any(l => l.Message.Contains(s, StringComparison.Ordinal));

    [Fact]
    public void Full_run()
    {
        var lib = NewLib();
        var http = new FakeHttp();
        var pack = SsRoutes(http, lib);
        OsRoutes(http, lib);
        // file ban tu bo vao srt_can_sync: dung tap 2 (do duoc) + ten giong nhung TAP KHAC
        var sc = Path.Combine(lib.Root, "srt_can_sync", "en");
        WriteText(Path.Combine(sc, "random name from web.srt"), Synth.SrtText(lib.Sub(Ep2, 0.8, seed: 15)));
        WriteText(Path.Combine(sc, "Show.S01E05.1080p.WEB-DL.en.srt"), Synth.SrtText(lib.Sub(Ep2, 0.0, seed: 16)));
        // TD-38: kho noi bo srt_pool DA BO -> file trong do khong con la nguon
        WriteText(Path.Combine(lib.Root, "srt_pool", "en", "Dune.Part.Two.2024.en.srt"), Synth.SrtText(lib.Sub(Dune, -3.0, seed: 17)));

        var (s, job) = Session(lib, http);
        var asked = new List<(string Stem, string Lang, List<string> Rels)>();
        s.Scan();
        Assert.Equal([Dune, Ep1, Ep2], s.Items.Select(i => i.Video.Stem).Distinct().Order(StringComparer.Ordinal)); // Output/audio_nguon/srt_pool bi bo qua
        var ex = Item(s, Ep2, "en").Existing;
        Assert.NotNull(ex);
        Assert.Equal("random name from web.srt", Path.GetFileName(ex.File));
        Assert.True(ex.Votes >= 20);
        s.PlanAll(it =>
        {
            asked.Add((it.Video.Stem, it.Lang, it.Candidates.Select(c => c.Release).ToList()));
            return it.Candidates[0];
        });

        // Dune vi: 2 ban OpenSubtitles -> phai hoi; ban thuong xep TRUOC ban dich may (Q2)
        var a = Assert.Single(asked);
        Assert.Equal((Dune, "vi"), (a.Stem, a.Lang));
        Assert.Equal(["Dune.Part.Two.2024.1080p", "Dune.Part.Two.2024.2160p.WEB"], a.Rels);
        var dv = Item(s, Dune, "vi");
        Assert.Equal([false, true], dv.Candidates.Select(c => c.Machine));
        Assert.Contains("dịch máy", dv.Candidates[1].Info, StringComparison.Ordinal);
        // Dune en: file trong srt_pool KHONG duoc dung nua -> hoi mang (khong co) -> thieu
        var de = Item(s, Dune, "en");
        Assert.Null(de.Chosen);
        Assert.Empty(de.Candidates);
        Assert.Equal("không nguồn nào có", de.Status);
        Assert.Contains(http.CallsTo(@"opensubtitles.*/subtitles$"), c => c.Params.GetValueOrDefault("languages") == "en"
                                                                          && c.Params.GetValueOrDefault("query", "").StartsWith("Dune", StringComparison.Ordinal));
        Assert.DoesNotContain(job.Lines, l => l.Message.Contains("kho", StringComparison.OrdinalIgnoreCase));
        // Tap 1 vi: goi season, uoc tinh tu preview (dau ~), tu lay vi chi 1 ung vien
        var v1 = Item(s, Ep1, "vi");
        Assert.True(v1.Chosen!.IsPack);
        Assert.EndsWith("~", v1.Chosen.OffsetLabel, StringComparison.Ordinal);
        Assert.True(Math.Abs(v1.Chosen.Off!.Offset - 2.5) <= 0.25);
        // Tap 2 vi: goi da tai -> DO THANG file cua tap 2 trong goi; dung lai lua chon (cung season + ngon ngu)
        var v2 = Item(s, Ep2, "vi");
        Assert.Equal(v1.Chosen.Ident, v2.Chosen!.Ident);
        Assert.Equal("dùng lại lựa chọn trước", v2.ChoiceReason);
        Assert.Equal(-1, v2.Chosen.Off!.Total);
        Assert.True(Math.Abs(v2.Chosen.Off.Offset + 1.25) < 0.1);
        // Tap 1 en: ban PAL bi loai (StrictFpsMatch), SDH bi loai; con 1 -> tu lay
        Assert.Equal(["Show.S01E01.1080p.WEB-DL-GRP"], Item(s, Ep1, "en").Candidates.Select(c => c.Release));

        var rep = s.Execute();
        var outp = Path.Combine(lib.Root, "srt_can_sync");
        Assert.Equal((4, 1, 1), (rep.Ok, rep.UsedExisting, rep.Missing));
        // buoc 1 KHONG sua thoi gian: chep nguyen byte
        Assert.Equal(Encoding.UTF8.GetBytes(pack["Show.S01E01.vi.srt"]), File.ReadAllBytes(Path.Combine(outp, "vi", $"{Ep1}.vi.srt")));
        Assert.Equal(Encoding.UTF8.GetBytes(pack["Show.S01E02.vi.srt"]), File.ReadAllBytes(Path.Combine(outp, "vi", $"{Ep2}.vi.srt")));
        Assert.True(File.Exists(Path.Combine(outp, "en", $"{Ep1}.en.ass"))); // Q7: ASS giu la ASS
        Assert.False(File.Exists(Path.Combine(outp, "en", $"{Ep1}.en.srt")));
        Assert.True(File.Exists(Path.Combine(outp, "en", $"{Ep2}.en.srt")));
        Assert.True(File.Exists(Path.Combine(sc, "random name from web.srt"))); // CHEP, khong di chuyen
        Assert.True(File.Exists(Path.Combine(outp, "vi", $"{Dune}.vi.srt")));
        Assert.False(File.Exists(Path.Combine(outp, "en", $"{Dune}.en.srt")));
        Assert.Single(http.CallsTo("subtitles/1/download")); // goi tai MOT lan
        Assert.Equal(99, rep.OsRemaining);
        Assert.NotNull(rep.ReportPath);
        Assert.StartsWith("fetch_", Path.GetFileName(rep.ReportPath), StringComparison.Ordinal);

        // hoan tac: moi file tao/chep -> Thung rac, file goc cua ban con nguyen
        var runs = Undo.ListRuns(lib.Root, "_subfetch_logs", "subfetch");
        var trashed = new List<string>();
        Undo.ExecuteUndo(Undo.PlanUndo(runs[0].Path), trashFn: (p, _) =>
        {
            trashed.Add(p);
            File.Delete(p);
            return true;
        });
        Assert.Equal(5, trashed.Count);
        Assert.True(File.Exists(Path.Combine(sc, "random name from web.srt")));
        Assert.False(File.Exists(Path.Combine(outp, "vi", $"{Ep1}.vi.srt")));
    }

    [Fact]
    public void Episode_specific_choice_not_reused_and_failed_choice_tries_next()
    {
        var lib = NewLib();
        var http = new FakeHttp();
        var ep2En = D(("subtitleId", 7L), ("language", "english"), ("releaseInfo", L("Show.S01E02.1080p.WEB-DL-GRP")), ("framerate", "23.976"), ("files", 1L));
        SsRoutes(http, lib, false, ep2En);
        http.Add("GET", "subtitles/7/download", FakeResponse.Bytes(200, ZipData.Of(("Show.S01E02.en.srt", Synth.SrtText(lib.Sub(Ep2, 0.3))))));
        OsRoutes(http, lib);
        var altVi = D(("subtitleId", 8L), ("language", "vietnamese"), ("releaseInfo", L("Show.S01E02.vi.alt")), ("framerate", ""), ("files", 1L));
        http.Add("GET", "subtitles/8/download", FakeResponse.Bytes(200, ZipData.Of(("Show.S01E02.vi.srt", Synth.SrtText(lib.Sub(Ep2, 0.1))))));
        var (s, job) = Session(lib, http, Cfg(("Languages", L("vi", "en"))));
        s.Scan();
        s.Ss!.ListCache[100] = [.. s.Ss.ListAll(100), altVi];
        s.PlanAll(it => it.Candidates[0]);
        Assert.Equal("Show.S01E02.1080p.WEB-DL-GRP", Item(s, Ep2, "en").Chosen!.Release); // khong dung lai ban rieng cua tap 1
        Assert.Equal("Show.S01.1080p.WEB-DL", Item(s, Ep2, "vi").Chosen!.Release); // nho tu tap 1 (goi season)
        s.Execute();
        Assert.True(Regex.Matches(File.ReadAllText(Path.Combine(lib.Root, "srt_can_sync", "vi", $"{Ep2}.vi.srt")), "-->").Count > 30);
        Assert.Contains(job.Lines, l => l.Message.Contains("Thử bản khác", StringComparison.Ordinal) && l.Message.Contains("Show.S01E02.vi.alt", StringComparison.Ordinal));
        Assert.Null(s.Memory.Get("100|vi")); // ban nho khong dung duoc -> xoa
    }

    [Fact]
    public void No_sources_and_legacy_pool_folder_is_ignored()
    {
        var lib = NewLib();
        var pl = Path.Combine(lib.Root, "srt_pool", "vi", "Dune.Part.Two.2024.vi.srt");
        WriteText(pl, Synth.SrtText(lib.Sub(Dune, 1.0)));
        var http = new FakeHttp();
        var (s, job) = Session(lib, http, Cfg(("Languages", L("vi")), ("UseOpenSubtitles", false)), []);
        s.PlanAll();
        Assert.Equal([Dune, Ep1, Ep2], s.Items.Select(i => i.Video.Stem).Order(StringComparer.Ordinal));
        Assert.All(s.Items, it => Assert.True(it.Chosen is null && it.Candidates.Count == 0));
        Assert.Empty(http.Calls);
        Assert.True(Has(job, "SubSource API key"));
        Assert.False(s.Sources.ContainsKey("pool"));
        var rep = s.Execute();
        Assert.Equal((0, 3), (rep.Ok, rep.Missing));
        Assert.True(File.Exists(pl));
    }

    [Fact]
    public void Keep_paid_downloads_off_trashes_weak_os_file_and_tries_next()
    {
        var lib = NewLib();
        var http = new FakeHttp();
        OsRoutes(http, lib);
        var weak = Synth.SrtText(Synth.MakeRefTimes(300, start: 4300.0, seed: 999)); // gan nhu khong khop
        http.Add("GET", @"dl\.opensubtitles\.com/501", FakeResponse.Text(200, weak));
        var trashed = new List<string>();
        var cfg = Cfg(("Languages", L("vi")), ("OsExcludeMachine", false), ("KeepPaidDownloads", false));
        var (s, _) = Session(lib, http, cfg, SecretsNoSs);
        s.Trash = (p, _) =>
        {
            trashed.Add(Path.GetFileName(p));
            return true;
        };
        s.PlanAll(it => it.Candidates.First(c => c.Machine)); // chon ban 501 (yeu)
        var rep = s.Execute();
        Assert.Equal(["os_501.srt"], trashed);
        Assert.Equal("OpenSub", Item(s, Dune, "vi").ResultSrc);
        Assert.Equal("n", Item(s, Dune, "vi").Chosen!.PayloadDict!["id"]);
        Assert.Equal(1, rep.Ok);
    }

    [Fact]
    public void Non_interactive_and_strict_unknown_fps()
    {
        var lib = NewLib();
        var http = new FakeHttp();
        SsRoutes(http, lib);
        OsRoutes(http, lib);
        var (s, _) = Session(lib, http, Cfg(("Languages", L("vi")), ("Interactive", false), ("AllowUnknownFps", false), ("OsExcludeMachine", false)));
        s.PlanAll();
        Assert.Empty(Item(s, Ep1, "vi").Candidates); // goi vi khong khai bao fps -> loai khi AllowUnknownFps tat
        Assert.Equal("Dune.Part.Two.2024.1080p", Item(s, Dune, "vi").Chosen!.Release); // tu dong: ban dung dau
    }

    [Fact]
    public void Os_quota_low_stops_downloads()
    {
        var lib = NewLib();
        var http = new FakeHttp();
        OsRoutes(http, lib, remaining: 5);
        var (s, job) = Session(lib, http, Cfg(("Languages", L("vi")), ("OsExcludeMachine", false)), SecretsNoSs);
        s.PlanAll(it => it.Candidates[0]);
        var rep = s.Execute();
        Assert.Equal(0, rep.Ok);
        Assert.Empty(http.CallsTo("/download", "POST"));
        Assert.True(Has(job, "DỪNG dùng nguồn này"));
    }

    [Fact]
    public void Skip_language_button()
    {
        var lib = NewLib();
        var http = new FakeHttp();
        OsRoutes(http, lib);
        var (s, _) = Session(lib, http, Cfg(("Languages", L("vi")), ("OsExcludeMachine", false)), SecretsNoSs);
        s.PlanAll(_ => null); // bam "Bo qua ngon ngu nay"
        var rep = s.Execute();
        Assert.True(Item(s, Dune, "vi").Skip);
        Assert.Equal((1, 0), (rep.Skipped, rep.Ok));
    }

    [Fact]
    public void Skip_if_exists_and_srt_goc_before_mkv()
    {
        var lib = NewLib();
        var dest = Path.Combine(lib.Root, "srt_can_sync", "vi", $"{Dune}.vi.ass");
        WriteText(dest, "x");
        var (s, _) = Session(lib, new FakeHttp(), Cfg(("Languages", L("vi"))), []);
        s.Scan();
        var it = Item(s, Dune, "vi");
        Assert.Equal(dest, it.Already); // da co (ke ca .ass) -> bo qua
        // TD-08: moc goc lay tu srt_goc du khong co mkvmerge/mkvextract
        Assert.Contains("dùng lại srt_goc", it.Video.Ref!.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void Ref_found_by_episode_in_srt_goc()
    {
        var lib = NewLib();
        var g = Path.Combine(lib.Root, "srt_goc");
        File.Move(Path.Combine(g, $"{Ep1}.srt"), Path.Combine(g, "[Group] Show - S01E01 khac ten.srt"));
        var (s, _) = Session(lib, new FakeHttp(), Cfg(("Languages", L("vi"))), []);
        s.Scan();
        Assert.Contains("tìm theo tập/tên", Item(s, Ep1, "vi").Video.Ref!.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void Candidate_sort_order()
    {
        static Candidate Mk(int tier, CandOffset? off = null, double score = 0.0, bool machine = false, string rel = "") =>
            new("X", tier, "", score, rel, "", null, off, machine);
        var cands = new[]
        {
            Mk(2, rel: "t2"), Mk(1, new CandOffset(5.0, 30, -1), rel: "t1-lech5"), Mk(1, rel: "t1-chuado", score: 0.9),
            Mk(1, new CandOffset(-0.5, 30, -1), rel: "t1-lech0.5"), Mk(1, new CandOffset(0.1, 30, -1), machine: true, rel: "t1-may"), Mk(3, rel: "t3"),
        };
        Assert.Equal(["t1-lech0.5", "t1-lech5", "t1-may", "t1-chuado", "t2", "t3"], SubFetch.SortCandidates(cands).Select(c => c.Release));
    }

    [Theory]
    [InlineData("23.976", 23.976, 1)]
    [InlineData("24", 23.976, 1)]
    [InlineData("25", 23.976, 3)]
    [InlineData("", 23.976, 2)]
    [InlineData(null, 23.976, 2)]
    [InlineData("23,976", 23.976, 1)]
    [InlineData("25", null, 2)]
    public void Fps_tier(string? declared, double? video, int tier) => Assert.Equal(tier, SubFetch.FpsTier(declared, video));

    [Fact]
    public void Candidate_labels()
    {
        Assert.Equal("+2.5s~", new Candidate("X", 1, "", 0, "", "", null, new CandOffset(2.5, 3, 5)).OffsetLabel);
        Assert.Equal("-1.25s", new Candidate("X", 1, "", 0, "", "", null, new CandOffset(-1.25, 30, -1)).OffsetLabel);
        Assert.Equal("+0.0s", new Candidate("X", 1, "", 0, "", "", null, new CandOffset(0.001, 30, -1)).OffsetLabel);
        Assert.Equal("?", new Candidate("X", 1, "", 0, "", "", null).OffsetLabel);
        Assert.Equal("23.976 KHỚP", new Candidate("X", 1, "23.976", 0, "", "", null).FpsLabel);
        Assert.Equal("không khai báo", new Candidate("X", 2, "", 0, "", "", null).FpsLabel);
        Assert.Equal("25 LỆCH", new Candidate("X", 3, "25", 0, "", "", null).FpsLabel);
        Assert.Equal("tên giống 0.67", new ExistingMatch("f", -1, 0.666, false).Reason);
    }

    // ---------------------------------------------------------------- TD-36: phim da co phu de -> khong tim nua

    private static JDict SubTrack(long tid, string codec, string lang, string name = "", bool forced = false, string? ietf = null)
    {
        var props = D(("codec_id", codec), ("language", lang), ("track_name", name), ("forced_track", forced));
        if (ietf is not null)
            props["language_ietf"] = ietf;
        return D(("id", tid), ("type", "subtitles"), ("properties", props));
    }

    private static readonly JDict HasSubsJson = D(("tracks", L(
        D(("id", 0L), ("type", "video"), ("properties", D(("default_duration", 41708333L)))),
        SubTrack(2, "S_TEXT/UTF8", "eng", "English SDH"), // SDH van tinh la da co
        SubTrack(3, "S_TEXT/UTF8", "vie", "Forced", forced: true), // Forced: khong tinh
        SubTrack(4, "S_HDMV/PGS", "cze"), // phu de dang anh: khong tinh
        SubTrack(5, "S_TEXT/ASS", "und"), // khong ghi ngon ngu: khong tinh
        SubTrack(6, "S_TEXT/ASS", "ger", ietf: "de"))));

    [Fact]
    public void Embedded_sub_langs_counts_text_and_sdh_only()
    {
        var got = SubFetch.EmbeddedSubLangs(HasSubsJson);
        Assert.Equal(new HashSet<string> { "en", "de" }, got.Keys.ToHashSet());
        Assert.Equal("track 2 SRT 'English SDH'", got["en"]);
        Assert.Equal("track 6 ASS", got["de"]);
        Assert.Empty(SubFetch.EmbeddedSubLangs(null));
        Assert.Empty(SubFetch.EmbeddedSubLangs(FpsJson));
    }

    [Fact]
    public void Sidecar_sub_langs_reads_suffix_next_to_video()
    {
        var md = _tmp.MovieDir();
        var v = Path.Combine(md, "Phim [YTS.MX] (2020).mkv");
        File.WriteAllBytes(v, []);
        foreach (var n in new[] { "Phim [YTS.MX] (2020).vi.srt", "Phim [YTS.MX] (2020).CS.ass", "Phim [YTS.MX] (2020).en.forced.srt",
                                  "Phim [YTS.MX] (2020).srt", "Phim [YTS.MX] (2020).vi.txt", "Phim khác (2020).en.srt",
                                  "Phim [YTS.MX] (2020) Extra.en.srt" })
            WriteText(Path.Combine(md, n), "x");
        var got = SubFetch.SidecarSubLangs(v);
        Assert.Equal([("vi", "Phim [YTS.MX] (2020).vi.srt"), ("cs", "Phim [YTS.MX] (2020).CS.ass")],
                     got.Select(kv => (kv.Key, Path.GetFileName(kv.Value))).OrderByDescending(x => x.Key));
    }

    [Fact]
    public void Scan_skips_languages_the_video_already_has()
    {
        var lib = NewLib();
        WriteText(Path.Combine(lib.Root, $"{Dune}.vi.srt"), "x"); // ket qua buoc ② nam canh phim
        var (s, _) = Session(lib, new FakeHttp(), Cfg(("Languages", L("vi", "en", "cs"))), [],
                             p => Subprep.Stem(p) == Dune ? HasSubsJson : FpsJson);
        s.Scan();
        var (vi, en, cs) = (Item(s, Dune, "vi"), Item(s, Dune, "en"), Item(s, Dune, "cs"));
        Assert.Equal(Path.Combine(lib.Root, $"{Dune}.vi.srt"), vi.Already);
        Assert.Equal("cạnh phim", vi.AlreadyNote);
        Assert.Equal(Path.Combine(lib.Root, $"{Dune}.mkv"), en.Already);
        Assert.StartsWith("trong MKV: track 2 SRT", en.AlreadyNote, StringComparison.Ordinal);
        Assert.Null(cs.Already);
        Assert.Equal("", cs.AlreadyNote); // chi con thieu tieng Sec -> chi tim tieng Sec
        Assert.Null(Item(s, Ep1, "vi").Already);
        Assert.Null(Item(s, Ep1, "en").Already);
        Assert.Empty(s.Gather(vi));
        Assert.Empty(s.Gather(en)); // khong hoi nguon nao
        Assert.NotNull(cs.Video.Ref); // con thieu ngon ngu -> van can moc
        var rep = s.Execute();
        Assert.Equal(2, rep.Already);
        Assert.False(File.Exists(Path.Combine(lib.Root, "srt_can_sync", "vi", $"{Dune}.vi.srt")));
    }

    [Fact]
    public void Scan_all_languages_present_skips_ref_and_switch_off()
    {
        var lib = NewLib();
        var (s, _) = Session(lib, new FakeHttp(), Cfg(("Languages", L("en", "de"))), [], _ => HasSubsJson);
        s.Scan();
        Assert.All(s.Items, it => Assert.NotNull(it.Already));
        Assert.All(s.Videos, v => Assert.Null(v.Ref)); // du ca roi -> khoi doc/trich phu de goc
        var (s2, _) = Session(lib, new FakeHttp(), Cfg(("Languages", L("en", "de")), ("SkipIfVideoHasSub", false)), [], _ => HasSubsJson);
        s2.Scan();
        Assert.All(s2.Items, it => Assert.Null(it.Already));
        Assert.All(s2.Videos, v => Assert.NotNull(v.Ref));
    }

    // ---------------------------------------------------------------- TD-40: nguon Titulky.com (chi tieng Sec)

    private const string Bb = "Breaking.Bad.S01E05.720p.BRRip.x264.AC3";
    private const string Dune21 = "Dune.2021.1080p.WEBRip.x264";

    private static string TtPreview(IEnumerable<double> times, bool ad = true)
    {
        var cues = (ad ? new[] { (0.042, "www.titulky.com") } : []).Concat(times.Select(t => (t, "x"))).ToList();
        var body = string.Concat(cues.Select((c, i) =>
            $"{i + 1}<br />{Srt.FormatStamp(c.Item1)} --&gt; {Srt.FormatStamp(c.Item1 + 1)}<br />{c.Item2}<br /><br />"));
        return $"<html><body><div id=\"nahled_zdroj\" style=\"x\">\n{body}</div></body></html>";
    }

    private static Dictionary<string, List<double>> TtLib(string root, params string[] stems)
    {
        var refs = new Dictionary<string, List<double>>();
        for (var n = 0; n < stems.Length; n++)
        {
            var stem = stems[n];
            File.WriteAllBytes(Path.Combine(root, stem + ".mkv"), [0x1a, 0x45, 0xdf, 0xa3]);
            refs[stem] = Synth.MakeRefTimes(600, seed: 40 + n);
            WriteText(Path.Combine(root, "srt_goc", stem + ".srt"), Synth.SrtText(refs[stem]));
        }
        return refs;
    }

    private (FetchSession S, Job J) TtSession(string root, FakeHttp http, (string K, object? V)[]? cfg = null, Dictionary<string, string>? secrets = null,
                                              IReadOnlyDictionary<string, bool>? sources = null)
    {
        var kv = new List<(string K, object? V)> { ("Languages", L("cs", "vi")), ("UseOpenSubtitles", false) };
        foreach (var x in cfg ?? [])
        {
            kv.RemoveAll(y => y.K == x.K);
            kv.Add(x);
        }
        var job = new Job();
        var s = new FetchSession(root, Cfg([.. kv]), secrets ?? [], job, http.Client, new Sleeper().Sleep, new ToolPaths(), _ => FpsJson, sources: sources);
        _sessions.Add(s);
        return (s, job);
    }

    private static string TtPage(string name) => TitulkyTests.Page(name);

    [Fact]
    public void Titulky_series_candidates_probe_download_with_captcha()
    {
        var md = _tmp.MovieDir();
        var refs = TtLib(md, Bb);
        var subCs = Encoding.UTF8.GetBytes(Synth.SrtText(Synth.TransformTimes(refs[Bb], 1.0, 2.5, seed: 3))); // ban Sec lech +2,5 s
        var http = new FakeHttp();
        http.Add("GET", @"idown\.php\?id=tok", FakeResponse.Bytes(200, ZipData.Of(("Breaking.Bad.S01E05.cz.srt", subCs))));
        http.Add("GET", @"captcha/captcha\.php", FakeResponse.Bytes(200, [0xFF, 0xD8, (byte)'J', (byte)'P', (byte)'E', (byte)'G']));
        http.Add("POST", @"titulky\.com/idown\.php$", FakeResponse.Text(200, "<html><body><a id=\"downlink\" href=\"idown.php?id=tok\">x</a><script>CountDown(3)</script></body></html>"));
        http.Add("GET", @"titulky\.com/idown\.php$", FakeResponse.Text(200, TtPage("idown_captcha.html")));
        // ban xem truoc: ban 232758 khop phim (lech +2,5 s), cac ban khac la phim khac
        http.Add("GET", @"titulky\.com/nahled\.php", call => FakeResponse.Text(200, TtPreview(
            call.Params["Titulky"] == "232758" ? refs[Bb].Take(12).Select(t => t + 2.5) : Synth.MakeRefTimes(12, seed: 99))));
        http.Add("GET", @"titulky\.com/Breaking-Bad-S01E05-\d+\.htm", FakeResponse.Text(200, TtPage("detail_breaking_bad_s01e05.html")));
        http.Add("GET", @"titulky\.com/$", _ => FakeResponse.Text(200, TtPage("search_breaking_bad_s01e05.html")));
        var (s, job) = TtSession(md, http);
        Assert.NotNull(s.Tt);
        Assert.True(s.Sources["titulky"]);
        Assert.False(s.Tt.HasAccount);
        s.Scan();
        Assert.True(Has(job, "Titulky: dùng kiểu khách"));
        var (cs, vi) = (Item(s, Bb, "cs"), Item(s, Bb, "vi"));
        var cands = s.Gather(cs);
        Assert.Equal([new("Fulltext", "Breaking Bad S01E05")], http.CallsTo(@"titulky\.com/$")[0].Params); // tim dung kieu trang ghi tap
        Assert.Equal(4, cands.Count);
        Assert.All(cands, c => Assert.Equal("Titulky", c.Src));
        var best = cands[0]; // FPS khop + da uoc tinh do lech -> dung dau
        Assert.Equal(232758, ((SearchRow)best.Payload!).Id);
        Assert.Equal("Breaking.Bad.S01E05.Gray.Matter.720p.BRRip.x264.AC3", best.Release);
        Assert.Equal((1, "23.976", "23.976 KHỚP"), (best.Tier, best.Fps, best.FpsLabel));
        Assert.NotNull(best.Off);
        Assert.True(Math.Abs(best.Off.Offset + 2.5) <= 0.25); // da bo qua dong quang cao dau file
        Assert.EndsWith("~", best.OffsetLabel, StringComparison.Ordinal);
        Assert.Equal(3, cands.Count(c => c.Tier == 1)); // chi 3 ban dau duoc xem ky
        Assert.Equal(1, cands.Count(c => c.Off is not null));
        Assert.Equal(3, http.CallsTo(@"-\d+\.htm$").Count);
        Assert.Equal(3, http.CallsTo(@"nahled\.php").Count);
        // tieng Viet: KHONG hoi Titulky
        var n = http.Calls.Count;
        Assert.Empty(s.Gather(vi));
        Assert.Equal(n, http.Calls.Count);
        // tai: gap CAPTCHA -> hoi ban; cho du thoi gian; luu NGUYEN byte vao srt_can_sync\cs\
        var asked = new List<(byte[], string)>();
        s.Captcha = (img, note) =>
        {
            asked.Add((img, note));
            return "k9x2";
        };
        s.Choose(cs, best);
        var rep = s.Execute();
        var dest = Path.Combine(md, "srt_can_sync", "cs", $"{Bb}.cs.srt");
        Assert.Equal(1, rep.Ok);
        Assert.Equal(subCs, File.ReadAllBytes(dest));
        Assert.Equal(dest, cs.Result);
        Assert.Equal("Titulky", cs.ResultSrc);
        var a = Assert.Single(asked);
        Assert.Equal([0xFF, 0xD8, (byte)'J', (byte)'P', (byte)'E', (byte)'G'], a.Item1);
        Assert.Equal("Breaking.Bad.S01E05.Gray.Matter.720p.BRRip.x264.AC3", a.Item2);
        Assert.Equal("k9x2", http.CallsTo(@"idown\.php$", "POST")[0].Data!["downkod"]);
        Assert.Contains("Khớp thời gian", cs.TimingNote, StringComparison.Ordinal);
        Assert.True(Has(job, "Titulky yêu cầu chờ"));
        var runs = Undo.ListRuns(md, "_subfetch_logs", "subfetch");
        Assert.NotEmpty(runs);
        Assert.Equal(1, runs[0].Counts.GetValueOrDefault("Create"));
    }

    [Fact]
    public void Titulky_movie_filters_language_year_title_and_source_order()
    {
        var md = _tmp.MovieDir();
        TtLib(md, Dune21);
        var http = new FakeHttp();
        http.Add("GET", @"titulky\.com/Dune-\d+\.htm|titulky\.com/Duna-\d+\.htm", FakeResponse.Text(200, "<html>FPS:</span> 25 </html>"));
        http.Add("GET", @"titulky\.com/$", FakeResponse.Text(200, TtPage("search_dune.html")));
        // OpenSubtitles gia co 1 ban tieng Sec -> phai dung SAU Titulky
        http.Add("POST", @"opensubtitles\.com/api/v1/login", Ok(D(("token", "t"), ("base_url", "api.opensubtitles.com"), ("user", D(("allowed_downloads", 20L), ("vip", false))))));
        http.Add("GET", @"opensubtitles\.com/api/v1/infos/user", Ok(D(("data", D(("remaining_downloads", 20L), ("allowed_downloads", 20L))))));
        http.Add("GET", @"opensubtitles\.com/api/v1/subtitles", Ok(D(("data", L(
            D(("id", "o1"), ("attributes", D(("release", "Dune.2021.1080p.WEBRip"), ("fps", 23.976), ("download_count", 3L), ("files", L(D(("file_id", 9L))))))))))));
        var (s, job) = TtSession(md, http, [("Languages", L("cs")), ("UseOpenSubtitles", true), ("TitulkyProbeTop", 1L)],
                                 new Dictionary<string, string> { ["OsApiKey"] = "demo-os", ["OsUsername"] = "u", ["OsPassword"] = "p" });
        s.Scan();
        var it = Item(s, Dune21, "cs");
        var outp = s.OnlineCandidates(it);
        Assert.Equal("OpenSub", outp[^1].Src); // thu tu nguon
        Assert.All(outp.Take(outp.Count - 1), c => Assert.Equal("Titulky", c.Src));
        var tt = outp.Where(c => c.Src == "Titulky").ToList();
        var rows = tt.Select(c => (SearchRow)c.Payload!).ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.True(r.Lang == "CZ" && r.Year == 2021 && r.Title == "Dune")); // bo SK, bo 1984 / 2000, bo "Duna"
        Assert.Equal(tt.Count, tt.Select(c => c.Ident).Distinct().Count()); // hai ban trung ten release van phan biet duoc (them #id)
        Assert.Contains(tt, c => Regex.IsMatch(c.Release, @" #\d+$"));
        Assert.Single(http.CallsTo(@"-\d+\.htm$")); // TitulkyProbeTop = 1
        var probed = tt.First(c => c.Fps.Length > 0);
        Assert.Equal(("25", 3), (probed.Fps, probed.Tier)); // khai bao 25 fps, video 23,976 -> LECH
        var cands = s.Gather(it);
        Assert.All(cands, c => Assert.NotEqual(3, c.Tier)); // StrictFpsMatch loai ban lech framerate
        Assert.True(Has(job, "Titulky: tìm 'Dune' → 13 kết quả, giữ"));
    }

    [Fact]
    public void Titulky_only_for_czech_and_can_be_switched_off()
    {
        var md = _tmp.MovieDir();
        TtLib(md, Dune21);
        var (s, _) = TtSession(md, new FakeHttp(), [("Languages", L("vi", "en"))]);
        Assert.Null(s.Tt); // khong co 'cs' trong ngon ngu can tai -> khong tao nguon
        var (s2, _) = TtSession(md, new FakeHttp(), [("UseTitulky", false)]);
        Assert.Null(s2.Tt);
        Assert.False(s2.Sources["titulky"]);
        var (s3, _) = TtSession(md, new FakeHttp(), sources: new Dictionary<string, bool> { ["titulky"] = false });
        Assert.Null(s3.Tt);
        // co tai khoan: dang nhap luc ket noi, mat khau khong lot vao nhat ky
        var http = new FakeHttp();
        http.Add("POST", @"titulky\.com/$", FakeResponse.Text(200, "<html><a>Odhlásit</a></html>"));
        http.Add("GET", @"titulky\.com/$", FakeResponse.Text(200, "<html><table class='soupis'></table></html>"));
        var (s4, job) = TtSession(md, http, secrets: new Dictionary<string, string> { ["TitulkyUsername"] = "anh", ["TitulkyPassword"] = "demo-pass" });
        s4.Scan();
        Assert.True(s4.Tt!.LoggedIn);
        Assert.True(Has(job, "Titulky: đã đăng nhập"));
        Assert.Equal("anh", http.CallsTo(@"titulky\.com/$", "POST")[0].Data!["Login"]);
        Assert.False(Has(job, "demo-pass"));
        var j2 = new Job();
        s4.SetJob(j2);
        Assert.Same(j2, s4.Tt.Job);
    }

    private const string Lotr = "The Lord of the Rings The Return of the King (2003) Extended 1080p BluRay HEVC x265 5.1 BONE";

    private static void LotrSite(FakeHttp http, List<string> queries)
    {
        const string realTitle = "The Lord of the Rings: The Return of the King";
        http.Add("GET", @"titulky\.com/$", call =>
        {
            var q = call.Params["Fulltext"];
            queries.Add(q);
            var hit = realTitle.Contains(q, StringComparison.OrdinalIgnoreCase);
            return FakeResponse.Text(200, hit ? TtPage("search_the_return_of_the_king.html") : "<html><table class='soupis'></table></html>");
        });
    }

    [Fact]
    public void Titulky_title_without_colon_falls_back_to_title_fragments()
    {
        var md = _tmp.MovieDir();
        TtLib(md, Lotr);
        var http = new FakeHttp();
        var queries = new List<string>();
        LotrSite(http, queries);
        var (s, job) = TtSession(md, http, [("Languages", L("cs")), ("TitulkyProbeTop", 0L)]);
        s.Scan();
        var it = Item(s, Lotr, "cs");
        var cands = s.Gather(it);
        Assert.Equal(["The Lord of the Rings The Return of the King", "The Return of the King"], queries);
        Assert.Equal(["Lord of the Rings: The Return of the King", "The Lord of the Rings - The Return of the King HDRE",
                      "The Lord of the Rings: The Return of the King", "The Lord of the Rings: The Return of the King [EXTENDED]"],
                     cands.Select(c => ((SearchRow)c.Payload!).Title).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(6, cands.Count); // bo: tap phim bo "King of Boys", ban nam 1980
        Assert.All(cands, c => Assert.Equal("Titulky", c.Src));
        Assert.NotEqual("không nguồn nào có", it.Status);
        var log = string.Join("\n", job.Lines.Select(l => l.Message));
        Assert.Contains("Titulky: tìm 'The Lord of the Rings The Return of the King' → 0 kết quả", log, StringComparison.Ordinal);
        Assert.Contains("Titulky: thử một phần tên 'The Return of the King' → 8 kết quả, giữ 6.", log, StringComparison.Ordinal);
    }

    [Fact]
    public void Titulky_asks_with_official_tmdb_title_first()
    {
        var md = _tmp.MovieDir();
        TtLib(md, Lotr);
        var http = new FakeHttp();
        var queries = new List<string>();
        LotrSite(http, queries);
        http.Add("GET", @"themoviedb\.org/3/search/movie", Ok(D(("results", L(
            D(("id", 122L), ("title", "The Lord of the Rings: The Return of the King"),
              ("original_title", "The Lord of the Rings: The Return of the King"), ("release_date", "2003-12-17")))))));
        http.Add("GET", @"themoviedb\.org/3/movie/122/external_ids", Ok(D(("imdb_id", "tt0167260"))));
        var (s, _) = TtSession(md, http, [("Languages", L("cs")), ("TitulkyProbeTop", 0L)],
                               new Dictionary<string, string> { ["TmdbApiKey"] = "0123456789abcdef0123456789abcdef" });
        s.Scan();
        var it = Item(s, Lotr, "cs");
        var cands = s.Gather(it);
        Assert.Equal("The Lord of the Rings: The Return of the King", it.Video.Official);
        Assert.Equal("tt0167260", it.Video.Imdb);
        Assert.Equal(["The Lord of the Rings: The Return of the King"], queries);
        Assert.Equal(6, cands.Count);
    }

    [Fact]
    public void Titulky_stops_after_a_few_queries_when_nothing_matches()
    {
        const string stem = "Mot Bo Phim Viet Nam Khong Co Tren Trang (2019) 1080p";
        var md = _tmp.MovieDir();
        TtLib(md, stem, Dune21);
        var http = new FakeHttp();
        var queries = new List<string>();
        http.Add("GET", @"titulky\.com/$", call =>
        {
            queries.Add(call.Params["Fulltext"]);
            if (call.Params["Fulltext"] == "Dune") // co ket qua, nhung chi tieng Slovak / phim nam khac
                return FakeResponse.Text(200, TtPage("search_dune.html").Replace("alt=\"CZ\"", "alt=\"SK\"", StringComparison.Ordinal));
            return FakeResponse.Text(200, "<html><table class='soupis'></table></html>");
        });
        var (s, _) = TtSession(md, http, [("Languages", L("cs")), ("TitulkyProbeTop", 0L)]);
        s.Scan();
        Assert.Empty(s.Gather(Item(s, stem, "cs")));
        Assert.Equal("không nguồn nào có", Item(s, stem, "cs").Status);
        Assert.Equal("Mot Bo Phim Viet Nam Khong Co Tren Trang", queries[0]);
        Assert.Equal(5, queries.Count);
        queries.Clear();
        Assert.Empty(s.Gather(Item(s, Dune21, "cs")));
        Assert.Equal(["Dune"], queries);
    }
}
