using MuxStudio.Core.Tests.Support;
using static MuxStudio.Core.Tests.Support.MkvFake;
using static MuxStudio.Core.Tests.Support.PyData;
using JDict = System.Collections.Generic.OrderedDictionary<string, object?>;

namespace MuxStudio.Core.Tests;

/// <summary>Bước ③ (MuxTool): quy tắc audio (§6.7), phụ đề (§6.8), dựng lệnh mkvmerge (§6.2, SPEC 4.4), thẻ codec, thực thi + mã thoát +
/// hoàn tác. JSON <c>mkvmerge -J</c> giả lập. Port <c>tests/test_mux.py</c> (phần audio/phụ đề/lệnh/thực thi; phần probe đã ở
/// <c>ProbeTests</c>) + bài cuối của <c>tests/test_naming_db.py</c>.</summary>
[Collection(GlobalStateCollection.Name)] // bang ngon ngu (LangName)
public sealed class MuxTests : IDisposable
{
    private const string Movie = "Phim.Thu.2020.1080p.WEB-DL";
    private readonly TempDir _tmp = new();
    private readonly List<MuxLib> _libs = [];
    private string? _movieDir;

    private string MovieDir => _movieDir ??= _tmp.MovieDir();

    public void Dispose()
    {
        foreach (var l in _libs)
            l.Dispose();
        _tmp.Dispose();
    }

    private MuxLib Lib(params (string K, object? V)[] cfg) => Lib(cfg, null);

    private MuxLib Lib((string K, object? V)[] cfg, Dictionary<string, string>? secrets, FakeHttp? http = null, MuxLib? share = null)
    {
        var l = new MuxLib(MovieDir, cfg, secrets, http, shareJson: share);
        _libs.Add(l);
        return l;
    }

    private static List<int> Kept(MuxPlan p) => MuxLib.AudioKept(p);

    private string Sib(string p, string name) => Path.Combine(Path.GetDirectoryName(p)!, name);

    // ==========================================================================
    //  AUDIO (§6.7)
    // ==========================================================================

    [Fact]
    public void Commentary_and_description_removed()
    {
        var lib = Lib();
        var p = lib.Add(Movie, J(V(), A(1), A(2, codec: "AC-3", codecId: "A_AC3", ch: 2, name: "Director's Commentary"),
                                 A(3, codec: "AAC", codecId: "A_AAC", ch: 2, name: "Audio Description"), A(4, "cze")));
        var plan = lib.Plan(p);
        Assert.Equal([1, 4], Kept(plan));
        Assert.Equal([(2, "Commentary"), (3, "Audio Description")], plan.Audio.Where(a => !a.Keep).Select(a => (a.Id, a.Reason)));
        Assert.Equal("English E-AC-3 5.1", plan.Audio[0].Label);
    }

    [Fact]
    public void All_commentary_keeps_everything()
    {
        var lib = Lib();
        var p = lib.Add(Movie, J(V(), A(1, name: "Commentary 1"), A(2, name: "Commentary 2", lang: "cze")));
        Assert.Equal([1, 2], Kept(lib.Plan(p)));
    }

    [Fact]
    public void Und_single_becomes_en_and_two_und_skips()
    {
        var lib = Lib();
        var p = lib.Add(Movie, J(V(), A(1, lang: "und")));
        var plan = lib.Plan(p);
        Assert.Equal([(1, "en")], plan.LangOverride.Select(kv => (kv.Key, kv.Value)));
        Assert.Equal("ok", plan.Status);
        var args = lib.S.BuildArgs(plan);
        var i = args.IndexOf("--language");
        Assert.Equal(["--language", "1:en"], args[i..(i + 2)]);
        var p2 = lib.Add("Phim.Khac.2021", J(V(), A(1, lang: "und"), A(2, lang: "eng")));
        var plan2 = lib.Plan(p2);
        Assert.Equal("skip", plan2.Status);
        Assert.Contains("und", plan2.Reasons[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Two_english_audio_needs_choice_q3()
    {
        var lib = Lib();
        var p = lib.Add(Movie, J(V(), A(1, codec: "AC-3", codecId: "A_AC3", bps: 448000), A(2, codec: "TrueHD", codecId: "A_TRUEHD", ch: 8, bps: 3500000)));
        var plan = lib.Plan(p);
        Assert.Equal("need_choice", plan.Status);
        Assert.Contains("2 audio tiếng Anh", plan.Reasons[0], StringComparison.Ordinal);
        lib.S.KeepAllAudio(plan);
        plan = lib.Plan(p);
        Assert.Equal(("ok", 2), (plan.Status, plan.DefaultAudio));
        Assert.Equal([1, 2], Kept(plan));
        lib.S.AudioInputs.Clear();
        lib.S.SetAudioChoice(plan, [2], applyToRest: false);
        plan = lib.Plan(p);
        Assert.Equal(("ok", 1), (plan.Status, plan.DefaultAudio));
        Assert.Equal([1], Kept(plan));
        var lib2 = Lib([("SkipOnMultipleEnAudio", false)], null, share: lib);
        plan = lib2.Plan(p);
        Assert.Equal(("ok", 2), (plan.Status, plan.DefaultAudio)); // tu lay bitrate cao nhat
    }

    [Fact]
    public void Default_audio_english_else_first()
    {
        var lib = Lib();
        Assert.Equal(2, lib.Plan(lib.Add(Movie, J(V(), A(1, "jpn"), A(2, "eng", ch: 2), A(3, "cze")))).DefaultAudio);
        Assert.Equal(1, lib.Plan(lib.Add("Anime.2020", J(V(), A(1, "jpn"), A(2, "cze")))).DefaultAudio);
    }

    [Fact]
    public void Audio_rule_remembers_what_is_kept()
    {
        var lib = Lib();
        var plan1 = lib.Plan(lib.Add("Show.S01E01", J(V(), A(1, ch: 6), A(2, ch: 2), A(3, "cze"))));
        var rule = lib.S.SetAudioChoice(plan1, [2, 3], applyToRest: true)!;
        Assert.Equal(["en"], rule.KeepLangs);
        Assert.Equal(["en"], rule.KeepSig!.Keys);
        Assert.Equal(["5.1"], rule.KeepSig["en"]);
        Assert.Equal("chỉ giữ English (5.1)", rule.Describe());
        var plan2 = lib.Plan(lib.Add("Show.S01E02", J(V(), A(1, ch: 6), A(2, ch: 2), A(3, "cze"), A(4, "ger"))));
        Assert.Equal([1], Kept(plan2));
        Assert.Equal("chỉ giữ English (5.1)", plan2.RuleApplied);
        // quy tac lam mat het audio -> bo qua quy tac cho file do
        Assert.Equal([1, 2], Kept(lib.Plan(lib.Add("Show.S01E03", J(V(), A(1, "jpn"), A(2, "cze"))))));
    }

    [Fact]
    public void Remember_audio_never()
    {
        var lib = Lib(("RememberAudioChoice", "never"));
        var p = lib.Add("Show.S01E01", J(V(), A(1), A(2, "cze")));
        Assert.Null(lib.S.SetAudioChoice(lib.Plan(p), [2], applyToRest: true));
        Assert.Null(lib.S.AudioRule);
    }

    [Fact]
    public void Needs_audio_table_flag()
    {
        var lib = Lib();
        Assert.True(lib.Plan(lib.Add(Movie, J(V(), A(1), A(2, "cze")))).NeedsAudioTable);
        Assert.False(lib.Plan(lib.Add("Phim.Mot.Audio.2020", J(V(), A(1)))).NeedsAudioTable);
    }

    // ==========================================================================
    //  PHỤ ĐỀ (§6.8)
    // ==========================================================================

    [Fact]
    public void Internal_sub_rules()
    {
        var lib = Lib();
        var p = lib.Add(Movie, J(V(), A(1), S(2, "eng"), S(3, "eng", name: "English SDH"), S(4, "eng", name: "Forced", forced: true),
                                 S(5, "cze", codecId: "S_HDMV/PGS"), S(6, "vie", name: "Commentary"), S(7, "fre"), S(8, "cze")));
        var plan = lib.Plan(p);
        Assert.Equal([(2, true, "giữ"), (3, false, "SDH (đã có English thường)"), (4, false, "Forced"), (5, false, "phụ đề dạng ảnh"),
                      (6, false, "Commentary"), (7, false, "ngoài danh sách ngôn ngữ"), (8, true, "giữ")],
                     plan.IntSubs.Select(s => (s.Id, s.Keep, s.Reason)));
        var s2 = lib.Plan(lib.Add("Phim.SDH.2020", J(V(), A(1), S(2, "eng", name: "SDH")))).IntSubs[0];
        Assert.True(s2.Keep);
        Assert.Equal("English SDH", s2.Label);
        Assert.StartsWith("vớt vát", s2.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void External_subs_priority_and_override()
    {
        var lib = Lib();
        var p = lib.Add(Movie, J(V(), A(1), S(2, "vie"), S(3, "eng")));
        MuxLib.WriteSub(Sib(p, $"{Movie}.vi.ass"));
        MuxLib.WriteSub(Sib(p, $"{Movie}.vi.srt")); // .srt dung truoc .ass trong SubExtPriority
        MuxLib.WriteSub(Sib(p, $"{Movie}.forced.srt"));
        MuxLib.WriteSub(Sib(p, $"{Movie}.ja.srt")); // ja khong nam trong SubKeepLangs
        var plan = lib.Plan(p);
        Assert.Equal([($"{Movie}.vi.srt", "vi")], plan.ExtSubs.Select(e => (Path.GetFileName(e.Path), e.Code)));
        Assert.Equal([(2, false), (3, true)], plan.IntSubs.Select(s => (s.Id, s.Keep))); // sub ngoai vi thay sub trong vi
        Assert.Equal(("ext", 0), plan.DefaultSub);
    }

    [Fact]
    public void External_sub_no_suffix_and_any_language_td25()
    {
        var lib = Lib(("SubKeepLangs", L("vi", "en", "ja")), ("SubDefaultPriority", L("vi", "en")));
        var p = lib.Add(Movie, J(V(), A(1)));
        MuxLib.WriteSub(Sib(p, $"{Movie}.srt")); // khong hau to = DefaultExternalLang (vi)
        MuxLib.WriteSub(Sib(p, $"{Movie}_japanese.srt"));
        MuxLib.WriteSub(Sib(p, $"{Movie}.English.srt"));
        Assert.Equal(["vi", "en", "ja"], lib.Plan(p).ExtSubs.Select(e => e.Code));
    }

    [Fact]
    public void Default_sub_priority_prefers_non_sdh()
    {
        var lib = Lib();
        var p = lib.Add(Movie, J(V(), A(1), S(2, "eng", name: "SDH"), S(3, "cze"), S(4, "eng")));
        Assert.Equal(("int", 4), lib.Plan(p).DefaultSub); // vi khong co -> en, ban khong SDH
    }

    [Fact]
    public void Sub_auto_plan_items_and_picker_choice()
    {
        var lib = Lib();
        var p = lib.Add(Movie, J(V(), A(1), S(2, "eng"), S(3, "eng"), S(4, "cze", codecId: "S_HDMV/PGS")));
        MuxLib.WriteSub(Sib(p, $"{Movie}.vi.srt"));
        var plan = lib.Plan(p);
        Assert.Equal([("E|vi", true), ("I|English", true), ("I|English #2", true), ("I|Czech [ảnh]", false)],
                     plan.SubItems.Select(i => (i.Key, i.AutoKeep)));
        Assert.Equal("NGOÀI\nVietnamese (SRT)", plan.SubItems[0].Header);
        lib.S.SetSubChoice(plan, [], [3, 4]);
        plan = lib.Plan(p);
        Assert.Empty(plan.ExtSubs);
        Assert.Equal([3, 4], plan.KeepSubs.Select(s => s.Id));
        lib.S.ResetSubChoices();
        Assert.NotEmpty(lib.Plan(p).ExtSubs);
    }

    [Fact]
    public void Sub_charset_for_legacy_external()
    {
        var lib = Lib();
        var p = lib.Add(Movie, J(V(), A(1)));
        MuxLib.WriteSub(Sib(p, $"{Movie}.vi.srt"), 1258, "Đây là tiên");
        MuxLib.WriteSub(Sib(p, $"{Movie}.cs.srt"), 1250, "Příliš");
        var args = lib.S.BuildArgs(lib.Plan(p));
        Assert.Equal(["0:windows-1258", "0:windows-1250"], args.Select((a, i) => (a, i)).Where(x => x.a == "--sub-charset").Select(x => args[x.i + 1]));
    }

    // ==========================================================================
    //  LỆNH MKVMERGE (§6.2, SPEC 4.4)
    // ==========================================================================

    private (MuxPlan Plan, string P) FullPlan(MuxLib lib, int ver = 82)
    {
        lib.S.Ver = new ToolVersion(true, ver, "");
        var p = lib.Add(Movie, J(V(), A(1, "eng", bps: 640000), A(2, "eng", name: "Commentary"), A(3, "cze", ch: 2),
                                 S(4, "eng"), S(5, "eng", name: "SDH"), S(6, "cze"), S(7, "fre")));
        MuxLib.WriteSub(Sib(p, $"{Movie}.vi.srt"));
        return (lib.Plan(p), p);
    }

    [Fact]
    public void Mkvmerge_args_v82_exact()
    {
        var lib = Lib();
        var (plan, p) = FullPlan(lib);
        var outp = Path.Combine(lib.Root, "Output", $"{plan.OutBase}.mkv");
        var vi = Sib(p, $"{Movie}.vi.srt");
        Assert.Equal($"{Movie} - 1080p HEVC EAC35.1", plan.OutBase);
        Assert.Equal([
            "-o", outp, "--track-order", "0:0,0:1,0:3,1:0,0:4,0:6",
            "--title", plan.OutBase,
            "--audio-tracks", "1,3", "--subtitle-tracks", "4,6",
            "--track-name", "1:English E-AC-3 5.1", "--track-name", "3:Czech E-AC-3 2.0",
            "--track-name", "4:English", "--track-name", "6:Czech",
            "--default-track-flag", "1:1", "--default-track-flag", "3:0",
            "--default-track-flag", "4:0", "--forced-display-flag", "4:0",
            "--default-track-flag", "6:0", "--forced-display-flag", "6:0",
            p,
            "--language", "0:vi", "--track-name", "0:Vietnamese", "--default-track-flag", "0:1", "--forced-display-flag", "0:0", vi,
        ], lib.S.BuildArgs(plan));
    }

    [Fact]
    public void Mkvmerge_flag_names_v73_and_iso_codes_v49()
    {
        var lib = Lib();
        var (plan, _) = FullPlan(lib, 73);
        var a = lib.S.BuildArgs(plan);
        Assert.Contains("--default-track", a);
        Assert.Contains("--forced-track", a);
        Assert.DoesNotContain("--default-track-flag", a);
        Assert.Contains("0:vi", a);
        (plan, _) = FullPlan(lib, 49);
        a = lib.S.BuildArgs(plan);
        Assert.Contains("0:vie", a);
        Assert.Contains("--default-track", a);
    }

    [Fact]
    public void No_subtitles_when_none_kept_and_no_audio_list_when_all_kept()
    {
        var lib = Lib(("SubKeepLangs", L("vi")), ("UseTrackOrder", false), ("SetTitleFromName", false));
        var a = lib.S.BuildArgs(lib.Plan(lib.Add(Movie, J(V(), A(1), A(2, "cze"), S(3, "eng")))));
        Assert.Contains("--no-subtitles", a);
        Assert.DoesNotContain("--audio-tracks", a);
        Assert.DoesNotContain("--track-order", a);
        Assert.DoesNotContain("--title", a);
    }

    [Fact]
    public void Codec_tag_only_in_file_name_and_off_for_tv()
    {
        var lib = Lib();
        var p = lib.Add("Show.S01E01.1080p", J(V(), A(1)));
        var plan = lib.Plan(p);
        Assert.Equal("", plan.CodecTag);
        Assert.Contains("phim bộ → không gắn thẻ codec", plan.Notes);
        var lib2 = Lib([("CodecTagForTv", true), ("CodecTagParts", L("res", "acodec"))], null, share: lib);
        Assert.Equal("1080p EAC35.1", lib2.Plan(p).CodecTag);
        var atmos = J(V(dim: "3840x2160"), A(1, codec: "TrueHD Atmos", codecId: "A_TRUEHD", ch: 8));
        Assert.Equal("2160p DV HDR HEVC TrueHD7.1 Atmos", Mux.CodecTag(atmos, D(("color_transfer", "smpte2084")), "Movie.2160p.DV",
                     (JDict)((List<object?>)atmos["tracks"]!)[1]!, ["res", "hdr", "vcodec", "acodec"]));
    }

    // ==========================================================================
    //  THỰC THI: mã thoát, file dở dang, hoàn tác (TĐ-17), "Xem lệnh" không ghi gì (TĐ-21)
    // ==========================================================================

    [Fact]
    public void Execute_codes_temp_name_and_undo()
    {
        var lib = Lib();
        var a = lib.Add("Phim.A.2020", J(V(), A(1)));
        var b = lib.Add("Phim.B.2020", J(V(), A(1)));
        var c = lib.Add("Phim.C.2020", J(V(), A(1)));
        var made = new List<string>();
        lib.S.Runner = MuxLib.FakeRun([0, 1, 2], made);
        var plans = lib.S.PlanAll("none", [a, b, c]);
        var outDir = Path.Combine(lib.Root, "Output");
        Assert.False(Directory.Exists(outDir)); // lap ke hoach + Xem lenh khong tao thu muc (TD-21)
        Assert.NotEmpty(lib.S.PreviewCommands(plans[0]));
        Assert.False(Directory.Exists(outDir));
        var rep = lib.S.Execute(plans);
        Assert.Equal((2, 1, 1), (rep.Ok, rep.Warning, rep.Failed));
        Assert.All(made, m => Assert.EndsWith(".muxstudio-tmp.mkv", m, StringComparison.Ordinal)); // ghi ten tam roi moi doi ten
        Assert.Equal(["Phim.A.2020 - 1080p HEVC EAC35.1.mkv", "Phim.B.2020 - 1080p HEVC EAC35.1.mkv"],
                     Directory.GetFileSystemEntries(outDir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        var rows = Undo.ReadRun(rep.LogPath!);
        Assert.Equal([("Mkdir", ""), ("Create", "OK"), ("Create", "OK-Warning"), ("Fail", "FAIL")], rows.Select(r => (r["Action"], r["Status"])));
        var trashed = new List<string>();
        Undo.ExecuteUndo(Undo.PlanUndo(rep.LogPath!), trashFn: (path, _) =>
        {
            trashed.Add(path);
            File.Delete(path);
            return true;
        });
        Assert.Equal(2, trashed.Count);
        Assert.False(Directory.Exists(outDir)); // file mux vao Thung rac, thu muc Output moi tao bi xoa
    }

    [Fact]
    public void Execute_skips_pending_and_existing()
    {
        var lib = Lib();
        var p = lib.Add(Movie, J(V(), A(1), A(2)));
        var made = new List<string>();
        lib.S.Runner = MuxLib.FakeRun([0], made);
        var plans = lib.S.PlanAll("none", [p]);
        Assert.Equal("need_choice", plans[0].Status);
        var rep = lib.S.Execute(plans);
        Assert.Equal((1, 0), (rep.Pending, rep.Ok));
        Assert.Empty(made);
    }

    [Fact]
    public void Skip_if_output_exists_incl_tagged_name()
    {
        var lib = Lib(("SkipIfOutputExists", true));
        var p = lib.Add(Movie, J(V(), A(1)));
        Directory.CreateDirectory(Path.Combine(MovieDir, "Output"));
        File.WriteAllBytes(Path.Combine(MovieDir, "Output", $"{Movie} - 2160p HEVC.mkv"), []);
        var plan = lib.Plan(p);
        Assert.Equal("exists", plan.Status);
        Assert.Contains("2160p", plan.Reasons[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Existing_output_goes_to_trash_before_replace()
    {
        var lib = Lib();
        var p = lib.Add("Phim.A.2020", J(V(), A(1)));
        var outp = Path.Combine(lib.Root, "Output", "Phim.A.2020 - 1080p HEVC EAC35.1.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(outp)!);
        File.WriteAllBytes(outp, "cu"u8.ToArray());
        var trashed = new List<string>();
        lib.S.Trash = (path, _) =>
        {
            trashed.Add(path);
            File.Delete(path);
            return true;
        };
        lib.S.Runner = MuxLib.FakeRun([0], []);
        var rep = lib.S.Execute(lib.S.PlanAll("none", [p]));
        Assert.Equal(1, rep.Ok);
        Assert.Equal([outp], trashed);
        Assert.Equal(Enumerable.Repeat((byte)'x', 100), File.ReadAllBytes(outp));
    }

    [Fact]
    public void Plan_all_requires_database_choice()
    {
        var lib = Lib();
        Assert.Throws<ArgumentException>(() => lib.S.PlanAll(null));
        Assert.Equal("TMDB: chưa điền key", lib.S.DbStatus()["tmdb"].Text);
        Assert.Null(lib.S.DbStatus()["tvdb"].Ok);
    }

    [Fact]
    public void Scan_skips_output_and_donor_folder()
    {
        var lib = Lib();
        lib.Add(Movie, J(V(), A(1)));
        lib.Add("x", J(V()), rel: @"Output\Phim (2020) [tmdbid-1]");
        lib.Add("y", J(V()), rel: "audio_nguon");
        lib.Add("Show.S01E01", J(V()), rel: "Season 1");
        Assert.Equal([$"{Movie}.mkv", "Show.S01E01.mkv"], lib.S.Scan().Select(Path.GetFileName));
    }

    [Fact]
    public void Keep_original_names_for_pending()
    {
        var h = new FakeHttp().Add("GET", "/search/movie$", FakeResponse.Json(200, D(("results", L(D(("id", 1L), ("title", "Khac Han"), ("release_date", "2001-01-01")))))));
        var lib = Lib([], new Dictionary<string, string> { ["TmdbApiKey"] = TmdbTests.Key }, h);
        var p = lib.Add("Phim.Thu.2020.1080p", J(V(), A(1)));
        var plans = lib.S.PlanAll("tmdb", [p]);
        Assert.Equal("need_choice", plans[0].Status);
        Assert.Equal("Khac Han", plans[0].Name.Candidates[0].Title);
        var np = Assert.Single(lib.S.KeepOriginalNames(plans));
        Assert.Equal("ok", np.Status);
        Assert.StartsWith("Phim.Thu.2020.1080p - ", np.OutBase, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- mux xong: de xuat dua file nguon vao Thung rac
    private static MuxPlan Done(MuxPlan plan, string? outp)
    {
        if (outp is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outp)!);
            File.WriteAllBytes(outp, [0x1a, 0x45, 0xdf, 0xa3, .. " ket qua"u8]);
        }
        plan.Result = new MuxResult { Code = 0, Ok = outp is not null, Out = outp };
        return plan;
    }

    [Fact]
    public void Used_sources_only_successful_and_not_still_needed()
    {
        string Mk(string name)
        {
            var p = Path.Combine(MovieDir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, "x"u8.ToArray());
            return p;
        }
        var outDir = Path.Combine(MovieDir, "Output");
        var (v1, v2, v3) = (Mk("A.S01E01.mkv"), Mk("A.S01E02.mkv"), Mk("A.S01E03.mkv"));
        var (s1, s2) = (Mk("A.S01E01.vi.srt"), Mk("A.S01E02.vi.srt"));
        var shared = Mk(@"audio_nguon\A.CZ.mka"); // nguon audio ghep dung chung cho tap 1 va tap 3
        var inside = Mk(@"Output\cu.vi.srt");
        MuxPlan Plan(string src, string[] subs, params string[] donors)
        {
            var p = new MuxPlan(src, Path.GetFileName(src), new OutputName(Subprep.Stem(src)))
            {
                ExtSubs = [.. subs.Select(x => new ExtSub(x, "vi", 0))],
            };
            p.Donor.Tracks = [.. donors.Select(d => new DonorTrack(d, 1, 0, "cs", "Czech", 0))];
            return p;
        }
        var p1 = Done(Plan(v1, [s1, inside], shared), Path.Combine(outDir, "A.S01E01.mkv"));
        var p2 = Done(Plan(v2, [s2]), Path.Combine(outDir, "A.S01E02.mkv"));
        var p3 = Done(Plan(v3, [], shared), null); // mux LOI -> nguon audio ghep van can
        var got = Mux.UsedSources([p1, p2, p3], outDir);
        Assert.Equal([("A.S01E01.mkv", "video"), ("A.S01E02.mkv", "video"), ("A.S01E01.vi.srt", "sub"), ("A.S01E02.vi.srt", "sub")],
                     got.Select(u => (Path.GetFileName(u.Path), u.Kind)));
        Assert.Equal(["A.S01E01.mkv"], got[0].UsedFor);
        Assert.Equal("Phim gốc", got[0].KindText);
        // tap 3 mux xong -> nguon audio ghep dung chung moi duoc de xuat
        Done(p3, Path.Combine(outDir, "A.S01E03.mkv"));
        var sh = Mux.UsedSources([p1, p2, p3], outDir).Where(u => u.Kind == "donor").ToList();
        Assert.Single(sh);
        Assert.Equal(shared, sh[0].Path);
        Assert.Equal(["A.S01E01.mkv", "A.S01E03.mkv"], sh[0].UsedFor);
        // ket qua mux bi xoa mat / file nguon khong con -> khong de xuat
        File.Delete(Path.Combine(outDir, "A.S01E02.mkv"));
        File.Delete(v1);
        var names = Mux.UsedSources([p1, p2, p3], outDir).Select(u => Path.GetFileName(u.Path)).ToList();
        Assert.DoesNotContain("A.S01E02.mkv", names);
        Assert.DoesNotContain("A.S01E01.mkv", names);
        Assert.DoesNotContain("A.S01E02.vi.srt", names);
    }

    [Fact]
    public void Trash_sources_logs_delete()
    {
        var lib = Lib();
        var trashed = new List<(string, bool)>();
        lib.S.Trash = (p, rb) =>
        {
            trashed.Add((p, rb));
            File.Delete(p);
            return true;
        };
        var a = lib.Add("Phim.2020", J(V(), A(1)));
        var srt = Path.Combine(lib.Root, "Phim.2020.vi.srt");
        File.WriteAllText(srt, "x", Py.Utf8NoBom);
        var rep = lib.S.TrashSources([new UsedFile(a, "video"), srt, Path.Combine(lib.Root, "khong co.srt")]);
        Assert.Equal([a, srt], rep.Done);
        Assert.Empty(rep.Failed);
        Assert.False(File.Exists(a));
        Assert.Equal([true, true], trashed.Select(x => x.Item2)); // mac dinh: vao Thung rac
        var txt = File.ReadAllText(lib.S.Log.Path!, System.Text.Encoding.UTF8);
        Assert.Equal(2, txt.Split("\"Delete\"").Length - 1);
        Assert.Contains("file nguồn đã mux xong → Thùng rác", txt, StringComparison.Ordinal);
        Assert.Contains(lib.S.Job.Lines, l => l.Message.Contains("DỌN FILE NGUỒN: 2 file vào Thùng rác", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- test_naming_db.py: "Tim lai voi ten..." qua MuxSession
    [Fact]
    public void Mux_search_name_tv_applies_to_every_episode()
    {
        var lib = Lib([], new Dictionary<string, string> { ["TmdbApiKey"] = TmdbTests.Key }, NamingDbTests.TmdbHttp());
        var e5 = lib.Add("GoT.S01E05.1080p", J(V(), A(1)));
        var e6 = lib.Add("GoT.S01E06.1080p", J(V(), A(1)));
        var p5 = lib.Plan(e5, "tmdb");
        Assert.Equal("keep", p5.Name.Status);
        Assert.Contains("không tìm thấy series 'GoT'", p5.Name.Note, StringComparison.Ordinal);
        Assert.NotNull(lib.S.SearchName(p5, "Game of Thrones"));
        p5 = lib.S.PlanFile(e5);
        Assert.Equal("need_choice", p5.Status);
        Assert.Equal([1399L], p5.Name.Candidates.Select(x => x.Id));
        lib.S.ChooseName(p5, 1399);
        (p5, var p6) = (lib.S.PlanFile(e5), lib.S.PlanFile(e6));
        Assert.Equal(("ok", "Game of Thrones (2011) - S01E05 - The Wolf and the Lion"), (p5.Status, p5.OutBase));
        Assert.Equal(("ok", "Game of Thrones (2011) - S01E06"), (p6.Status, p6.OutBase));
        Assert.Equal(Path.GetDirectoryName(p5.OutPath), Path.GetDirectoryName(p6.OutPath));
        // "Khong doi ten" -> khong tim duoc
        lib.S.Db = "none";
        Assert.Null(lib.S.SearchName(p5, "Game of Thrones"));
    }
}
