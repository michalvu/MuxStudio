using System.Text;
using MuxStudio.Core.Tests.Support;
using static MuxStudio.Core.Tests.Support.PyData;
using static MuxStudio.Core.Tests.Support.SubprepData;

namespace MuxStudio.Core.Tests;

/// <summary>
/// Bước ② (SubPrep): cắt recap, sửa khối 00:00:00, sức khoẻ, ghép tên, đổi tên, đồng bộ (alass GIẢ), dọn dẹp.
/// Port <c>tests/test_subprep.py</c> + test hành vi chọn ngôn ngữ của bản C# (<c>Language_*</c>).
/// </summary>
[Collection(GlobalStateCollection.Name)] // Undo.SendToRecycleBin giả, bảng ngôn ngữ (NormLang đăng ký mã)
public class SubprepTests
{
    private static readonly List<double> REF = Synth.MakeRefTimes(300, start: 30, seed: 1);
    private static readonly List<double> HREF = Synth.MakeRefTimes(400, seed: 5);

    private static void Near(IReadOnlyList<double> expected, IReadOnlyList<double>? actual, double abs)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
            Assert.True(Math.Abs(expected[i] - actual[i]) <= abs, $"[{i}] {expected[i]} != {actual[i]}");
    }

    private static List<string> Messages(PrepSession s) => [.. s.Job.Lines.Select(l => l.Message)];

    /// <summary>Thùng rác giả cho cả test (= monkeypatch <c>undo.trash</c>); trả lại như cũ khi Dispose.</summary>
    private sealed class TrashSpy : IDisposable
    {
        private readonly Action<string> _old = Undo.SendToRecycleBin;

        public TrashSpy(bool delete)
        {
            Undo.SendToRecycleBin = p =>
            {
                Items.Add(p);
                if (delete)
                    File.Delete(p);
            };
        }

        public List<string> Items { get; } = [];

        public void Dispose() => Undo.SendToRecycleBin = _old;
    }

    // ==========================================================================
    //  CẮT RECAP (Remove-LeadingExtra)
    // ==========================================================================

    [Fact]
    public void Trim_recap_cuts_exact_count()
    {
        var (keep, cut) = Subprep.RemoveLeadingExtra(Blocks(RecapSub(REF)), REF);
        Assert.Equal(30, cut);
        Assert.Equal(300, keep!.Count);
        Approx.Equal(REF[0] + 65, keep[0].Start);
    }

    [Fact]
    public void Trim_recap_with_fps_scale()
    {
        var scale = 25 / 23.976;
        List<double> sub = [.. Enumerable.Range(0, 30).Select(i => 1.0 + 2 * i), .. REF.Select(r => (r + 65) / scale)];
        Assert.Equal(30, Subprep.RemoveLeadingExtra(Blocks(sub), REF).Cut);
    }

    [Fact]
    public void Trim_nothing_when_no_recap() =>
        Assert.Equal((null, 0), Subprep.RemoveLeadingExtra(Blocks(REF.Select(r => r + 5)), REF));

    [Fact]
    public void Trim_cancelled_when_recap_too_long() => // recap > 40% (nằm ngoài 30% đầu được phép tìm) -> huỷ
        Assert.Equal((null, 0), Subprep.RemoveLeadingExtra(Blocks(RecapSub(REF, 200, 420)), REF));

    [Fact]
    public void Trim_needs_40_lines_and_strong_relation()
    {
        Assert.Equal((null, 0), Subprep.RemoveLeadingExtra(Blocks(RecapSub(REF[..9], 30)), REF)); // 39 dòng
        var other = Synth.MakeRefTimes(300, start: 30, seed: 99);
        Assert.Equal((null, 0), Subprep.RemoveLeadingExtra(Blocks(RecapSub(other)), REF)); // phim khác
        Assert.Equal((null, 0), Subprep.RemoveLeadingExtra(Blocks(RecapSub(REF)), null));
    }

    [Fact]
    public void Trim_then_write_keeps_hours()
    {
        // TĐ-01: ghi lại sau khi cắt giữ đúng giờ (bản .bat biến 00:45:00 thành 01:45:00)
        using var tmp = new TempDir();
        var @ref = Synth.MakeRefTimes(300, start: 2600, seed: 3);
        var (keep, _) = Subprep.RemoveLeadingExtra(Blocks(RecapSub(@ref)), @ref);
        var p = tmp["x.srt"];
        Srt.WriteBlocks(p, keep!);
        Assert.True(Math.Abs(Srt.CueTimes(p)![0] - (@ref[0] + 65)) <= 0.001);
        Assert.Contains("00:44:", File.ReadAllText(p, Encoding.UTF8), StringComparison.Ordinal);
    }

    // ==========================================================================
    //  KHỐI 00:00:00 (Repair-CollapsedCues)
    // ==========================================================================

    [Fact]
    public void Repair_collapsed_cues()
    {
        var good = Blocks(REF[..100]);
        var (keep, n) = Subprep.RepairCollapsedCues([.. Blocks(Enumerable.Repeat(0.0, 5)), .. good], 2);
        Assert.Equal((5, 100), (n, keep!.Count));
        Assert.Equal((null, 0), Subprep.RepairCollapsedCues([.. Blocks([0.0, 0.01]), .. good], 2)); // <= MaxZeroStartCues
        Assert.Equal((null, 0), Subprep.RepairCollapsedCues([.. Blocks(Enumerable.Repeat(0.0, 5)), .. good[..8]], 2)); // còn < 10 dòng
    }

    // ==========================================================================
    //  SỨC KHOẺ (Test-SubHealth) — mỗi dấu hiệu một ca
    // ==========================================================================

    private static Health HealthOf(TempDir tmp, IReadOnlyList<double> times, IReadOnlyList<double>? @ref = null, bool noRef = false,
                                   double? dur = null, int before = 0, IReadOnlyList<double>? src = null, double d = 1.0, byte[]? raw = null)
    {
        var p = tmp["h.srt"];
        if (raw is not null)
            File.WriteAllBytes(p, raw);
        else
            WriteSrt(p, times, d);
        var vd = dur ?? HREF[^1] + 30;
        return Subprep.SubHealth(p, noRef ? null : @ref ?? HREF, vd, before, src);
    }

    [Fact]
    public void Health_good()
    {
        using var tmp = new TempDir();
        var h = HealthOf(tmp, HREF, src: [.. HREF.Select(t => t - 3)], before: 400);
        Assert.Equal((0, "TỐT"), (h.Level, h.Text));
        Assert.Empty(h.Flags);
        Approx.Equal(3, h.ShiftFirst!.Value);
        Approx.Equal(3, h.ShiftLast!.Value);
        Assert.True(h.ResVotes >= 90 && Math.Abs(h.ResOffset!.Value) < 0.01);
    }

    [Theory]
    [InlineData("zero", "zero", 2)]
    [InlineData("overlap", "overlap", 1)]
    [InlineData("lost", "lost", 1)]
    [InlineData("overrun", "overrun", 2)]
    [InlineData("weak", "weak", 2)]
    [InlineData("fps", "fps", 1)]
    [InlineData("offset", "offset", 1)]
    [InlineData("stretch", "stretch", 1)]
    [InlineData("badchar", "badchar", 1)]
    [InlineData("few", "few", 1)]
    public void Health_each_sign(string kase, string code, int level)
    {
        using var tmp = new TempDir();
        IReadOnlyList<double> times = HREF;
        Health h;
        switch (kase)
        {
            case "zero":
                h = HealthOf(tmp, [.. Enumerable.Repeat(0.0, 5), .. HREF]);
                break;
            case "overlap":
                h = HealthOf(tmp, times, d: 6.0);
                break;
            case "lost":
                h = HealthOf(tmp, times, before: 500);
                break;
            case "overrun":
                h = HealthOf(tmp, times, dur: HREF[^1] - 200);
                break;
            case "weak":
                h = HealthOf(tmp, times, @ref: Synth.MakeRefTimes(400, seed: 77));
                break;
            case "fps":
                h = HealthOf(tmp, [.. HREF.Select(r => r / (25 / 23.976))]);
                break;
            case "offset":
                h = HealthOf(tmp, [.. HREF.Select(r => r + 2)]);
                break;
            case "stretch":
                h = HealthOf(tmp, times, src: [.. HREF.Select(t => t * 0.995)]);
                break;
            case "badchar":
                var raw = Encoding.UTF8.GetBytes(Synth.SrtText(HREF, 1.0));
                var bad = Encoding.UTF8.GetBytes("Dòng 7");
                var i = raw.AsSpan().IndexOf(bad);
                byte[] repl = [(byte)'D', 0xFF, (byte)'n', (byte)'g', (byte)' ', (byte)'7'];
                h = HealthOf(tmp, times, raw: [.. raw[..i], .. repl, .. raw[(i + bad.Length)..]]);
                break;
            default: // few
                h = HealthOf(tmp, HREF[..40], noRef: true);
                break;
        }
        Assert.True(h.Codes.Contains(code), string.Join("; ", h.Flags));
        Assert.Equal(level, h.Level);
        Assert.Equal(h.Codes.Count, h.Flags.Count);
    }

    [Fact]
    public void Health_messages()
    {
        using var tmp = new TempDir();
        var h = HealthOf(tmp, [.. HREF.Select(r => r + 2)], dur: 600);
        Assert.Contains("vẫn lệch -2s", h.Flags);
        Assert.Contains(h.Flags, f => f.StartsWith("dòng cuối ", StringComparison.Ordinal) && f.EndsWith("> phim 10p", StringComparison.Ordinal));
        h = HealthOf(tmp, [.. Enumerable.Repeat(0.0, 5), .. HREF], noRef: true);
        Assert.Equal("5 dòng dính ở 00:00:00", h.Flags[0]);
    }

    [Fact]
    public void Health_reads_ass_and_vtt()
    {
        // TĐ-14: ASS/VTT được đọc mốc (bản .bat báo 'CÓ VẤN ĐỀ: không đọc được nội dung')
        using var tmp = new TempDir();
        var p = tmp["a.ass"];
        File.WriteAllText(p, AssText(HREF), Py.Utf8NoBom);
        var h = Subprep.SubHealth(p, HREF, HREF[^1] + 30, 400, null);
        Assert.Equal((0, "ass", 400), (h.Level, h.Fmt, h.Cues));
        var v = tmp["a.vtt"];
        File.WriteAllText(v, "WEBVTT\n\n" + string.Concat(HREF.Select(t =>
            $"{Srt.FormatStamp(t).Replace(',', '.')} --> {Srt.FormatStamp(t + 1).Replace(',', '.')}\nx\n\n")), Py.Utf8NoBom);
        h = Subprep.SubHealth(v, HREF, HREF[^1] + 30, 400, null);
        Assert.Equal((0, "vtt"), (h.Level, h.Fmt));
        var e = tmp["e.srt"];
        File.WriteAllBytes(e, []);
        Assert.Equal(["unreadable"], Subprep.SubHealth(e, HREF, 0, 0, null).Codes);
    }

    // ==========================================================================
    //  GHÉP PHỤ ĐỀ ↔ MỐC (Resolve-Ref)
    // ==========================================================================

    private static RefIndex Index(IEnumerable<string> names, string kind = "video", string root = @"C:\lib") =>
        RefIndex.Build(names.Select(n => Path.Combine(root, n)), kind, root, [".mkv", ".mp4"]);

    private static RefMatch Rr(RefIndex idx, string name, int n = 5) => Subprep.ResolveRef(idx, name, n);

    [Fact]
    public void Resolve_tiers()
    {
        var idx = Index(["Dune.Part.Two.2024.2160p.mkv", "Dune.2021.1080p.mkv", "Avengers.Endgame.2019.mkv",
                         "Other.Name.S01E05.mkv", "Other.Name.S01E06.mkv"]);
        Assert.Equal("trùng tên", Rr(idx, "Avengers.Endgame.2019").How);
        var m = Rr(idx, "Dune.Part.Two.2024.WEB.vi");
        Assert.Equal("Dune.Part.Two.2024.2160p", m.Ref!.Canon);
        Assert.StartsWith("theo tên phim", m.How, StringComparison.Ordinal);
        Assert.Equal("Dune.2021.1080p", Rr(idx, "Dune.2021.vi").Ref!.Canon); // lọc theo năm
        m = Rr(idx, "Phim Bo S01E05.vi"); // tên khác hẳn -> theo số tập
        Assert.Equal(("Other.Name.S01E05", "theo tập S01E05"), (m.Ref!.Canon, m.How));
        m = Rr(idx, "Unknown.Film.Part.3.vi");
        Assert.True(m.Ref is null && m.Reason == "không tìm được phim tương ứng" && m.Details.Count > 0);
        Assert.Equal("không nhận diện được số tập", Rr(idx, "Hoan toan khac.vi").Reason);
    }

    [Fact]
    public void Resolve_title_ambiguity_reported()
    {
        var m = Rr(Index(["Movie.Night.2010.mkv", "Movie.Night.2012.mkv"]), "Movie Night.vi");
        Assert.Null(m.Ref);
        Assert.StartsWith("nhập nhằng theo tên phim (2 mốc", m.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_anime_absolute_numbers()
    {
        // TĐ-31a: anime đánh số tuyệt đối — bản .bat luôn 'NHẬP NHẰNG theo tên phim'
        var idx = Index(new[] { 643, 644, 645 }.Select(e => $"[Judas] One Piece - {e:0000} [1080p].mkv"));
        Assert.Equal("[Judas] One Piece - 0644 [1080p]", Rr(idx, "One Piece - 0644.vi").Ref!.Canon);
        Assert.Equal("One.Piece.S16E644.1080p",
                     Rr(Index(["One.Piece.S16E644.1080p.mkv", "One.Piece.S16E645.1080p.mkv"]), "One Piece - 0644.vi").Ref!.Canon);
    }

    [Fact]
    public void Resolve_never_pairs_different_episode()
    {
        // TĐ-31b: thiếu phim tập 5, chỉ có tập 6 → bản .bat ghép phụ đề tập 5 vào tập 6
        var m = Rr(Index(["Show.S01E06.1080p.WEB-DL.mkv"]), "Show.S01E05.vi", n: 1);
        Assert.True(m.Ref is null && m.Reason == "không tìm được mốc");
        Assert.Contains(m.Details, d => d.Contains("số tập khác", StringComparison.Ordinal));
        // tầng 4 (1 phụ đề - 1 mốc) cũng không ghép khi số tập khác nhau
        Assert.Null(Rr(Index(["Foo.Bar.S01E05.mkv"]), "Completely Different - 07", n: 1).Ref);
    }

    [Fact]
    public void Resolve_tier3_ambiguity_reported()
    {
        // TĐ-31c: tầng 3 nhiều phim cùng khớp → báo nhập nhằng (bản .bat lấy bừa file đầu)
        var idx = Index(["Frieren.Beyond.Journeys.End.S01E05.1080p.mkv", "Frieren.Beyond.Journeys.End.S02E05.1080p.mkv"]);
        var m = Rr(idx, "Sousou no Frieren - 05.vi");
        Assert.True(m.Ref is null && m.Reason == "nhập nhằng tập E05 (2 file mốc khớp)");
    }

    [Fact]
    public void Resolve_direct_one_to_one()
    {
        var m = Rr(Index(["Foo.Bar.2019.mkv"]), "Something E05", n: 1);
        Assert.Equal("Foo.Bar.2019", m.Ref!.Canon);
        Assert.StartsWith("ghép thẳng", m.How, StringComparison.Ordinal);
        Assert.Null(Rr(Index(["Foo.Bar.2019.mkv"]), "Something E05", n: 2).Ref);
    }

    [Fact]
    public void Select_variant_and_by_name_prefer_plain()
    {
        var idx = Index(["Movie.A.2010.en.srt", "Movie.A.2010.srt", "Movie.A.2010.vi.srt"], kind: "sub");
        Assert.Equal("Movie.A.2010.srt", Path.GetFileName(idx.ByName["movie.a.2010"].File));
        Assert.Equal("Movie.A.2010.srt", Path.GetFileName(Subprep.SelectRefVariant(idx.Items)!.File));
        var v = Index(["Movie.A.2010.en.srt", "Movie.A.2010.mkv"], kind: "").Items;
        Assert.True(Subprep.SelectRefVariant(v)!.IsVideo);
    }

    // ==========================================================================
    //  CHỈ MỤC MỐC TRONG THƯ MỤC THẬT
    // ==========================================================================

    [Fact]
    public void Index_excludes_work_folders()
    {
        // TĐ-13 + §6.5: srt_pool, srt_can_sync, Output, audio_nguon không bao giờ làm mốc
        using var lib = new SubprepLib();
        using var fa = new FakeAlass();
        using var s = Sess(lib.Root, fa);
        foreach (var mode in new[] { "sub", "audio" })
        {
            var idx = s.BuildIndex(mode)!;
            var names = idx.Items.Select(r => Path.GetRelativePath(lib.Root, r.File).Replace('\\', '/')).ToList();
            Assert.DoesNotContain(names, n => n.Split('/')[0] is "srt_pool" or "audio_nguon" or "Output" or "srt_can_sync");
        }
        Assert.Equal(3, s.BuildIndex("audio")!.Items.Count);
        Assert.Contains("Mốc lấy từ: srt_goc (3)", Messages(s)[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Counts()
    {
        using var lib = new SubprepLib();
        using var fa = new FakeAlass();
        WriteSrt(lib.C("vi", "a.srt"), REF);
        using var s = Sess(lib.Root, fa);
        Assert.Equal(new PrepCounts(3, 1, 3), s.Counts());
    }

    // ==========================================================================
    //  ĐỔI TÊN
    // ==========================================================================

    [Fact]
    public void Plan_rename_lang_from_folder_writes_nothing()
    {
        using var lib = new SubprepLib();
        using var fa = new FakeAlass();
        WriteSrt(lib.C("vi", "show.s01e01.srt"), REF);
        WriteSrt(lib.C("cs", "Show S01E02 [1080p].srt"), REF);
        var before = Tree(lib.Root);
        List<RenameRow> rows;
        using (var s = Sess(lib.Root, fa))
            rows = s.PlanRename("sub", "auto");
        Assert.Equal(before, Tree(lib.Root));
        var got = rows.ToDictionary(r => Path.GetFileName(r.Src), r => (Path.GetFileName(r.NewPath!), r.EpLabel));
        Assert.Equal(2, got.Count);
        Assert.Equal(($"{Show[0]}.vi.srt", "S01E01"), got["show.s01e01.srt"]);
        Assert.Equal(($"{Show[1]}.cs.srt", "S01E02"), got["Show S01E02 [1080p].srt"]);
    }

    [Fact]
    public void Rename_reasons()
    {
        using var lib = new SubprepLib();
        using var fa = new FakeAlass();
        WriteSrt(lib.C("Show.S01E01.srt"), REF); // không rõ ngôn ngữ
        WriteSrt(lib.C("vi", $"{Show[1]}.vi.srt"), REF); // đã đúng tên
        WriteSrt(lib.C("vi", "Show.S01E03.a.srt"), REF);
        WriteSrt(lib.C("vi", "Show.S01E03.b.srt"), REF); // trùng đích với file trên
        WriteSrt(lib.C("en", "Show.S01E02.srt"), REF);
        WriteSrt(lib.C("en", $"{Show[1]}.en.srt"), REF); // đích đã tồn tại
        using var s = Sess(lib.Root, fa);
        var rows = s.PlanRename("sub", "auto").ToDictionary(r => Path.GetFileName(Path.GetDirectoryName(r.Src)) == "en"
                                                                ? "en/" + Path.GetFileName(r.Src) : Path.GetFileName(r.Src));
        Assert.Equal("không đoán được ngôn ngữ", rows["Show.S01E01.srt"].Reason);
        Assert.Equal("đã đúng tên", rows[$"{Show[1]}.vi.srt"].Reason);
        Assert.True(rows["Show.S01E03.a.srt"].Ok);
        Assert.Equal("trùng đích với 'Show.S01E03.a.srt'", rows["Show.S01E03.b.srt"].Reason);
        Assert.Equal("đích đã tồn tại", rows["en/Show.S01E02.srt"].Reason);
    }

    [Fact]
    public void Cz_suffix_becomes_cs()
    {
        // TĐ-15: hậu tố .cz → .cs (cả khi chọn 'cz' trong ô Ngôn ngữ)
        using var lib = new SubprepLib();
        using var fa = new FakeAlass();
        WriteSrt(lib.C("Show.S01E01.cz.srt"), REF);
        using var s = Sess(lib.Root, fa);
        var row = Assert.Single(s.PlanRename("sub", "auto"));
        Assert.Equal($"{Show[0]}.cs.srt", Path.GetFileName(row.NewPath));
        var row2 = Assert.Single(s.PlanRename("sub", "cz"));
        Assert.Equal("cs", row2.Lang);
    }

    [Fact]
    public void Execute_rename_then_undo()
    {
        using var lib = new SubprepLib();
        using var fa = new FakeAlass();
        var src = WriteSrt(lib.C("vi", "show.s01e01.srt"), REF);
        RenameReport rep;
        using (var s = Sess(lib.Root, fa))
            rep = s.ExecuteRename(s.PlanRename("sub", "auto"));
        var nw = lib.C("vi", $"{Show[0]}.vi.srt");
        Assert.True(rep.Ok == 1 && File.Exists(nw) && !File.Exists(src));
        var log = Undo.ReadRun(rep.LogPath!);
        Assert.Equal([("Rename", "show.s01e01.srt", Path.GetFileName(nw), "S01E01")],
                     log.Select(r => (r["Action"], Path.GetFileName(r["From"]), Path.GetFileName(r["To"]), r["Detail"])));
        Undo.ExecuteUndo(Undo.PlanUndo(rep.LogPath!));
        Assert.True(File.Exists(src) && !File.Exists(nw));
    }

    [Fact]
    public void Unticked_rows_not_renamed()
    {
        using var lib = new SubprepLib();
        using var fa = new FakeAlass();
        WriteSrt(lib.C("vi", "show.s01e01.srt"), REF);
        using var s = Sess(lib.Root, fa);
        var rows = s.PlanRename("sub", "auto");
        rows[0].Selected = false;
        Assert.Equal(0, s.ExecuteRename(rows).Ok);
        Assert.True(File.Exists(lib.C("vi", "show.s01e01.srt")));
    }

    // ==========================================================================
    //  ĐỒNG BỘ (alass giả)
    // ==========================================================================

    [Fact]
    public void Sync_sub_mode_full_pipeline()
    {
        // Chuyển mã + cắt recap trên BẢN SAO (TĐ-12), alass không cờ, kết quả UTF-8 cạnh phim, sức khoẻ, nhật ký
        using var lib = new SubprepLib();
        using var fa = new FakeAlass();
        var @ref = lib.Ref[Show[0]];
        var src = WriteSrt(lib.C("vi", "show.s01e01.srt"), RecapSub(@ref), enc: 1258,
                           texts: [.. Enumerable.Range(0, 430).Select(i => $"Đây là tiên {i}")]);
        fa.Set(("offset", -65.0));
        var renamed = lib.C("vi", $"{Show[0]}.vi.srt");
        byte[] raw;
        List<SyncPair> pairs;
        SyncReport rep;
        using (var s = Sess(lib.Root, fa))
        {
            s.ExecuteRename(s.PlanRename("sub", "auto"));
            raw = File.ReadAllBytes(renamed);
            pairs = s.PlanSync("sub");
            Assert.Equal([lib.P($"{Show[0]}.vi.srt")], pairs.Select(p => p.OutPath));
            Assert.True(Math.Abs(pairs[0].Relation!.Offset - -65) <= 0.1);
            Assert.False(File.Exists(lib.P($"{Show[0]}.vi.srt"))); // lập kế hoạch không ghi gì
            rep = s.ExecuteSync(pairs);
        }
        Assert.False(File.Exists(src));
        Assert.Equal(raw, File.ReadAllBytes(renamed)); // file nguồn KHÔNG bị sửa (TĐ-12)
        var call = Assert.Single(fa.Calls());
        Assert.Empty(FakeAlass.Flags(call));
        Assert.Equal(lib.P("srt_goc", $"{Show[0]}.srt"), call["ref"]);
        var outp = lib.P($"{Show[0]}.vi.srt");
        var txt = Py.Utf8Strict.GetString(File.ReadAllBytes(outp)); // đã chuyển UTF-8
        Assert.Contains("Đây là tiên 30\r\n", txt, StringComparison.Ordinal);
        Assert.DoesNotContain("Đây là tiên 29\r\n", txt, StringComparison.Ordinal); // 30 dòng recap đã cắt
        Near(@ref, Srt.CueTimes(outp), 0.002);
        var r = pairs[0].Result!;
        Assert.Equal((1, 1, 30, 0), (rep.Ok, rep.Good, r.Cut, r.Health!.Level));
        Assert.Contains("cp1258 → UTF-8", r.Notes);
        var runs = Directory.GetFiles(lib.P("_subprep_logs"), "run_*.csv");
        Assert.Single(runs); // đổi tên + đồng bộ = MỘT lần chạy
        Assert.Equal([("Rename", $"{Show[0]}.vi.srt"), ("Create", $"{Show[0]}.vi.srt")],
                     Undo.ReadRun(runs[0]).Select(x => (x["Action"], Path.GetFileName(x["To"]))));
        Assert.StartsWith("health_", Path.GetFileName(rep.HealthPath), StringComparison.Ordinal);
        var head = File.ReadAllText(rep.HealthPath!, Encoding.UTF8).Split("\r\n")[0];
        Assert.Contains("DichDau", head, StringComparison.Ordinal);
        Assert.True(File.ReadAllBytes(rep.HealthPath!).AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])); // utf-8-sig
    }

    [Fact]
    public void Sync_ass_stays_ass()
    {
        // Q7 + TĐ-14: phụ đề ASS giữ ASS, không cắt recap (ghi lý do), sức khoẻ đọc được
        using var lib = new SubprepLib();
        using var fa = new FakeAlass();
        var a = lib.C("vi", $"{Show[0]}.vi.ass");
        Directory.CreateDirectory(Path.GetDirectoryName(a)!);
        File.WriteAllText(a, AssText(lib.Ref[Show[0]].Select(r => r + 3)), Py.Utf8NoBom);
        fa.Set(("offset", -3.0));
        List<SyncPair> pairs;
        using (var s = Sess(lib.Root, fa))
        {
            pairs = s.PlanSync("sub");
            s.ExecuteSync(pairs);
        }
        var outp = lib.P($"{Show[0]}.vi.ass");
        Assert.StartsWith("[Script Info]", File.ReadAllText(outp, Encoding.UTF8), StringComparison.Ordinal);
        var r = pairs[0].Result!;
        Assert.Equal((0, "ass"), (r.Health!.Level, r.Health.Fmt));
        Assert.Contains("không cắt recap (ASS)", r.Notes);
    }

    [Fact]
    public void Sync_repairs_collapsed_block()
    {
        using var lib = new SubprepLib();
        using var fa = new FakeAlass();
        WriteSrt(lib.C("vi", $"{Show[0]}.vi.srt"), [.. lib.Ref[Show[0]].Select(r => r + 2)]);
        fa.Set(("offset", -2.0), ("zero_first", 6L));
        using var s = Sess(lib.Root, fa);
        var pairs = s.PlanSync("sub");
        s.ExecuteSync(pairs);
        Assert.Equal(6, pairs[0].Result!.Fixed);
        Assert.Equal(394, Srt.ReadBlocks(lib.P($"{Show[0]}.vi.srt"))!.Count);
    }

    [Fact]
    public void Max_shift_zero_still_repairs_and_checks()
    {
        // TĐ-20: MaxShiftSeconds = 0 chỉ tắt ngưỡng từ chối, vẫn sửa 00:00:00 + kiểm tra sức khoẻ
        using var lib = new SubprepLib();
        using var fa = new FakeAlass();
        WriteSrt(lib.C("vi", $"{Show[0]}.vi.srt"), [.. lib.Ref[Show[0]].Select(r => r + 2)]);
        fa.Set(("offset", -2.0), ("zero_first", 6L));
        using var s = Sess(lib.Root, fa, [new("MaxShiftSeconds", 0L)]);
        var pairs = s.PlanSync("sub");
        s.ExecuteSync(pairs);
        Assert.True(pairs[0].Result!.Fixed == 6 && pairs[0].Result!.Health is not null);
    }

    [Fact]
    public void Max_shift_sub_mode_only_warns()
    {
        using var lib = new SubprepLib();
        using var fa = new FakeAlass();
        WriteSrt(lib.C("vi", $"{Show[0]}.vi.srt"), [.. lib.Ref[Show[0]].Select(r => r + 200)]);
        fa.Set(("offset", -200.0));
        using var s = Sess(lib.Root, fa);
        var pairs = s.PlanSync("sub");
        var rep = s.ExecuteSync(pairs);
        Assert.True(rep.Ok == 1 && File.Exists(lib.P($"{Show[0]}.vi.srt")));
        Assert.Contains(pairs[0].Result!.Notes, n => n.StartsWith("dịch 200s", StringComparison.Ordinal));
    }

    [Fact]
    public void Sync_skip_existing_and_replace_into_trash()
    {
        using var lib = new SubprepLib();
        using var fa = new FakeAlass();
        WriteSrt(lib.C("vi", $"{Show[0]}.vi.srt"), lib.Ref[Show[0]]);
        var old = WriteSrt(lib.P($"{Show[0]}.vi.srt"), REF[..50]);
        using (var s = Sess(lib.Root, fa))
            Assert.Equal("đã có kết quả", Assert.Single(s.PlanSync("sub")).Reason);
        using var trash = new TrashSpy(delete: true);
        using var s2 = Sess(lib.Root, fa, [new("SkipExistingOut", false)]);
        var pairs = s2.PlanSync("sub");
        var rep = s2.ExecuteSync(pairs);
        Assert.Equal(1, rep.Ok);
        Assert.Equal([old], trash.Items); // file cũ vào Thùng rác, không ghi đè mất
        Assert.Equal(["Delete", "Create"], Undo.ReadRun(rep.LogPath!).Select(r => r["Action"]));
    }

    [Fact]
    public void Sync_duplicate_target_and_untagged_lang()
    {
        // TĐ-32: phụ đề chưa đổi tên (không hậu tố) trong thư mục vi\ → kết quả vẫn có .vi; hai phụ đề cùng đích → bỏ cái sau
        using var lib = new SubprepLib();
        using var fa = new FakeAlass();
        WriteSrt(lib.C("vi", "Show.S01E01.a.srt"), lib.Ref[Show[0]]);
        WriteSrt(lib.C("vi", "Show.S01E01.b.srt"), lib.Ref[Show[0]]);
        using var s = Sess(lib.Root, fa);
        var pairs = s.PlanSync("sub");
        Assert.Equal($"{Show[0]}.vi.srt", Path.GetFileName(pairs[0].OutPath));
        Assert.Equal("trùng đích với 'Show.S01E01.a.srt'", pairs[1].Reason);
    }

    [Fact]
    public void Alass_failure_and_retry_without_flags()
    {
        using var lib = new SubprepLib();
        using var fa = new FakeAlass();
        WriteSrt(lib.C("vi", $"{Show[0]}.vi.srt"), lib.Ref[Show[0]]);
        fa.Set(("fail", L(D(("ref", "sub"), ("flags", true)))));
        using (var s = Sess(lib.Root, fa, [new("AlassArgsSubMode", L("--no-split"))]))
        {
            var pairs = s.PlanSync("sub");
            var rep = s.ExecuteSync(pairs);
            Assert.Equal([["--no-split"], []], fa.Calls().Select(FakeAlass.Flags));
            Assert.True(rep.Ok == 1 && pairs[0].Result!.Attempt == 1);
        }
        File.Delete(fa.LogPath);
        fa.Set(("fail", L(D(("ref", "any"), ("flags", false)), D(("ref", "any"), ("flags", true)))));
        File.Delete(lib.P($"{Show[0]}.vi.srt"));
        using (var s = Sess(lib.Root, fa))
        {
            var pairs = s.PlanSync("sub");
            var rep = s.ExecuteSync(pairs);
            Assert.True(rep.Errors == 1 && !File.Exists(lib.P($"{Show[0]}.vi.srt")));
            Assert.Equal("thất bại (mã 1)", pairs[0].Status);
            Assert.Contains("codec_long_name", pairs[0].Result!.OutputTail[^1], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Audio_mode_without_ffmpeg_tries_video_then_no_flags()
    {
        // Thiếu ffmpeg: đưa thẳng file phim (có cảnh báo); lỗi kèm cờ → thử lại không cờ
        using var lib = new SubprepLib();
        using var fa = new FakeAlass();
        WriteSrt(lib.C("vi", $"{Show[0]}.vi.srt"), lib.Ref[Show[0]]);
        fa.Set(("fail", L(D(("ref", "video"), ("flags", true)))));
        using var s = Sess(lib.Root, fa);
        var pairs = s.PlanSync("audio");
        var rep = s.ExecuteSync(pairs);
        Assert.Contains(Messages(s), m => m.Contains("missing field codec_long_name", StringComparison.Ordinal));
        Assert.Equal([($"{Show[0]}.mkv", "--no-split --disable-fps-guessing"), ($"{Show[0]}.mkv", "")],
                     fa.Calls().Select(c => (Path.GetFileName((string)c["ref"]!), string.Join(' ', FakeAlass.Flags(c)))));
        Assert.Equal(1, rep.Ok);
    }

    [Fact]
    public void Audio_mode_fps_mismatch_from_srt_goc_drops_flag()
    {
        // TĐ-18: mốc audio + có phụ đề gốc trong srt_goc → đo được lệch FPS → bỏ --disable-fps-guessing
        using var lib = new SubprepLib();
        using var fa = new FakeAlass();
        WriteSrt(lib.C("vi", $"{Show[0]}.vi.srt"), [.. lib.Ref[Show[0]].Select(r => r / (25 / 23.976))]);
        WriteSrt(lib.C("vi", $"{Show[1]}.vi.srt"), [.. lib.Ref[Show[1]].Select(r => r + 1)]);
        using var s = Sess(lib.Root, fa);
        var pairs = s.PlanSync("audio");
        var (p1, p2) = (pairs[0], pairs[1]);
        Assert.True(p1.Relation!.FpsMismatch);
        Assert.Equal(["--no-split"], p1.Flags);
        Assert.Equal($"srt_goc\\{Show[0]}.srt", p1.RelationSrc);
        Assert.False(p2.Relation!.FpsMismatch);
        Assert.Equal(["--no-split", "--disable-fps-guessing"], p2.Flags);
    }

    [Fact]
    public void Audio_mode_rejects_big_shift()
    {
        using var lib = new SubprepLib();
        using var fa = new FakeAlass();
        WriteSrt(lib.C("vi", $"{Show[0]}.vi.srt"), lib.Ref[Show[0]]);
        fa.Set(("offset", 150.0));
        using var s = Sess(lib.Root, fa);
        var pairs = s.PlanSync("audio");
        var rep = s.ExecuteSync(pairs);
        Assert.True(rep.Rejected == 1 && rep.Ok == 0 && !File.Exists(lib.P($"{Show[0]}.vi.srt")));
        Assert.Equal("từ chối (dịch 150s)", pairs[0].Status);
    }

    [Fact]
    public void Unsupported_alass_flags_in_session()
    {
        using var lib = new SubprepLib();
        using var fa = new FakeAlass();
        fa.Set(("help", "USAGE: alass --no-split"));
        using var s = Sess(lib.Root, fa);
        Assert.Equal(["--no-split"], s.AlassFlags(["--no-split", "--disable-fps-guessing"]));
        Assert.Contains(Messages(s), m => m.Contains("KHÔNG hỗ trợ: --disable-fps-guessing", StringComparison.Ordinal));
    }

    [Fact]
    public void Loose_sup_in_srt_goc_used_as_ref()
    {
        // .sup bạn tự bỏ vào srt_goc → đọc mốc (lập kế hoạch không ghi gì); lúc chạy mới ghi .srt cạnh nó
        using var lib = new SubprepLib();
        using var fa = new FakeAlass();
        File.Delete(lib.P("srt_goc", $"{Show[2]}.srt"));
        var @ref = lib.Ref[Show[2]];
        var ev = @ref.SelectMany(t => new[] { (t, "show"), (t + 1.0, "hide") }).ToArray();
        var sup = lib.P("srt_goc", $"{Show[2]}.sup");
        File.WriteAllBytes(sup, Synth.PgsStream(ev));
        WriteSrt(lib.C("vi", $"{Show[2]}.vi.srt"), [.. @ref.Select(r => r + 4)]);
        fa.Set(("offset", -4.0));
        List<SyncPair> pairs;
        using (var s = Sess(lib.Root, fa))
        {
            pairs = s.PlanSync("sub");
            Assert.Equal(sup, pairs[0].Ref!.File);
            Assert.False(File.Exists(Path.ChangeExtension(sup, ".srt")));
            s.ExecuteSync(pairs);
        }
        var made = Path.ChangeExtension(sup, ".srt");
        Assert.Equal(400, Srt.CueTimes(made)!.Count);
        var rows = Undo.ReadRun(Directory.GetFiles(lib.P("_subprep_logs"), "run_*.csv")[0]);
        Assert.Contains(("Create", Path.GetFileName(made)), rows.Select(r => (r["Action"], Path.GetFileName(r["To"]))));
        Assert.Equal(0, pairs[0].Result!.Health!.Level);
    }

    [Fact]
    public void Stop_after_current()
    {
        using var lib = new SubprepLib();
        using var fa = new FakeAlass();
        foreach (var s_ in Show[..2])
            WriteSrt(lib.C("vi", $"{s_}.vi.srt"), lib.Ref[s_]);
        using var s = Sess(lib.Root, fa);
        var pairs = s.PlanSync("sub");
        s.Job.StopAfterCurrent();
        var rep = s.ExecuteSync(pairs);
        Assert.True(rep.Stopped && rep.Ok == 0);
        Assert.Empty(fa.Calls());
    }

    [Fact]
    public void Cancel_now_during_sync_writes_nothing()
    {
        // (bản C#) "Dừng ngay" khi alass đang chạy: diệt tiến trình, không ghi file dở dang, vẫn lưu nhật ký
        using var lib = new SubprepLib();
        using var fa = new FakeAlass();
        WriteSrt(lib.C("vi", $"{Show[0]}.vi.srt"), lib.Ref[Show[0]]);
        fa.Set(("sleep", 30.0));
        using var s = Sess(lib.Root, fa);
        var pairs = s.PlanSync("sub");
        SyncReport? rep = null;
        var t = new Thread(() => rep = s.ExecuteSync(pairs));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        t.Start();
        while (s.Job.RunningCount == 0 && sw.Elapsed < TimeSpan.FromSeconds(10))
            Thread.Sleep(20);
        s.Job.CancelNow();
        Assert.True(t.Join(TimeSpan.FromSeconds(10)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15));
        Assert.True(rep!.Stopped);
        Assert.False(File.Exists(lib.P($"{Show[0]}.vi.srt")));
        Assert.Contains("[!] Đã dừng ngay — file đang làm dở không được ghi.", Messages(s));
    }

    [Fact]
    public void Preview_commands_sub_mode()
    {
        using var lib = new SubprepLib();
        using var fa = new FakeAlass();
        WriteSrt(lib.C("vi", $"{Show[0]}.vi.srt"), lib.Ref[Show[0]]);
        using var s = Sess(lib.Root, fa, [new("AlassArgsSubMode", L("--no-split"))]);
        var p = Assert.Single(s.PlanSync("sub"));
        var lines = s.PreviewCommands(p);
        Assert.Contains("--no-split", lines[0], StringComparison.Ordinal);
        Assert.Contains($"{Show[0]}.srt", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("# thử lại không kèm tham số", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("# rồi chép", lines[^1], StringComparison.Ordinal);
        Assert.Contains(@"""<thư mục tạm>\Show.S01E01.1080p.WEB-DL.vi.srt""", lines[0], StringComparison.Ordinal); // có dấu cách -> ngoặc kép
    }

    // ==========================================================================
    //  DỌN DẸP + HOÀN TÁC
    // ==========================================================================

    [Fact]
    public void Cleanup_and_undo_whole_run()
    {
        using var lib = new SubprepLib();
        using var fa = new FakeAlass();
        WriteSrt(lib.C("vi", "show.s01e01.srt"), [.. lib.Ref[Show[0]].Select(r => r + 3)]);
        WriteSrt(lib.C("vi", "show.s01e02.srt"), [.. lib.Ref[Show[1]].Select(r => r + 3)]);
        fa.Set(("offset", -3.0), ("fail", L()));
        using var trash = new TrashSpy(delete: false);
        CleanupReport rep;
        using (var s = Sess(lib.Root, fa))
        {
            s.ExecuteRename(s.PlanRename("sub", "auto"));
            s.ExecuteSync(s.PlanSync("sub"));
            var plan = s.CleanupTargets()!;
            Assert.Equal((2, "Thùng rác (khôi phục được)"), (plan.Done, plan.Where));
            Assert.Equal([$"{Show[0]}.vi.srt", $"{Show[1]}.vi.srt"], plan.Targets("subs").Select(Path.GetFileName));
            Assert.Equal([$"{Show[0]}.srt", $"{Show[1]}.srt"], plan.Targets("refs").Select(Path.GetFileName));
            Assert.Empty(plan.Targets("none"));
            rep = s.ExecuteCleanup(plan, "refs");
        }
        Assert.Equal(2, rep.Deleted);
        Assert.Equal(["srt_goc", "srt_goc"], trash.Items.Select(p => Path.GetFileName(Path.GetDirectoryName(p))));
        var log = Directory.GetFiles(lib.P("_subprep_logs"), "run_*.csv")[0];
        Assert.Equal(["Rename", "Rename", "Create", "Create", "Delete", "Delete"], Undo.ReadRun(log).Select(r => r["Action"]));
        // Hoàn tác cả lượt: kết quả vào Thùng rác, tên cũ trả lại
        var up = Undo.PlanUndo(log);
        Assert.Equal(["rename_back", "rename_back", "trash", "trash"], up.Ops.Select(o => o.Kind).Order(StringComparer.Ordinal));
        Assert.Contains(up.Notes, n => n.Contains("Thùng rác", StringComparison.Ordinal));
    }

    [Fact]
    public void Cleanup_none_when_nothing_synced()
    {
        using var lib = new SubprepLib();
        using var fa = new FakeAlass();
        using var s = Sess(lib.Root, fa);
        Assert.Null(s.CleanupTargets());
    }

    [Fact]
    public void Extract_needs_mkvtoolnix()
    {
        using var lib = new SubprepLib();
        using var fa = new FakeAlass();
        using var s = Sess(lib.Root, fa);
        Assert.Empty(s.PlanExtractRefs());
        Assert.Contains(Messages(s), m => m.Contains("mkvmerge", StringComparison.Ordinal));
    }

    // ==========================================================================
    //  (bản C#) Ô "Ngôn ngữ": chọn một ngôn ngữ = CHỈ xử lý phụ đề của ngôn ngữ đó
    // ==========================================================================

    /// <summary>vi\ (2 tập), cs\ (tập 1), gốc srt_can_sync: hậu tố .vi (tập 3) + một file không rõ ngôn ngữ (tập 2).</summary>
    private static void MixedLanguages(SubprepLib lib, double shift = 3)
    {
        WriteSrt(lib.C("vi", "show.s01e01.srt"), [.. lib.Ref[Show[0]].Select(r => r + shift)]);
        WriteSrt(lib.C("vi", "show.s01e02.srt"), [.. lib.Ref[Show[1]].Select(r => r + shift)]);
        WriteSrt(lib.C("cs", "Show S01E01 [cz].srt"), [.. lib.Ref[Show[0]].Select(r => r + shift)]);
        WriteSrt(lib.C("Show.S01E03.vi.srt"), [.. lib.Ref[Show[2]].Select(r => r + shift)]);
        WriteSrt(lib.C("Show.S01E02.srt"), [.. lib.Ref[Show[1]].Select(r => r + shift)]);
    }

    [Fact]
    public void Language_specific_sync_only_that_language()
    {
        using var lib = new SubprepLib();
        using var fa = new FakeAlass();
        MixedLanguages(lib);
        using var s = Sess(lib.Root, fa);
        var vi = s.PlanSync("sub", lang: "vi");
        // thứ tự quét: đường dẫn đầy đủ, không phân biệt hoa thường (srt_can_sync\Show… trước srt_can_sync\vi\…)
        Assert.Equal(["Show.S01E03.vi.srt", "show.s01e01.srt", "show.s01e02.srt"], vi.Select(p => Path.GetFileName(p.Sub)));
        Assert.All(vi, p => Assert.Equal("vi", p.Lang));
        Assert.Contains("[i] Chỉ xử lý phụ đề 'vi': 3 file; bỏ qua 2 file của ngôn ngữ khác hoặc chưa rõ ngôn ngữ.", Messages(s));
        var cs = Assert.Single(s.PlanSync("sub", lang: "cz")); // 'cz' = 'cs' (TĐ-15)
        Assert.Equal(("Show S01E01 [cz].srt", "cs", $"{Show[0]}.cs.srt"), (Path.GetFileName(cs.Sub), cs.Lang, Path.GetFileName(cs.OutPath)));
        Assert.Equal(5, s.PlanSync("sub", lang: "auto").Count); // Tự nhận diện: mọi ngôn ngữ (như Python)
        Assert.Empty(s.PlanSync("sub", lang: "en"));
        Assert.Contains("[!] Không có phụ đề 'en' nào trong 'srt_can_sync'.", Messages(s));
    }

    [Fact]
    public void Language_specific_rename_only_that_language()
    {
        using var lib = new SubprepLib();
        using var fa = new FakeAlass();
        MixedLanguages(lib);
        using var s = Sess(lib.Root, fa);
        var rows = s.PlanRename("sub", "cs");
        var row = Assert.Single(rows);
        Assert.Equal(($"{Show[0]}.cs.srt", ""), (Path.GetFileName(row.NewPath), row.Warn));
        var rep = s.ExecuteRename(rows);
        Assert.Equal(1, rep.Ok);
        // phụ đề vi\ và file chưa rõ ngôn ngữ KHÔNG bị gắn nhãn 'cs' (bản Python gắn ép + chỉ cảnh báo)
        Assert.True(File.Exists(lib.C("vi", "show.s01e01.srt")) && File.Exists(lib.C("Show.S01E02.srt")));
        Assert.True(File.Exists(lib.C("cs", $"{Show[0]}.cs.srt")));
    }

    [Fact]
    public void Language_specific_cleanup_only_that_language()
    {
        using var lib = new SubprepLib();
        using var fa = new FakeAlass();
        MixedLanguages(lib);
        fa.Set(("offset", -3.0));
        using var trash = new TrashSpy(delete: true);
        using var s = Sess(lib.Root, fa);
        s.ExecuteSync(s.PlanSync("sub", lang: "auto")); // đồng bộ mọi ngôn ngữ trong cùng phiên
        Assert.Equal(5, s.Processed.Count);
        var cs = s.CleanupTargets("cs")!;
        Assert.Equal(["Show S01E01 [cz].srt"], cs.SubFiles.Select(Path.GetFileName));
        Assert.Equal([$"{Show[0]}.srt"], cs.RefFiles.Select(Path.GetFileName)); // srt_goc: vẫn cho xoá như cũ (theo lựa chọn người dùng)
        var vi = s.CleanupTargets("vi")!;
        Assert.Equal(["Show.S01E03.vi.srt", "show.s01e01.srt", "show.s01e02.srt"], vi.SubFiles.Select(Path.GetFileName));
        Assert.Equal(5, s.CleanupTargets("auto")!.SubFiles.Count);
        Assert.Null(s.CleanupTargets("en"));
        var rep = s.ExecuteCleanup(vi, "subs");
        Assert.Equal(3, rep.Deleted);
        // phụ đề cs và file chưa rõ ngôn ngữ còn nguyên
        Assert.True(File.Exists(lib.C("cs", "Show S01E01 [cz].srt")) && File.Exists(lib.C("Show.S01E02.srt")));
        Assert.Equal(2, rep.Left["srt_can_sync"]);
    }

    [Fact]
    public void Language_messages_have_english_translation()
    {
        try
        {
            I18n.SetLanguage("en");
            Assert.Equal("[i] Only 'vi' subtitles: 3 file(s); skipped 2 file(s) in another or unknown language.",
                         I18n.Tr("[i] Chỉ xử lý phụ đề 'vi': 3 file; bỏ qua 2 file của ngôn ngữ khác hoặc chưa rõ ngôn ngữ."));
            Assert.Equal("[!] No 'en' subtitles in 'srt_can_sync'.", I18n.Tr("[!] Không có phụ đề 'en' nào trong 'srt_can_sync'."));
        }
        finally
        {
            I18n.SetLanguage("vi");
        }
    }

    [Fact]
    public void Language_specific_ignores_files_of_unknown_language()
    {
        // Người dùng chọn "Bỏ qua": file ở gốc srt_can_sync không có thư mục ngôn ngữ / hậu tố thì không xử lý khi chọn 'vi'
        using var lib = new SubprepLib();
        using var fa = new FakeAlass();
        WriteSrt(lib.C("Show.S01E01.srt"), lib.Ref[Show[0]]);
        using var s = Sess(lib.Root, fa);
        Assert.Empty(s.PlanSync("sub", lang: "vi"));
        Assert.Empty(s.PlanRename("sub", "vi"));
        var p = Assert.Single(s.PlanSync("sub", lang: "auto"));
        Assert.Equal(("", $"{Show[0]}.srt"), (p.Lang, Path.GetFileName(p.OutPath))); // Tự nhận diện: như Python (không nhãn)
    }

    // ==========================================================================
    //  ĐỐI CHIẾU VỚI PYTHON (Data/python_subprep_cases.json — tools/gen_dotnet_subprep.py)
    // ==========================================================================

    private const string Cases = "python_subprep_cases.json";

    private static List<double>? RefOf(object? name) =>
        name is string n ? [.. ((List<object?>)((OrderedDictionary<string, object?>)PyCases.Load(Cases)["refs"]!)[n]!).Select(x => Py.AsDouble(x!))] : null;

    private static List<double>? Times(object? x) => x is List<object?> l ? [.. l.Select(v => Py.AsDouble(v!))] : null;

    [Fact]
    public void Trim_and_repair_same_as_python()
    {
        PyCases.Check("trim", x =>
        {
            var a = (List<object?>)x!;
            var t = Times(a[1])!;
            return L([.. new[] { 20, 40 }.Select(mv =>
            {
                var (keep, cut) = Subprep.RemoveLeadingExtra(Blocks(t), RefOf(a[0]), mv);
                if (keep is null)
                    return (object?)null;
                Assert.Equal(t.Skip(cut), keep.Select(b => b.Start));
                return L(keep.Count, cut);
            })]);
        }, Cases);
        PyCases.Check("repair", x =>
        {
            var a = (List<object?>)x!;
            var t = Times(a[0])!;
            var (keep, n) = Subprep.RepairCollapsedCues(Blocks(t), (int)(long)a[1]!);
            if (keep is null)
                return null;
            Assert.Equal(t.Where(v => v >= 0.05), keep.Select(b => b.Start));
            return L(keep.Count, n);
        }, Cases);
    }

    [Fact]
    public void Health_same_as_python()
    {
        using var tmp = new TempDir();
        var k = 0;
        PyCases.Check("health", x =>
        {
            var a = (List<object?>)x!;
            var fmt = (string)a[0]!;
            var t = Times(a[1])!;
            var p = tmp[$"h{k++}.{fmt}"];
            File.WriteAllText(p, fmt == "ass" ? AssText(t) : Synth.SrtText(t, Py.AsDouble(a[6]!)), Py.Utf8NoBom);
            return L([.. new[] { (20, 2), (60, 0) }.Select(o =>
            {
                var h = Subprep.SubHealth(p, RefOf(a[2]), Py.AsDouble(a[3]!), (int)(long)a[4]!, Times(a[5]), o.Item1, o.Item2);
                return (object?)L(h.Level, h.Flags.Cast<object?>().ToList(), h.Codes.Cast<object?>().ToList(), h.Cues, h.ShiftFirst,
                                  h.ShiftLast, h.ResOffset, h.ResVotes, h.ResFps, h.Fmt, h.Text, h.Spread);
            })]);
        }, Cases);
    }

    [Fact]
    public void Resolve_same_as_python()
    {
        PyCases.Check("resolve", x =>
        {
            var a = (List<object?>)x!;
            var idx = RefIndex.Build(((List<object?>)a[0]!).Select(f => Path.Combine(@"C:\lib", ((string)f!).Replace('/', '\\'))), (string)a[1]!,
                                     @"C:\lib", [".mkv", ".mp4"]);
            var m = Subprep.ResolveRef(idx, (string)a[3]!, (int)(long)a[2]!, Py.AsDouble(a[4]!), Py.AsDouble(a[5]!));
            var info = L(idx.Items.Count, idx.ByKey.Keys.Cast<object?>().ToList(), idx.Canons().Cast<object?>().ToList(), idx.Summary());
            return L(info, m.Ref?.Canon, m.Ref is null ? null : Path.GetFileName(m.Ref.File), m.How, m.Reason, m.Details.Cast<object?>().ToList());
        }, Cases);
    }

    [Fact]
    public void Audio_choice_and_numbers_same_as_python()
    {
        PyCases.Check("audio", x =>
        {
            var a = (List<object?>)x!;
            var streams = ((List<object?>)a[0]!).Cast<List<object?>>()
                .Select(s => new AudioStream((int)(long)s[0]!, (string)s[1]!, (string)s[2]!, (int)(long)s[3]!, (string)s[4]!, (bool)s[5]!)).ToList();
            var (i, d, miss) = Subprep.PickAudioStream(streams, new AudioChoice((string)a[1]!, a[2]));
            return L(i, d, miss);
        }, Cases);
        PyCases.Check("num", x =>
        {
            var a = (List<object?>)x!;
            var v = Py.AsDouble(a[0]!);
            var d = (int)(long)a[1]!;
            return L(Subprep.Num(v, d), Subprep.Signed(v, d), Py.Fixed(v, 2, plus: true), Py.Fixed(v, d));
        }, Cases);
    }
}
