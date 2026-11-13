using System.Text;
using MuxStudio.Core.Tests.Support;
using MuxStudio.Core.Ui;
using static MuxStudio.Core.Tests.Support.MkvFake;
using static MuxStudio.Core.Tests.Support.PyData;

namespace MuxStudio.Core.Tests;

/// <summary>
/// Phần logic THUẦN của giao diện (MuxStudio.Core.Ui — không cần WPF / màn hình). Port các bài thuần của <c>tests/test_gui_logic.py</c>:
/// rút gọn chữ, GridModel, chữ ↔ giá trị cấu hình, "Kiểm tra kết nối" (API giả), bảng "Nhập từ .bat", dòng bảng / màu của ① ② ③, CSV,
/// Hoàn tác, bố cục cửa sổ, thanh các bước, tiến độ, trang Giới thiệu. (Bài <c>Runner</c> phụ thuộc Dispatcher của WPF: không port ở đây.)
/// </summary>
[Collection(GlobalStateCollection.Name)] // bang ngon ngu (LangName), ngon ngu giao dien (I18n)
public sealed class GuiLogicTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly List<MuxLib> _libs = [];

    public void Dispose()
    {
        foreach (var l in _libs)
            l.Dispose();
        _tmp.Dispose();
    }

    private static Dictionary<string, string> Sec(params (string K, string V)[] kv) => kv.ToDictionary(x => x.K, x => x.V, StringComparer.Ordinal);

    // ==========================================================================
    //  widgets
    // ==========================================================================

    [Theory]
    [InlineData(5, "Show.")]
    [InlineData(11, "Show.S01E01")]
    [InlineData(12, "Show....rtv]")]
    [InlineData(13, "Show.S...rtv]")]
    [InlineData(20, "Show.S01E...b[rartv]")]
    [InlineData(33, "Show.S01E01.1080...264-NTb[rartv]")] // (33-3)*0.55 = 16.5 -> 16 (lam tron ve so chan)
    [InlineData(40, "Show.S01E01.1080p.WE....H.264-NTb[rartv]")]
    [InlineData(48, "Show.S01E01.1080p.WEB-DL.DDP5.1.H.264-NTb[rartv]")]
    public void Ellipsize_middle_matches_limit_text(int n, string outp)
    {
        Assert.Equal(outp, UiText.EllipsizeMiddle("Show.S01E01.1080p.WEB-DL.DDP5.1.H.264-NTb[rartv]", n));
        Assert.Equal("", UiText.EllipsizeMiddle("", 10));
        Assert.Equal("", UiText.EllipsizeMiddle(null, 10));
    }

    [Fact]
    public void Grid_model_cells_columns_rows_reset()
    {
        var auto = new Dictionary<(string, string), bool> { [("e1", "E|vi")] = true, [("e1", "I|English")] = false, [("e2", "I|English")] = true };
        var m = new GridModel(["e1", "e2"], ["E|vi", "I|English"], auto);
        Assert.True(m.Present("e1", "E|vi") && !m.Present("e2", "E|vi")); // o xam: tap 2 khong co sub ngoai vi
        Assert.False(m.ToggleCell("e2", "E|vi"));
        Assert.True(m.ToggleCell("e1", "I|English") && m.Differs("e1", "I|English"));
        Assert.Equal(["e1"], m.ChangedRows());
        m.Reset();
        Assert.True(m.ToggleCol("I|English") && m.Get("e1", "I|English") == true && m.Get("e2", "I|English") == true);
        Assert.True(m.ToggleCol("I|English") && m.Get("e1", "I|English") == false && m.Get("e2", "I|English") == false);
        m.Reset();
        Assert.True(m.ToggleRow("e1"));
        Assert.Equal([new("E|vi", true), new("I|English", true)], m.RowValues("e1").ToList());
        Assert.True(m.ToggleRow("e1"));
        Assert.Equal([new("E|vi", false), new("I|English", false)], m.RowValues("e1").ToList());
        m.SetAll(true);
        Assert.True(m.Cells.Values.All(x => x));
        Assert.Equal(["e1"], m.ChangedRows());
        m.Reset();
        Assert.Equal(auto, m.Cells);
        Assert.Empty(m.ChangedRows());
        var m2 = new GridModel(["e1"], ["E|vi"], new Dictionary<(string, string), bool> { [("e1", "E|vi")] = true },
                               new Dictionary<(string, string), bool> { [("e1", "E|vi")] = false, [("e9", "x")] = true });
        Assert.False(m2.Get("e1", "E|vi"));
        Assert.False(m2.Present("e9", "x"));
    }

    // ==========================================================================
    //  Cài đặt
    // ==========================================================================

    [Fact]
    public void Every_field_text_roundtrip()
    {
        foreach (var f in ConfigSchema.Fields)
        {
            var k = SettingsUi.FieldKind(f);
            if (k is "bool" or "choice")
                continue;
            var txt = SettingsUi.ValueToText(f, f.Default);
            Assert.True(SettingsUi.Same(SettingsUi.TextToValue(f, txt), f.Default), f.Key);
        }
        var fe = ConfigSchema.FieldByKey["ExtraToolDirs"];
        Assert.Equal("C:\\A B\\x\nD:\\y", SettingsUi.ValueToText(fe, new List<string> { "C:\\A B\\x", "D:\\y" }));
        Assert.Equal(["C:\\A B\\x", "D:\\y"], (List<string>)SettingsUi.TextToValue(fe, "C:\\A B\\x\n\n D:\\y ")!);
        Assert.Equal(["--split-penalty", "7"], (List<string>)SettingsUi.TextToValue(ConfigSchema.FieldByKey["AlassExtraArgs"], "--split-penalty  7")!);
        Assert.Equal(0.4, SettingsUi.TextToValue(ConfigSchema.FieldByKey["ExistingMinScore"], "0,4"));
        Assert.Equal("1", SettingsUi.ValueToText(ConfigSchema.FieldByKey["DonorMaxDurationDiff"], 1.0));
        Assert.Equal("parts", SettingsUi.FieldKind(ConfigSchema.FieldByKey["CodecTagParts"]));
    }

    [Fact]
    public void Field_errors_are_vietnamese_with_label()
    {
        var e = Assert.Throws<ConfigValueException>(() => SettingsUi.TextToValue(ConfigSchema.FieldByKey["ApiPauseMs"], "nhanh"));
        Assert.Contains("Nghỉ giữa các lượt gọi", e.Message, StringComparison.Ordinal);
        e = Assert.Throws<ConfigValueException>(() => SettingsUi.TextToValue(ConfigSchema.FieldByKey["MinVerifyVotes"], "  "));
        Assert.Contains("không được để trống", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Donor_offset_dict_text()
    {
        var f = ConfigSchema.FieldByKey["DonorOffsetMs"];
        var v = SettingsUi.TextToValue(f, "S01E03 = 1200\n'tap #643' = -500\nPhim.2020.mkv: 12,5\n\n");
        Assert.True(SettingsUi.Same(v, D(("S01E03", 1200L), ("tap #643", -500L), ("Phim.2020.mkv", 12.5))));
        Assert.True(SettingsUi.Same(SettingsUi.TextToValue(f, SettingsUi.ValueToText(f, v)), v));
        var e = Assert.Throws<ConfigValueException>(() => SettingsUi.ParseDictText("S01E01 = 1\nkhông có số"));
        Assert.Contains("dòng 2", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Languages_switches_and_extra()
    {
        var (sw, extra) = SettingsUi.LangsToParts(["vi", "en", "cs", "pt", "ru"]);
        Assert.True(sw["vi"] && sw["cs"] && !sw["ja"]);
        Assert.Equal("pt ru", extra);
        Assert.Equal(["vi", "en", "ja", "pt", "cs"],
                     SettingsUi.LangsFromParts(new Dictionary<string, bool> { ["vi"] = true, ["ja"] = true, ["en"] = true }, "pt, cz vi"));
    }

    [Fact]
    public void Check_connection_with_fake_apis()
    {
        var cfg = new Config();
        Assert.Null(SettingsUi.CheckConnection("tmdb", Sec(), cfg, http: new FakeHttp().Client, sleep: new Sleeper().Sleep).Ok);
        var h = new FakeHttp().Add("GET", "/configuration$", FakeResponse.Json(200, D(("images", D()))));
        var st = SettingsUi.CheckConnection("tmdb", Sec(("TmdbApiKey", "demo-tmdb-key-0123456789abcdef01")), cfg, http: h.Client, sleep: new Sleeper().Sleep);
        Assert.True(st.Ok == true && st.Text.Contains("sẵn sàng", StringComparison.Ordinal));
        h = new FakeHttp().Add("GET", "/configuration$", FakeResponse.Json(401, D(("status_message", "Invalid API key"))));
        st = SettingsUi.CheckConnection("tmdb", Sec(("TmdbApiKey", "demo-tmdb-key-0123456789abcdef01")), cfg, http: h.Client, sleep: new Sleeper().Sleep);
        Assert.True(st.Ok == false && st.Text.Contains("từ chối", StringComparison.Ordinal));
        h = new FakeHttp().Add("GET", @"subsource\.net/api/v1/movies/search",
                               FakeResponse.Json(200, D(("data", L())), ("X-RateLimit-Remaining", "58"), ("X-RateLimit-Limit", "60")));
        st = SettingsUi.CheckConnection("subsource", Sec(("SubSourceApiKey", "demo-ss")), cfg, http: h.Client, sleep: new Sleeper().Sleep);
        Assert.True(st.Ok);
        Assert.Equal("SubSource: key hợp lệ, còn 58/60 lượt/phút", st.Text);
        h = new FakeHttp().Add("POST", @"thetvdb\.com/v4/login", FakeResponse.Json(401, D()));
        st = SettingsUi.CheckConnection("tvdb", Sec(("TvdbApiKey", "demo-tvdb")), cfg, http: h.Client, sleep: new Sleeper().Sleep);
        Assert.True(st.Ok == false && st.Text.Contains("đăng nhập lỗi", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => SettingsUi.CheckConnection("khac", Sec(), cfg));
    }

    [Fact]
    public void Import_rows_mask_secrets_and_filter()
    {
        var p = _tmp["MuxTool [cu].bat"];
        File.WriteAllText(p, "<# : batch\n@echo off\n#>\n$DonorFolder = 'audio_x'\n$TmdbApiKey = 'demo-tmdb-key-0123456789abcdef01'\n$MaxShiftSeconds = 99\n",
                          Py.Utf8NoBom);
        var q = _tmp["MuxTool2.bat"];
        File.WriteAllText(q, "<# : batch\n#>\n$DonorFolder = 'audio_y'\n", Py.Utf8NoBom);
        var imp = BatImporter.ImportFromBat([p, q]);
        var rows = SettingsUi.ImportRows(imp, new Config(), k => k == "TmdbApiKey" ? "demo-cu-0123456789" : "");
        var sec = rows.First(r => r.Key == "TmdbApiKey");
        Assert.True(sec.Secret);
        Assert.Equal("demo…ef01", sec.New);
        Assert.Equal("demo…6789", sec.Current);
        Assert.DoesNotContain("demo-tmdb-key", string.Join(" ", rows.Select(r => r.New + r.Current)), StringComparison.Ordinal);
        var donor = rows.Where(r => r.Key == "DonorFolder").ToList();
        Assert.Equal(2, donor.Count);
        Assert.All(donor, r => Assert.Equal("DonorFolder", r.Group)); // xung dot -> chon mot
        var f = SettingsUi.FilteredImport(imp, new HashSet<string> { "DonorFolder" }, new HashSet<string>());
        Assert.False(f.Values.ContainsKey("MaxShiftSeconds"));
        Assert.Empty(f.SecretValues);
        Assert.Equal(imp.Conflicts.Keys, f.Conflicts.Keys);
    }

    [Fact]
    public void Tool_status_texts_and_startup_summary()
    {
        var ok = new ToolInfo("mkvmerge", "C:\\MKVToolNix\\mkvmerge.exe", true, "v82.0");
        Assert.Equal("✓ v82.0 — tự tìm thấy: C:\\MKVToolNix\\mkvmerge.exe", SettingsUi.ToolStatusText(ok, true));
        Assert.Equal("✓ v82.0 — C:\\MKVToolNix\\mkvmerge.exe", SettingsUi.ToolStatusText(ok, false));
        var miss = new ToolInfo("alass", null, false, Problem: "không tìm thấy");
        Assert.Contains("BẮT BUỘC", SettingsUi.ToolStatusText(miss, true), StringComparison.Ordinal);
        Assert.Contains("đồng bộ phụ đề", SettingsUi.ToolStatusText(miss, true), StringComparison.Ordinal);
        Assert.Equal("error", SettingsUi.ToolStatusLevel(miss));
        var weak = new ToolInfo("ffprobe", "x", false, Problem: "không có file này");
        Assert.Equal("✗ không có file này — x", SettingsUi.ToolStatusText(weak, false));
        Assert.Equal("warn", SettingsUi.ToolStatusLevel(weak));
        var res = Tools.ToolNames.ToDictionary(n => n, n => (new ToolInfo(n, "/" + n, true, "1"), true), StringComparer.Ordinal);
        Assert.Equal([("ok", "Công cụ: mkvmerge ✓ 1 · mkvextract ✓ 1 · ffmpeg ✓ 1 · ffprobe ✓ 1 · alass ✓ 1")], SettingsUi.ToolSummary(res));
        res["alass"] = (miss, true);
        var lines = SettingsUi.ToolSummary(res);
        Assert.Equal("warn", lines[0].Level);
        Assert.Contains("alass ✗", lines[0].Message, StringComparison.Ordinal);
        Assert.Equal("error", lines[1].Level);
        Assert.StartsWith("[!] alass", lines[1].Message, StringComparison.Ordinal);
        Assert.Contains("không làm được: đồng bộ phụ đề (bước ②)", lines[1].Message, StringComparison.Ordinal);
        Assert.Equal(["MkvmergePath", "MkvextractPath", "AlassPath", "FfprobePath", "FfmpegPath"],
                     ConfigSchema.Fields.Where(f => f.Group == "chung").Select(f => f.Key).Take(5));
    }

    // ==========================================================================
    //  ① Tải phụ đề
    // ==========================================================================

    private FetchItem Vi(Action<FetchItem>? set = null)
    {
        var v = new VideoInfo(_tmp["Show.S01E01.mkv"], "Show.S01E01", null, "Show", null, 23.976, ["show"]);
        var it = new FetchItem(v, "vi", _tmp["srt_can_sync", "vi"]);
        set?.Invoke(it);
        return it;
    }

    [Fact]
    public void Fetch_item_status_texts()
    {
        var c = new Candidate("SubSource", 1, "23.976", 0.8, "Show.S01.1080p.WEB-DL", "2 file, 50 tải (gói season)", D(), new CandOffset(2.5, 4, 4));
        Assert.Equal(("chưa tìm", ""), FetchUi.ItemStatus(Vi()));
        Assert.Equal(("CẦN CHỌN — 2 ứng viên", "need"), FetchUi.ItemStatus(Vi(i => (i.Candidates, i.Gathered) = (new List<Candidate> { c, c }, true))));
        Assert.Equal(("không nguồn nào có", "bad"), FetchUi.ItemStatus(Vi(i => (i.Gathered, i.Status) = (true, "không nguồn nào có"))));
        Assert.Equal("ok", FetchUi.ItemStatus(Vi(i => (i.Candidates, i.Chosen, i.ChoiceReason) = (new List<Candidate> { c }, c, "bạn chọn"))).Tag);
        Assert.Equal(("bỏ qua", "dim"), FetchUi.ItemStatus(Vi(i => (i.Skip, i.Candidates) = (true, new List<Candidate> { c }))));
        var done = Vi(i => (i.Chosen, i.Result, i.ResultSrc, i.TimingNote) = (c, _tmp["a.vi.srt"], "SubSource", "Kiểm chứng yếu"));
        Assert.Equal(("đã tải (SubSource): a.vi.srt", "need"), FetchUi.ItemStatus(done));
        Assert.Equal("[SubSource] Show.S01.1080p.WEB-DL", FetchUi.ChosenText(done));
        Assert.Equal([new("n", "1"), new("src", "SubSource"), new("fps", "23.976 KHỚP"), new("score", "80%"), new("off", "+2.5s~"),
                      new("rel", "Show.S01.1080p.WEB-DL"), new("info", "2 file, 50 tải (gói season)")], FetchUi.CandidateRow(c, 0).ToList());
        Assert.Equal("", FetchUi.CandidateTag(new Candidate("SubSource", 2, "?", 0.5, "x", "", null)));
        Assert.Equal("good", FetchUi.CandidateTag(new Candidate("SubSource", 1, "đo: khớp", 0.5, "x", "", null, new CandOffset(0.1, 30, -1))));
        Assert.Equal("bad", FetchUi.CandidateTag(new Candidate("OpenSub", 3, "25", 0.5, "x", "", null)));
        Assert.DoesNotContain("pool", FetchUi.Sources.Select(s => s.Key)); // TD-38: het nguon "kho noi bo"
        Assert.Equal("vi — Vietnamese", FetchUi.LangLabel("vi"));
        var rep = new FetchReport { Ok = 3, UsedExisting = 1, Missing = 1, OsRemaining = 48, OsAllowed = 100, Stopped = true };
        Assert.StartsWith("Tải được: 3  |  Dùng file có sẵn: 1", FetchUi.SummaryText(rep), StringComparison.Ordinal);
        Assert.Contains("OpenSubtitles còn 48/100", FetchUi.SummaryText(rep), StringComparison.Ordinal);
        Assert.Contains("đã dừng", FetchUi.SummaryText(rep), StringComparison.Ordinal);
    }

    [Fact]
    public void Item_status_video_already_has_sub()
    {
        var v = new VideoInfo("Phim.mkv", "Phim", null, "Phim", 2020, 23.976, []);
        var it = new FetchItem(v, "en", "srt_can_sync\\en") { Already = "Phim.mkv", AlreadyNote = "trong MKV: track 2 SRT" };
        Assert.Equal(("phim đã có (trong MKV: track 2 SRT) — không tìm", "dim"), FetchUi.ItemStatus(it));
        var it2 = new FetchItem(v, "vi", "srt_can_sync\\vi") { Already = "Phim.vi.srt", AlreadyNote = "cạnh phim" };
        Assert.Equal(("phim đã có (cạnh phim): Phim.vi.srt — không tìm", "dim"), FetchUi.ItemStatus(it2));
        var it3 = new FetchItem(v, "cs", "srt_can_sync\\cs") { Already = "srt_can_sync\\cs\\Phim.cs.srt" };
        Assert.Equal(("đã có: Phim.cs.srt", "dim"), FetchUi.ItemStatus(it3));
    }

    // ==========================================================================
    //  ② Chuẩn bị phụ đề
    // ==========================================================================

    [Fact]
    public void Prep_lang_options_and_rows()
    {
        var opts = PrepUi.LangOptions();
        Assert.Equal(PrepUi.AutoLang, opts[0]);
        Assert.Equal(PrepUi.OtherLang, opts[^1]);
        Assert.Contains("vi — Vietnamese", opts);
        Assert.Equal("auto", PrepUi.LangFromOption(PrepUi.AutoLang));
        Assert.Equal("cs", PrepUi.LangFromOption("cs — Czech"));
        Assert.Contains("pt — Portuguese", PrepUi.LangOptions(["pt"]));
        var src = _tmp["srt_can_sync", "cs", "x.srt"];
        var r = new RenameRow(src, "vi", "cs") { NewPath = _tmp["Show.S01E01.vi.srt"], EpLabel = "S01E01", Warn = "file nằm trong thư mục 'cs' nhưng bạn chọn 'vi'" };
        var (vals, tag) = PrepUi.RenameValues(r);
        Assert.Equal("bad", tag);
        Assert.Equal("Show.S01E01.vi.srt", vals["new"]);
        Assert.StartsWith("⚠ file nằm trong thư mục 'cs'", vals["why"], StringComparison.Ordinal);
        Assert.Equal("dim", PrepUi.RenameValues(new RenameRow(src, "vi", "vi") { Reason = "đã đúng tên" }).Tag);
        Assert.Equal("need", PrepUi.RenameValues(new RenameRow(src, "vi", "vi") { Reason = "nhập nhằng" }).Tag);
        Assert.Equal("good", PrepUi.RenameValues(new RenameRow(src, "vi", "vi") { Status = "đã đổi tên" }).Tag);
    }

    [Fact]
    public void Prep_pair_values_health_and_csv()
    {
        var p = new SyncPair(_tmp["a.vi.srt"], "vi", "sub") { OutPath = _tmp["Phim.vi.srt"] };
        var (vals, tag) = PrepUi.PairValues(p);
        Assert.Equal("", tag);
        Assert.Equal("", vals["health"]);
        Assert.False(PrepUi.PairNeedsReview(p));
        var h = new Health(1, ["còn lệch 1.3s so với phụ đề gốc"], ["offset"], 812) { ShiftFirst = -2.0, ShiftLast = -2.5, ResOffset = 1.3, ResVotes = 31 };
        p.Result = new SyncResult { Ok = true, Code = 0, Health = h, Cut = 12, Fixed = 3 };
        (vals, tag) = PrepUi.PairValues(p);
        Assert.Equal("need", tag);
        Assert.Equal("CẦN XEM", vals["health"]);
        Assert.Equal(["-2.00", "-2.50", "-0.50", "+1.30", "31", "12"], [vals["first"], vals["last"], vals["spread"], vals["resid"], vals["votes"], vals["cut"]]);
        Assert.StartsWith("bỏ 3 dòng dồn về 00:00:00; còn lệch 1.3s", vals["flags"], StringComparison.Ordinal);
        Assert.True(PrepUi.PairNeedsReview(p));
        var q = new SyncPair(_tmp["b.vi.srt"], "vi", "audio") { Reason = "đã có kết quả" };
        var q2 = new SyncPair(_tmp["c.vi.srt"], "vi", "audio") { OutPath = _tmp["c.srt"] };
        q2.Result = new SyncResult { Ok = false, Code = 1, Rejected = true };
        q2.Result.Notes.Add("alass dịch 300s > 130s");
        Assert.Equal("dim", PrepUi.PairValues(q).Tag);
        Assert.Equal("bad", PrepUi.PairValues(q2).Tag);
        Assert.Equal("TỪ CHỐI", PrepUi.PairValues(q2).Values["health"]);
        var outp = _tmp["[x] bao cao.csv"];
        Assert.Equal(3, PrepUi.ExportCsv([p, q, q2], outp));
        var data = File.ReadAllBytes(outp);
        Assert.True(data.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]));
        var txt = Encoding.UTF8.GetString(data, 3, data.Length - 3);
        Assert.Contains("\r\n", txt, StringComparison.Ordinal);
        Assert.StartsWith("\"Phụ đề\",\"Mốc\",\"Loại mốc\",\"Kết quả\"", txt, StringComparison.Ordinal);
        Assert.Contains("CẦN XEM", txt, StringComparison.Ordinal);
        Assert.Contains("TỪ CHỐI", txt, StringComparison.Ordinal);
    }

    private static VideoAudio Va(string name, string ep, params (string Lang, string Codec, int Ch, string Title, bool Def)[] streams) =>
        new(name, ep, [.. streams.Select((s, i) => new AudioStream(i, s.Lang, s.Codec, s.Ch, s.Title, s.Def))]);

    [Fact]
    public void Audio_rows_and_summary_for_every_file()
    {
        var en = ("eng", "eac3", 6, "Surround", true);
        var cs = ("cze", "aac", 2, "", false);
        List<VideoAudio> files = [Va("Show.S01E01.mkv", "S01E01", en, cs), Va("Show.S01E02.mkv", "S01E02", en, cs), Va("Show.S01E03.mkv", "S01E03", cs),
                                  Va("Show.S01E04.mkv", "S01E04")];
        var rows = PrepUi.AudioRows(files, new AudioChoice("lang", "cs"));
        Assert.Equal(["a0_0", "a0_1", "a1_0", "a1_1", "a2_0", "a3_x"], rows.Select(r => r.Id));
        Assert.Equal(3, PrepUi.AudioIidIndex("a12_3"));
        Assert.Equal([new("ep", "S01E01"), new("file", "Show.S01E01.mkv"), new("idx", "0"), new("lang", "eng"), new("codec", "eac3"), new("ch", "6"),
                      new("title", "Surround"), new("def", "✓"), new("ref", "")], rows[0].Values.ToList());
        Assert.Equal("", rows[1].Values["file"]);
        Assert.Equal(PrepUi.RefMark, rows[1].Values["ref"]);
        Assert.Equal(["", "", "", "", "need", "bad"], rows.Select(r => r.Tag)); // tap 3 khac bo cuc; tap 4 khong doc duoc
        Assert.Equal(PrepUi.RefMark, rows[4].Values["ref"]);
        var byIndex = PrepUi.AudioRows(files, new AudioChoice("index", 1));
        Assert.StartsWith(PrepUi.RefMark + " (Default — không có", byIndex[4].Values["ref"], StringComparison.Ordinal);
        Assert.All(PrepUi.AudioRows(files, null), r => Assert.Equal("", r.Values["ref"]));
        var (txt, level) = PrepUi.AudioSummary(files);
        Assert.Contains("4 file phim", txt, StringComparison.Ordinal);
        Assert.Contains("1 file có bố cục audio KHÁC", txt, StringComparison.Ordinal);
        Assert.Contains("1 file không đọc được", txt, StringComparison.Ordinal);
        Assert.Equal("warn", level);
        Assert.Equal(("Audio track của 2 file phim — mọi file cùng bố cục audio.", "info"), PrepUi.AudioSummary(files[..2]));
        Assert.Equal("warn", PrepUi.AudioSummary([]).Level);
        Assert.Equal("error", PrepUi.AudioSummary(files[3..]).Level);
        Assert.EndsWith(": Show.S01E01.mkv", PrepUi.AudioSummary(files[..1]).Text, StringComparison.Ordinal);
    }

    // ==========================================================================
    //  ③ Mux
    // ==========================================================================

    private MuxLib Lib()
    {
        var l = new MuxLib(_tmp.MovieDir());
        _libs.Add(l);
        return l;
    }

    [Fact]
    public void Mux_overview_audio_rows_and_details()
    {
        const string Movie = "Phim.Thu.2020.1080p.WEB-DL";
        var lib = Lib();
        var j = J(MkvFake.V(), A(1, "eng", ch: 6, bps: 640000), A(2, "eng", ch: 2, bps: 192000), A(3, "eng", name: "Commentary"), A(4, "cze", ch: 2),
                  S(5, "eng"), S(6, "eng", sdh: true, name: "SDH"), S(7, "cze"));
        var p = lib.Plan(lib.Add(Movie, j));
        Assert.Equal("need_choice", p.Status);
        var (vals, tag) = MuxUi.OverviewValues(p, lib.S.OutDir);
        Assert.Equal("need", tag);
        Assert.StartsWith("Cần chọn: có 2 audio tiếng Anh", vals["status"], StringComparison.Ordinal);
        Assert.Equal("3/4", vals["audio"]);
        Assert.Equal($"{Movie} - 1080p HEVC EAC35.1.mkv", vals["new"]);
        var rows = MuxUi.AudioRows(p).ToDictionary(r => r.Id);
        Assert.Null(rows["a3"].Check); // binh luan: khong cho tick
        Assert.Equal("Commentary", rows["a3"].Values["note"]);
        Assert.False(rows["a1"].Check);
        Assert.Equal("giữ — Default", rows["a1"].Values["note"]);
        Assert.Equal("ok", rows["a1"].Tag);
        Assert.True(MuxUi.MultiEn(p));
        var det = string.Join("\n", MuxUi.PlanDetails(p, lib.S.OutDir));
        Assert.Contains("#3 English E-AC-3 5.1", det, StringComparison.Ordinal);
        Assert.Contains("XOÁ (Commentary)", det, StringComparison.Ordinal);
        Assert.Contains("Phụ đề trong MKV:", det, StringComparison.Ordinal);
        lib.S.SetAudioChoice(p, [2], true);
        var p2 = lib.Plan(p.Src);
        Assert.Equal("ok", p2.Status);
        Assert.Equal("", MuxUi.OverviewValues(p2, lib.S.OutDir).Tag);
    }

    [Fact]
    public void Mux_grid_data_and_choice()
    {
        var lib = Lib();
        var a = lib.Add("Show.S01E01.1080p", J(MkvFake.V(), A(1), S(2, "eng"), S(3, "eng", sdh: true, name: "SDH")));
        var b = lib.Add("Show.S01E02.1080p", J(MkvFake.V(), A(1), S(2, "eng")));
        File.WriteAllText(Path.Combine(lib.Root, "Show.S01E01.1080p.vi.srt"), "1\n00:00:01,000 --> 00:00:02,000\nx\n", Py.Utf8NoBom);
        var pa = lib.Plan(a);
        var pb = lib.Plan(b);
        var d = MuxUi.GridData([pa, pb]);
        Assert.Equal(["m0", "m1"], d.Rows);
        Assert.Equal("E|vi", d.Cols[0]);
        var sdh = d.Cols.First(c => c.Contains("SDH", StringComparison.Ordinal));
        Assert.True(d.Auto[("m0", "E|vi")]);
        Assert.False(d.Auto.ContainsKey(("m1", "E|vi")));
        Assert.False(d.Auto[("m0", sdh)]);
        Assert.StartsWith("NGOÀI\nVietnamese", d.Headers["E|vi"], StringComparison.Ordinal);
        Assert.Equal(pb.Rel, d.Labels["m1"]);
        var (ext, ints) = MuxUi.GridChoice(pa, new Dictionary<string, bool> { ["E|vi"] = false, ["I|English"] = true, [sdh] = true });
        Assert.Empty(ext);
        Assert.Equal([2, 3], ints);
        lib.S.SetSubChoice(pa, [], [2, 3]);
        var d2 = MuxUi.GridData([lib.Plan(a), pb], lib.S.SubChoices);
        Assert.Equal(new Dictionary<(string, string), bool> { [("m0", "E|vi")] = false, [("m0", "I|English")] = true, [("m0", sdh)] = true }, d2.Current);
    }

    [Fact]
    public void Mux_parse_inputs_and_summary()
    {
        Assert.Null(MuxUi.ParseMs(""));
        Assert.Equal(1200.0, MuxUi.ParseMs(" +1200 "));
        Assert.Equal(-500.0, MuxUi.ParseMs("-500ms"));
        Assert.Equal(1.5, MuxUi.ParseMs("1,5"));
        Assert.Throws<FormatException>(() => MuxUi.ParseMs("1 giây"));
        Assert.Null(MuxUi.ParseYear(""));
        Assert.Equal(2019, MuxUi.ParseYear("2019"));
        Assert.Throws<FormatException>(() => MuxUi.ParseYear("19"));
        Assert.Equal("Thành công: 3 (1 có cảnh báo)  |  Bỏ qua: 2  |  Cần chọn: 1  |  Lỗi: 0",
                     MuxUi.SummaryText(new MuxReport { Ok = 3, Warning = 1, Skipped = 2, Pending = 1, Failed = 0 }));
    }

    [Fact]
    public void Trash_dialog_rows()
    {
        Assert.Equal(["0 B", "999 B", "1,5 KB", "700 MB", "5,5 GB"], new long[] { 0, 999, 1536, 734003200, (5L << 30) + (1L << 29) }.Select(MuxUi.SizeText));
        var root = _tmp["[YTS.MX] Phim (2020)"];
        Directory.CreateDirectory(root);
        var f = Path.Combine(root, "Phim.2020.vi.srt");
        File.WriteAllBytes(f, new byte[2048]);
        var u = new UsedFile(f, "sub");
        u.UsedFor.Add("Phim (2020).mkv");
        Assert.Equal([new("kind", "Phụ đề ngoài"), new("file", "Phim.2020.vi.srt"), new("size", "2,0 KB"), new("used", "Phim (2020).mkv")],
                     MuxUi.TrashValues(u, root).ToList());
        Assert.Equal("?", MuxUi.TrashValues(new UsedFile(Path.Combine(root, "mat.mkv"), "video"), null)["size"]);
    }

    // ==========================================================================
    //  Hoàn tác (màn hình chung)
    // ==========================================================================

    [Fact]
    public void Undo_entries_all_steps_newest_first()
    {
        var cfg = new Config();
        var dir = _tmp.MovieDir();
        var f = Path.Combine(dir, "a.srt");
        File.WriteAllText(f, "x", Py.Utf8NoBom);
        foreach (var (step, key, day) in new[] { ("subfetch", "SubFetchLogDir", 1), ("mux", "MuxLogDir", 3), ("subprep", "SubPrepLogDir", 2) })
        {
            var lg = new RunLog(dir, cfg.Str(key), step);
            lg.Add(Undo.Create, "", f);
            lg.Add(Undo.Rename, Path.Combine(dir, "b.srt"), f);
            lg.Save(new DateTime(2026, 11, day, 9, 30, 5, DateTimeKind.Local));
        }
        var ents = UndoUi.Entries(dir, cfg);
        Assert.Equal(["mux", "subprep", "subfetch"], ents.Select(e => e.Step));
        Assert.Equal("③ Mux", ents[0].Label);
        Assert.Equal("03/11/2026 09:30:05", ents[0].When);
        Assert.Equal("1 tạo, 1 đổi tên", UndoUi.CountsText(ents[0].Run.Counts));
        Assert.Equal("(trống)", UndoUi.CountsText([]));
        var lines = UndoUi.PlanLines(Undo.PlanUndo(ents[0].Run.Path), dir);
        Assert.Equal("Vào Thùng rác: a.srt", lines[0]);
        Assert.Equal("Đổi lại tên cũ: a.srt  →  b.srt", lines[1]);
    }

    // ==========================================================================
    //  Tên file dài + bố cục theo độ phân giải
    // ==========================================================================

    private const string Long = "[Judas] Sousou no Frieren - S01E05 - The Journey's End and the Hero's Party Departs Again "
                                + "[1080p][HEVC x265 10bit][Dual-Audio][Multi-Subs].mkv";

    [Theory]
    [InlineData(120)]
    [InlineData(80)]
    [InlineData(60)]
    [InlineData(40)]
    [InlineData(30)]
    [InlineData(20)]
    [InlineData(14)]
    [InlineData(9)]
    public void Fit_name_keeps_episode_and_extension(int w)
    {
        var outp = UiText.FitText(Long, w, null, "name");
        Assert.True(outp.Length <= w);
        Assert.Contains("…", outp, StringComparison.Ordinal);
        Assert.Contains("S01E05", outp, StringComparison.Ordinal); // KHONG BAO GIO mat so tap
        if (w >= 30)
        {
            Assert.StartsWith("[Judas] S", outp, StringComparison.Ordinal);
            Assert.EndsWith(".mkv", outp, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Fit_text_other_cases()
    {
        Assert.Equal("ngắn", UiText.FitText("ngắn", 10));
        Assert.Equal("", UiText.FitText(null, 10));
        Assert.Equal("", UiText.FitText("", 0));
        Assert.Equal("Cần chọn: có 2…", UiText.FitText("Cần chọn: có 2 audio tiếng Anh", 16));
        Assert.Equal("", UiText.FitText("abc", 0));
        var outp = UiText.FitText("Dune.Part.Two.2024.2160p.WEB-DL.DDP5.1.Atmos.DV.HDR.H.265-FLUX.mkv", 40, null, "name");
        Assert.Equal(40, outp.Length);
        Assert.StartsWith("Dune.Part.Two.2024", outp, StringComparison.Ordinal);
        Assert.EndsWith("-FLUX.mkv", outp, StringComparison.Ordinal);
        var anime = UiText.FitText("[Judas] One Piece - 1085 [1080p][HEVC x265 10bit][Multi-Subs].mkv", 30, null, "name");
        Assert.Contains("- 1085", anime, StringComparison.Ordinal);
        Assert.True(anime.Length <= 30);
        outp = UiText.FitText(new string('A', 80) + ".S02E10.1080p.mkv", 24, null, "name");
        Assert.Contains("S02E10", outp, StringComparison.Ordinal);
        Assert.True(outp.Length <= 24);
        outp = UiText.FitText(Long, 300, s => s.Sum(c => char.IsUpper(c) ? 9 : 7), "name");
        Assert.Contains("S01E05", outp, StringComparison.Ordinal);
        Assert.Equal(UiText.FitText(Long, 50, null, "name"), UiText.ShortenName(Long, 50));
        Assert.True(UiText.ShortenName(Long, 50).Length <= 50);
    }

    [Theory]
    [InlineData(1280, 720, true, "compact", 1264, 648)]
    [InlineData(1366, 768, true, "compact", 1350, 696)]
    [InlineData(1536, 864, false, "compact", 0, 0)]
    [InlineData(1920, 1080, false, "normal", 0, 0)]
    [InlineData(2560, 1440, false, "large", 0, 0)]
    [InlineData(2048, 1152, false, "normal", 0, 0)]
    [InlineData(3840, 2160, false, "large", 0, 0)]
    public void Plan_window_fits_every_screen(int sw, int sh, bool zoomed, string density, int fw, int fh)
    {
        var p = WindowLayout.PlanWindow(sw, sh);
        Assert.Equal(zoomed, p.Zoomed);
        Assert.Equal(density, p.Density);
        Assert.True(p.Width <= sw - 16 && p.Height <= sh - 72);
        Assert.True(p.MinWidth <= p.Width && p.MinHeight <= p.Height);
        if (fw > 0)
            Assert.Equal((fw, fh), (p.Width, p.Height));
        if (sw >= 1900 && !zoomed)
            Assert.True(p.Width >= 1600);
    }

    [Fact]
    public void Plan_window_saved_size_only_if_it_still_fits_and_ui_factor()
    {
        Assert.Equal((1500, 900), (WindowLayout.PlanWindow(1920, 1080, "1500x900").Width, WindowLayout.PlanWindow(1920, 1080, "1500x900").Height));
        Assert.Equal(1500, WindowLayout.PlanWindow(1920, 1080, "1500x900+12+30").Width);
        var big = WindowLayout.PlanWindow(1920, 1080, "2400x1400");
        Assert.True(big.Width <= 1904 && big.Height <= 1008);
        Assert.Equal(WindowLayout.PlanWindow(1920, 1080).Width, WindowLayout.PlanWindow(1920, 1080, "rác").Width);
        Assert.True(WindowLayout.PlanWindow(1920, 1080, zoomed: true).Zoomed);
        Assert.Equal(1.0, WindowLayout.UiFactor("auto", 1920, 1080));
        Assert.Equal(1.1, WindowLayout.UiFactor("auto", 2560, 1440));
        Assert.Equal(1.25, WindowLayout.UiFactor("125", 1920, 1080));
        Assert.Equal(0.9, WindowLayout.UiFactor("90%", 1280, 720));
        Assert.Equal(2.0, WindowLayout.UiFactor("500", 0, 0));
        Assert.Equal(1.0, WindowLayout.UiFactor("lạ", 0, 0));
    }

    // ==========================================================================
    //  Thanh các bước, khung nhật ký, tiến độ
    // ==========================================================================

    [Fact]
    public void Step_states_next_is_first_undone_and_enabled()
    {
        Assert.Equal(["off", "off", "off", "todo"], Steps.States([false, false, false, false], [false, false, false, true]));
        Assert.Equal(["next", "off", "off", "todo"], Steps.States([false, false, false, false], [true, false, false, true]));
        Assert.Equal(["done", "next", "todo", "todo"], Steps.States([true, false, false, false], [true, true, true, true]));
        Assert.Equal(["done", "done", "done", "next"], Steps.States([true, true, true, false], [true, true, true, true]));
        Assert.Equal("✓ 1. Quét thư mục", Steps.Label(0, "Quét thư mục", "done"));
        Assert.Equal("▶ 2. Tìm phụ đề", Steps.Label(1, "Tìm phụ đề", "next"));
        Assert.Equal("Sang ② →", Steps.Label(3, "Sang ② →", "todo", numbered: false));
        string[] keys = ["extract", "preview", "rename", "next"];
        Assert.Equal(["hidden", "next", "off", "todo"], Steps.VisibleStates(keys, ["extract"], [false, false, false, false], [true, true, false, true]));
        Assert.Equal(["next", "todo", "off", "todo"], Steps.VisibleStates(keys, [], [false, false, false, false], [true, true, false, true]));
        Assert.Equal(["hidden", "done", "next", "todo"], Steps.VisibleStates(keys, ["extract"], [false, true, false, false], [false, true, true, true]));
    }

    [Fact]
    public void Log_drag_height_format_and_progress()
    {
        Assert.Equal(200, LogUi.DragHeight(120, -80, 600));
        Assert.Equal(LogUi.MinHeight, LogUi.DragHeight(120, 500, 600));
        Assert.Equal(600, LogUi.DragHeight(120, -2000, 600));
        Assert.Equal("09:00:01  [OK]   xong\n09:00:02  a\n09:00:03  [LỖI]  hỏng\n",
                     LogUi.FormatLog([("09:00:01", "ok", "xong"), ("09:00:02", "info", "a"), ("09:00:03", "error", "hỏng")]));
        Assert.Equal("", LogUi.FormatLog([]));
        Assert.Equal([0, 37, 100, 99, 0, 100, 0], new[] { (0, 8), (3, 8), (8, 8), (199, 200), (5, 0), (9, 8), (-1, 8) }.Select(x => UiText.ProgressPercent(x.Item1, x.Item2)));
        Assert.Equal("37% · 3/8", UiText.ProgressText(3, 8));
        Assert.Equal("0%", UiText.ProgressText(0, 0));
        Assert.Equal("100% · 8/8", UiText.ProgressText(8, 8));
    }

    // ==========================================================================
    //  Giới thiệu
    // ==========================================================================

    [Fact]
    public void About_page_author_and_third_party_credits()
    {
        Assert.Equal("Vũ Hoàng Anh", AboutUi.Author);
        Assert.Equal("© 2026 Vũ Hoàng Anh", AboutUi.Copyright);
        var names = string.Join(" | ", AboutUi.Credits.Select(c => c.Name));
        foreach (var must in new[] { ".NET", "WPF", "WPF-UI", "CommunityToolkit.Mvvm", "MKVToolNix", "FFmpeg", "alass", "TMDB", "TheTVDB", "OpenSubtitles",
                                     "SubSource", "Titulky.com" })
            Assert.Contains(must, names, StringComparison.Ordinal);
        Assert.All(AboutUi.Credits, c => Assert.True(c.Owner.Length > 0 && c.License.Length > 0 && c.Url.StartsWith("http", StringComparison.Ordinal) && c.Use.Length > 0));
        var lic = AboutUi.Credits.ToDictionary(c => c.Name.Split(' ')[0], c => c.License, StringComparer.Ordinal);
        Assert.Equal("GPL-2.0", lic["MKVToolNix"]);
        Assert.Equal("GPL-3.0", lic["alass"]);
        Assert.Equal("MIT", lic["WPF-UI"]);
        Assert.Equal([AboutUi.Tool, AboutUi.Tool, AboutUi.Tool], AboutUi.ByKind(AboutUi.Tool).Select(c => c.Kind)); // cong cu ngoai: KHONG di kem
        var text = string.Join("\n", AboutUi.AboutLines("9.9.9"));
        Assert.StartsWith("MuxStudio 9.9.9\nTác giả: Vũ Hoàng Anh\n© 2026 Vũ Hoàng Anh", text, StringComparison.Ordinal);
        Assert.Contains("bản quyền của chúng thuộc về tác giả / tổ chức tương ứng", text, StringComparison.Ordinal);
        Assert.Contains("This product uses the TMDB API but is not endorsed or certified by TMDB.", text, StringComparison.Ordinal);
        Assert.Contains("TheTVDB", text, StringComparison.Ordinal);
    }

    // ==========================================================================
    //  Phần trình bày "Concept 3" (WorkbenchUi): nhãn màu, giỏ phụ đề, thẻ luồng — chỉ đọc dữ liệu thật
    // ==========================================================================

    [Fact]
    public void Workbench_basket_video_langs_and_candidate_kinds()
    {
        var v = new VideoInfo(_tmp["Show.S01E01.mkv"], "Show.S01E01", null, "Show", null, 23.976, []);
        var c = new Candidate("SubSource", 1, "23.976", 0.85, "x", "", D(), new CandOffset(1.0, 4, 4));
        List<FetchItem> items =
        [
            new(v, "vi", "d") { Chosen = c },
            new(v, "cs", "d") { Candidates = [c], Gathered = true },
            new(v, "en", "d") { Already = "Show.S01E01.mkv", AlreadyNote = "trong MKV: track 3 SRT" },
        ];
        Assert.Equal([("VI: 1/1", "good"), ("CS: 0/1 · 1 cần chọn", "need"), ("EN: 1/1", "good")], WorkbenchUi.LangBasket(items, ["vi", "cs", "en", "ja"]));
        Assert.Equal([("vi: đã chọn", "info"), ("cs: CẦN CHỌN", "need"), ("en: đã có", "dim")], WorkbenchUi.VideoLangs(items));
        var k = WorkbenchUi.CandidateKinds(c);
        Assert.Equal(("good", "good", "need", "info"), (k["~fps"], k["~score"], k["~off"], k["~src"])); // off.Total > 0 = uoc tinh
        var k3 = WorkbenchUi.CandidateKinds(new Candidate("OpenSub", 3, "25", 0.3, "x", "", null));
        Assert.Equal(("bad", "dim", "dim", "need"), (k3["~fps"], k3["~score"], k3["~off"], k3["~src"]));
    }

    [Fact]
    public void Workbench_sync_chips_and_pair_kinds()
    {
        Assert.Empty(WorkbenchUi.SyncChips([]));
        var a = new SyncPair(_tmp["a.vi.srt"], "vi", "sub") { OutPath = _tmp["a.srt"] };
        var b = new SyncPair(_tmp["b.vi.srt"], "vi", "sub") { Reason = "đã có kết quả" };
        Assert.Equal([("1 cặp sẽ đồng bộ", "info"), ("1 bỏ qua", "dim")], WorkbenchUi.SyncChips([a, b]));
        a.Result = new SyncResult { Ok = true, Health = new Health(1, [], [], 10), Cut = 2 };
        b.Result = new SyncResult { Ok = false, Rejected = true };
        Assert.Equal([("0 TỐT", "good"), ("1 CẦN XEM", "need"), ("0 CÓ VẤN ĐỀ", "bad"), ("1 lỗi/từ chối", "bad")], WorkbenchUi.SyncChips([a, b]));
        var kinds = WorkbenchUi.PairKinds(a, PrepUi.PairValues(a).Tag);
        Assert.Equal(("need", "need"), (kinds["~health"], kinds["~cut"]));
    }

    [Fact]
    public void Workbench_stream_cards_follow_the_plan()
    {
        var lib = Lib();
        var j = J(MkvFake.V(), A(1, "eng", ch: 6, bps: 640000), A(2, "eng", name: "Commentary"), A(3, "cze", ch: 2), S(4, "eng"), S(5, "fre", forced: true));
        var p = lib.Plan(lib.Add("Phim.Cards.2020", j));
        var cards = WorkbenchUi.Streams(p);
        Assert.Equal(["video", "audio", "audio", "audio", "int", "int"], cards.Select(c => c.Kind));
        Assert.Equal("VIDEO #0", cards[0].Badge);
        Assert.Contains("1920x1080", cards[0].Title, StringComparison.Ordinal);
        Assert.Contains("23.976 fps", cards[0].Title, StringComparison.Ordinal);
        var com = cards.First(c => c.Kind == "audio" && c.Id == 2);
        Assert.Equal(("drop", false), (com.State, com.CanToggle)); // binh luan: XOA, khong cho doi
        Assert.StartsWith("XOÁ (Commentary)", com.StateText, StringComparison.Ordinal);
        var a1 = cards.First(c => c.Kind == "audio" && c.Id == 1);
        Assert.True(a1.CanToggle && a1.State == "keep" && a1.IsDefault);
        Assert.Contains("640 kbps", a1.Title, StringComparison.Ordinal);
        Assert.Equal(p.IntSubs.Select(s => s.Keep ? "keep" : "drop"), cards.Where(c => c.Kind == "int").Select(c => c.State));
        var ok = WorkbenchUi.OverviewKinds(p, MuxUi.OverviewValues(p, lib.S.OutDir).Tag);
        Assert.Equal("accent", ok["~tag"]);
    }

    // ==========================================================================
    //  i18n: mọi câu MỚI của giao diện WPF có bản dịch
    // ==========================================================================

    [Fact]
    public void New_wpf_strings_have_english_translations()
    {
        var old = I18n.Language;
        try
        {
            I18n.SetLanguage("en");
            foreach (var s in new[] { "Có", "Không", "Sáng", "Tối", "Theo Windows", "Đặt tên theo", "chưa chọn", "Phụ đề gốc", "Tập / file",
                                      "Bấm để tick / bỏ cả cột", "Đổi tên · đồng bộ (alass)", "Nền giao diện cửa sổ", "(không có gì để hoàn tác)",
                                      "GIỎ PHỤ ĐỀ", "✓ Đã chọn", "+ Chọn", "XOÁ (chưa áp dụng)", "GHÉP VÀO MKV", "Trong file MKV", "Phụ đề trong 'srt_can_sync'",
                                      "VI: 2/2 · 1 cần chọn", "vi: đã chọn", "2 cặp sẽ đồng bộ", "1 lỗi/từ chối" })
                Assert.False(I18n.HasVietnamese(I18n.T(s)), s);
            // cau ghep gia tri van dich theo mau cua ban Python
            Assert.False(I18n.HasVietnamese(I18n.T(PrepUi.CountsText(new PrepCounts(2, 3, 0), new Config()))));
            Assert.False(I18n.HasVietnamese(I18n.T(MuxUi.SummaryText(new MuxReport { Ok = 1 }))));
            Assert.False(I18n.HasVietnamese(I18n.T(Steps.Label(1, "Tìm phụ đề", "next"))));
        }
        finally
        {
            I18n.SetLanguage(old);
        }
    }
}
