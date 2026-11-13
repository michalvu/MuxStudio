using System.Globalization;
using System.Text;
using JDict = System.Collections.Generic.OrderedDictionary<string, object?>;

namespace MuxStudio.Core;

/// <summary>
/// Bước ② — phiên làm việc (port <c>subprep.PrepSession</c>). Mẫu "lập kế hoạch → thực thi" (§4):
/// <list type="bullet">
/// <item><see cref="PlanExtractRefs"/> → <see cref="ExtractRefs"/>: trích phụ đề gốc (không SDH, theo <c>RefSubLangs</c>; chỉ có PGS thì
/// đọc mốc thời gian) từ MKV vào <c>srt_goc</c>.</item>
/// <item><see cref="PlanRename"/> → <see cref="ExecuteRename"/>: đổi tên phụ đề trong <c>srt_can_sync</c> cho khớp tên phim.</item>
/// <item><see cref="PlanSync"/> → <see cref="ExecuteSync"/>: mỗi file làm trên BẢN SAO tạm (TĐ-12): chuẩn hoá UTF-8 → cắt recap (chỉ SRT,
/// TĐ-14) → alass (mốc audio: LUÔN trích WAV, thứ tự thử TĐ-19) → sửa khối 00:00:00 → kiểm tra sức khoẻ (luôn chạy, TĐ-20) → chép
/// kết quả cạnh file phim.</item>
/// <item><see cref="CleanupTargets"/> → <see cref="ExecuteCleanup"/>: dọn file nguồn (Thùng rác).</item>
/// </list>
/// Hàm <c>Plan*</c> không ghi gì vào thư mục phim (chỉ thư mục tạm). Mọi thao tác ghi vào CÙNG một nhật ký hoàn tác
/// <c>_subprep_logs\run_*.csv</c> cho cả lượt (đổi tên + đồng bộ + dọn dẹp), lưu sau mỗi bước. Phụ đề ASS giữ nguyên là ASS (Q7).
/// <para><b>Khác bản Python (cố ý, theo yêu cầu người dùng 13/11/2026):</b> ô "Ngôn ngữ" chọn một ngôn ngữ cụ thể thì đổi tên / đồng
/// bộ / dọn dẹp CHỈ áp dụng cho phụ đề của đúng ngôn ngữ đó (thư mục con <c>srt_can_sync\vi\</c> hoặc hậu tố <c>.vi</c>); phụ đề
/// của ngôn ngữ khác hoặc chưa rõ ngôn ngữ bị bỏ qua. Bản Python xử lý MỌI phụ đề và gắn ép nhãn đã chọn. "Tự nhận diện" giữ như
/// Python (mọi ngôn ngữ).</para>
/// </summary>
public sealed class PrepSession : IDisposable
{
    private readonly Dictionary<string, AlassFlags> _flagCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int? Index, string Desc)> _audioCache = new(StringComparer.Ordinal);
    private Dictionary<string, (string File, string Tag)>? _goc;

    /// <param name="toolPaths">(cho test) đường dẫn công cụ; bỏ trống = tự tìm (<see cref="ToolPaths.Find"/>).</param>
    public PrepSession(string targetDir, Config cfg, Job? job = null, ToolPaths? toolPaths = null)
    {
        Td = targetDir;
        Cfg = cfg;
        Job = job ?? new Job();
        Temp = Directory.CreateTempSubdirectory("muxstudio_prep_").FullName;
        var paths = toolPaths ?? ToolPaths.Find(cfg, Td);
        Mkvmerge = paths.Mkvmerge;
        Mkvextract = paths.Mkvextract;
        Ffmpeg = paths.Ffmpeg;
        Ffprobe = paths.Ffprobe;
        AlassCmd = paths.Alass is { Count: > 0 } a ? [.. a] : null;
        Log = new RunLog(Td, cfg.Str("SubPrepLogDir"), "subprep", cfg.Int("KeepUndoRuns"));
    }

    public string Td { get; }
    public Config Cfg { get; }
    public Job Job { get; private set; }
    public string Temp { get; }
    public string? Mkvmerge { get; }
    public string? Mkvextract { get; }
    public string? Ffmpeg { get; }
    public string? Ffprobe { get; }
    public List<string>? AlassCmd { get; }
    public Dictionary<string, JDict?> JsonCache { get; } = new(StringComparer.Ordinal);
    public Dictionary<(string Path, string Kind), List<JDict>> FfprobeCache { get; } = [];
    public RunLog Log { get; }
    public List<Processed> Processed { get; } = [];
    public List<HealthRow> HealthRows { get; } = [];

    /// <summary><c>set_job</c>: giao diện — phiên sống qua nhiều lần bấm nút, mỗi lần một <see cref="Core.Job"/> mới.</summary>
    public void SetJob(Job job) => Job = job;

    // ------------------------------------------------------------------ thu muc
    public string SubDir => Py.Join(Td, Cfg.Str("SubFolderName"));

    public string? ExDir => Cfg.Str("ExtractFolder") is { Length: > 0 } e ? Py.Join(Td, e) : null;

    public string RefDir => Cfg.Str("RefFolderName") is { Length: > 0 } r ? Py.Join(Td, r) : Td;

    public int NRef => Cfg.Int("FpsSampleRef");

    public int NTgt => Cfg.Int("FpsSampleTgt");

    private bool ScanSub => Cfg.Bool("ScanSubFolders");

    /// <summary>
    /// <c>excluded_dirs</c>: thư mục KHÔNG quét làm mốc (<c>Test-ExcludedPath</c>). TĐ-13: luôn cộng thêm thư mục audio nguồn, kho
    /// <c>srt_pool</c>, <c>srt_can_sync</c> (và các thư mục nhật ký).
    /// </summary>
    public List<string> ExcludedDirs()
    {
        var c = Cfg;
        List<string> names = [.. c.List("RefExcludeFolders"), c.Str("SubFolderName"), c.Str("DonorFolder"), Tools.LegacyPoolFolder,
                              c.Str("SubPrepLogDir"), c.Str("SubFetchLogDir"), c.Str("MuxLogDir")];
        var keepScan = new HashSet<string>([c.Str("ExtractFolder").ToLowerInvariant(), c.Str("RefFolderName").ToLowerInvariant()],
                                           StringComparer.Ordinal);
        keepScan.Remove("");
        var outp = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in names)
        {
            var low = n.ToLowerInvariant();
            if (n.Length > 0 && !keepScan.Contains(low) && seen.Add(low))
                outp.Add(Py.Join(Td, n));
        }
        return outp;
    }

    public bool IsExcluded(string path) => ExcludedDirs().Any(d => Tools.IsUnder(path, d));

    public List<string> ListSubFiles() => Tools.WalkFiles(SubDir, ScanSub, Cfg.List("SubExts"));

    public List<string> ListVideos()
    {
        var skip = ExcludedDirs();
        if (ExDir is { } ex)
            skip.Add(ex);
        return Tools.WalkFiles(Td, ScanSub, Cfg.List("VideoExts"), skip);
    }

    /// <summary><c>counts</c>: §5.2 mục 1 — số phim, số phụ đề trong <c>srt_can_sync</c>, số phụ đề gốc trong <c>srt_goc</c>.</summary>
    public PrepCounts Counts()
    {
        var refs = 0;
        if (ExDir is { } ex && Directory.Exists(ex))
            refs = Tools.WalkFiles(ex, ScanSub, [.. Cfg.List("SubExts"), ".sup"]).Count;
        return new PrepCounts(ListVideos().Count, ListSubFiles().Count, refs);
    }

    /// <summary><c>sample_audio_streams</c>: audio track của file mẫu (file phim đầu tiên) — bảng chọn audio làm mốc.</summary>
    public (string? Video, List<AudioStream> Streams) SampleAudioStreams()
    {
        var vids = ListVideos();
        if (vids.Count == 0)
            return (null, []);
        return (vids[0], Probe.AudioStreams(Probe.FfprobeStreams(vids[0], Ffprobe, "a", Job, FfprobeCache)));
    }

    /// <summary><c>all_audio_streams</c>: audio track của MỌI file phim (TĐ-35) — để thấy tập nào có audio nào trước khi chọn mốc.</summary>
    public List<VideoAudio> AllAudioStreams()
    {
        var vids = ListVideos();
        var outp = new List<VideoAudio>();
        for (var i = 0; i < vids.Count; i++)
        {
            var v = vids[i];
            Job.Check();
            Job.Progress(i + 1, vids.Count, Subprep.Name(v));
            var info = Episodes.ParseEpisode(Subprep.Stem(v));
            var streams = Probe.AudioStreams(Probe.FfprobeStreams(v, Ffprobe, "a", Job, FfprobeCache));
            outp.Add(new VideoAudio(v, info?.Label ?? "", streams));
        }
        return outp;
    }

    private void EnsureDir(string d)
    {
        if (Path.Exists(d))
            return;
        var missing = new List<string>();
        var p = d;
        while (!Path.Exists(p))
        {
            missing.Add(p);
            p = Subprep.Parent(p);
        }
        Directory.CreateDirectory(d);
        for (var i = missing.Count - 1; i >= 0; i--)
            Log.Add(Undo.Mkdir, "", missing[i]);
    }

    /// <summary><c>_replace_existing</c>: đích đã có file (khi tắt "bỏ qua") → đưa file cũ vào Thùng rác trước, không ghi đè mất (§7).</summary>
    private bool ReplaceExisting(string dst, string why)
    {
        if (!Path.Exists(dst))
            return true;
        if (Undo.Trash(dst, Cfg.Bool("DeleteToRecycleBin")))
        {
            Log.Add(Undo.Delete, dst, "", why);
            return true;
        }
        Job.Error($"  [!] Không dọn được file cũ: {Subprep.Name(dst)}");
        return false;
    }

    public string? SaveLog() => Log.Save();

    // ------------------------------------------------------------------ chi muc moc
    private (string Tmp, string Dst)? SupTiming(string sup)
    {
        if (!Cfg.Bool("PgsTimingFallback"))
            return null;
        var d = Path.Combine(Temp, "sup");
        Directory.CreateDirectory(d);
        var tmp = Path.Combine(d, $"{Guid.NewGuid().ToString("N")[..8]}_{Subprep.Stem(sup)}.srt");
        var n = Pgs.PgsToTimingSrt(sup, tmp, Py.Str(Cfg["PgsTimingText"]));
        if (n <= 0)
        {
            Job.Warn($"  [!] Không đọc được PGS: {Subprep.Name(sup)}");
            return null;
        }
        Job.Dim($"  [pgs] {Subprep.Name(sup)} → {Subprep.Stem(sup)}.srt ({n.ToString(CultureInfo.InvariantCulture)} mốc thời gian)");
        return (tmp, Path.ChangeExtension(sup, ".srt"));
    }

    /// <summary><c>build_index</c>: <paramref name="mode"/> = <c>sub</c> (mốc = phụ đề gốc) | <c>audio</c> (mốc = file phim). Hiện "Mốc lấy từ: …".</summary>
    public RefIndex? BuildIndex(string mode)
    {
        var c = Cfg;
        var kind = mode == "sub" ? "sub" : "video";
        var dirs = new List<string>();
        foreach (var d in new[] { RefDir, ExDir })
        {
            if (d is not null && Directory.Exists(d) && !dirs.Any(x => Subprep.SamePath(d, x)))
                dirs.Add(d);
        }
        if (dirs.Count == 0)
        {
            Job.Error($"[LỖI] Không tìm thấy thư mục mốc: {RefDir}");
            return null;
        }
        List<string> exts = [.. c.List("VideoExts"), .. c.List("SubExts")];
        if (c.Bool("PgsTimingFallback") && kind == "sub")
            exts.Add(".sup");
        var skip = ExcludedDirs();
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in dirs)
        {
            foreach (var f in Tools.WalkFiles(d, ScanSub, exts, skip))
            {
                if (seen.Add(Py.NormCase(Py.AbsPath(f))))
                    files.Add(f);
            }
        }
        var idx = RefIndex.Build(files, kind, Td, c.List("VideoExts"), SupTiming);
        if (idx.Items.Count == 0)
        {
            if (kind == "sub")
                Job.Error($"[LỖI] Không có phụ đề gốc nào trong '{c.Str("ExtractFolder")}'.");
            else
                Job.Error("[LỖI] Không tìm thấy file phim nào.");
            return null;
        }
        Job.Info(idx.Summary());
        Job.Dim($"    Mốc lấy từ: {idx.SourceText()}");
        return idx;
    }

    private RefMatch Resolve(RefIndex idx, string sub, int nSubs) =>
        Subprep.ResolveRef(idx, Subprep.Stem(sub), nSubs, Cfg.Double("TitleMinScore"), Cfg.Double("TitleMinMargin"));

    /// <summary><c>_norm_lang</c>: '' / 'auto' → <c>auto</c>; còn lại → mã chuẩn (TĐ-15: 'cz' → 'cs'), đăng ký mã lạ.</summary>
    public static string NormLang(string? lang)
    {
        var x = (lang ?? "").Trim().ToLowerInvariant().TrimStart('.');
        if (x is "" or "auto")
            return "auto";
        var code = Naming.LangFromWord(x) is { Length: > 0 } w ? w : x;
        Naming.RegisterLanguage(code);
        return code;
    }

    /// <summary>
    /// (Bản C#) chọn ngôn ngữ cụ thể → chỉ giữ phụ đề CỦA ngôn ngữ đó; <paramref name="langOf"/> = ngôn ngữ của file theo đúng cách
    /// bước đó tự nhận diện. Phụ đề ngôn ngữ khác / chưa rõ ngôn ngữ bị bỏ qua (ghi một dòng nhật ký).
    /// </summary>
    private List<string> OnlyLang(List<string> files, string lang, Func<string, string> langOf)
    {
        if (lang == "auto")
            return files;
        var keep = files.Where(f => langOf(f) == lang).ToList();
        if (keep.Count < files.Count)
        {
            Job.Dim($"[i] Chỉ xử lý phụ đề '{lang}': {keep.Count.ToString(CultureInfo.InvariantCulture)} file; bỏ qua "
                    + $"{(files.Count - keep.Count).ToString(CultureInfo.InvariantCulture)} file của ngôn ngữ khác hoặc chưa rõ ngôn ngữ.");
        }
        if (keep.Count == 0)
            Job.Warn($"[!] Không có phụ đề '{lang}' nào trong '{Cfg.Str("SubFolderName")}'.");
        return keep;
    }

    // ------------------------------------------------------------------ 0. trich phu de goc

    /// <summary><c>plan_extract_refs</c>: lập kế hoạch trích phụ đề gốc (chỉ đọc <c>mkvmerge -J</c>, không ghi gì).</summary>
    public List<ExtractRow> PlanExtractRefs()
    {
        var c = Cfg;
        if (string.IsNullOrEmpty(Mkvmerge) || string.IsNullOrEmpty(Mkvextract))
        {
            Job.Error("[LỖI] Không tìm thấy mkvmerge / mkvextract. Chọn thư mục MKVToolNix trong Cài đặt.");
            return [];
        }
        if (ExDir is not { } ex)
        {
            Job.Error("[LỖI] 'Thư mục phụ đề gốc' (ExtractFolder) không được để trống.");
            return [];
        }
        var videos = ListVideos().Where(v => Subprep.Suffix(v).Equals(".mkv", StringComparison.OrdinalIgnoreCase)).ToList();
        if (videos.Count == 0)
        {
            Job.Warn("[!] Không tìm thấy file .mkv nào để trích (chỉ file MKV mới trích được).");
            return [];
        }
        Job.Info($"[i] Tìm thấy {videos.Count.ToString(CultureInfo.InvariantCulture)} file MKV.");
        var rows = new List<ExtractRow>();
        for (var i = 0; i < videos.Count; i++)
        {
            var v = videos[i];
            Job.Progress(i + 1, videos.Count, Subprep.Name(v));
            var j = Probe.MkvJson(v, Mkvmerge, Job, JsonCache);
            if (j is null)
            {
                rows.Add(new ExtractRow(v, null, null, "không đọc được file"));
                continue;
            }
            var trk = Probe.PickRefSubTrack(j, c.List("RefSubLangs"), c.Bool("PgsTimingFallback"));
            if (trk is null)
            {
                rows.Add(new ExtractRow(v, null, null, "không có phụ đề văn bản dùng được (có thể chỉ có PGS/VobSub)"));
                continue;
            }
            var final = Py.Join(ex, Subprep.Stem(v) + (trk.IsPgs ? ".srt" : trk.Ext));
            if (c.Bool("SkipExistingRef") && Path.Exists(final))
            {
                rows.Add(new ExtractRow(v, trk, final, "đã có mốc"));
                continue;
            }
            rows.Add(new ExtractRow(v, trk, final));
        }
        return rows;
    }

    /// <summary><c>extract_refs</c>: trích theo kế hoạch vào <c>srt_goc</c> (qua thư mục tạm; ghi nhật ký hoàn tác).</summary>
    public ExtractReport ExtractRefs(IReadOnlyList<ExtractRow> rows)
    {
        var rep = new ExtractReport { Skipped = rows.Count(r => !(r.Ok && r.Selected)) };
        var todo = rows.Where(r => r.Ok && r.Selected).ToList();
        var tmpd = Path.Combine(Temp, "extract");
        Directory.CreateDirectory(tmpd);
        try
        {
            for (var n = 0; n < todo.Count; n++)
            {
                var row = todo[n];
                if (Job.ShouldStop())
                {
                    rep.Stopped = true;
                    break;
                }
                Job.Progress(n + 1, todo.Count, Subprep.Name(row.Video));
                var trk = row.Track!;
                var final = row.Final!;
                var raw = Path.Combine(tmpd, Guid.NewGuid().ToString("N")[..8] + trk.Ext);
                var r = Tools.Run([string.IsNullOrEmpty(Mkvextract) ? "mkvextract" : Mkvextract, "tracks", row.Video,
                                   $"{trk.Id.ToString(CultureInfo.InvariantCulture)}:{raw}"], Job);
                if (!(r.Code <= 1 && File.Exists(raw) && new FileInfo(raw).Length > 32))
                {
                    Job.Error($"  [!] Trích thất bại: {Subprep.Name(row.Video)}");
                    foreach (var ln in Py.SplitLines(r.Output).TakeLast(3))
                        Job.Dim($"      {ln}");
                    row.Status = "trích thất bại";
                    rep.Errors++;
                    continue;
                }
                var src = raw;
                var extra = "";
                if (trk.IsPgs)
                {
                    src = Path.ChangeExtension(raw, ".srt");
                    var cnt = Pgs.PgsToTimingSrt(raw, src, Py.Str(Cfg["PgsTimingText"]));
                    File.Delete(raw);
                    if (cnt <= 0)
                    {
                        Job.Error($"  [!] Không đọc được mốc thời gian từ PGS: {Subprep.Name(row.Video)}");
                        row.Status = "PGS hỏng";
                        rep.Errors++;
                        continue;
                    }
                    extra = $", đọc {cnt.ToString(CultureInfo.InvariantCulture)} mốc thời gian";
                }
                EnsureDir(Subprep.Parent(final));
                if (!ReplaceExisting(final, "thay bằng phụ đề gốc trích lại"))
                {
                    rep.Errors++;
                    continue;
                }
                Tools.CopyFile(src, final);
                Log.Add(Undo.Create, row.Video, final, $"phụ đề gốc: {trk.Describe()}");
                row.Status = "đã trích";
                Job.Ok($"  [+] {Subprep.Name(final)}  ← {trk.Describe()}{extra}");
                rep.Ok++;
            }
        }
        catch (Cancelled)
        {
            rep.Stopped = true;
            Job.Warn("[!] Đã dừng ngay.");
        }
        finally
        {
            rep.LogPath = SaveLog();
        }
        Job.Head($"TRÍCH: {I(rep.Ok)} thành công | {I(rep.Skipped)} bỏ qua | {I(rep.Errors)} lỗi");
        return rep;
    }

    private static string I(int x) => x.ToString(CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ 1. doi ten

    /// <summary>
    /// <c>plan_rename</c>: bảng xem trước đổi tên (<c>Invoke-Rename</c>, phần lập kế hoạch). <paramref name="lang"/>: <c>auto</c> (tự nhận
    /// diện theo thư mục con cấp 1 / hậu tố, mọi ngôn ngữ) hoặc một mã — khi đó CHỈ đổi tên phụ đề của ngôn ngữ đó (bản C#).
    /// </summary>
    public List<RenameRow> PlanRename(string mode = "sub", string? lang = "auto", RefIndex? idx = null)
    {
        lang = NormLang(lang);
        idx ??= BuildIndex(mode);
        var all = ListSubFiles();
        if (all.Count == 0)
        {
            Job.Warn($"[!] Không có phụ đề nào trong '{Cfg.Str("SubFolderName")}'.");
            return [];
        }
        var files = OnlyLang(all, lang, f => Naming.LangTagOfFile(f, SubDir));
        if (files.Count == 0)
            return [];
        if (idx is null)
            return [.. files.Select(f => new RenameRow(f, "", "") { Reason = "không có mốc" })];
        var rows = new List<RenameRow>();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            var detected = Naming.LangTagOfFile(f, SubDir);
            var row = new RenameRow(f, lang != "auto" ? lang : detected, detected);
            rows.Add(row);
            if (lang == "auto" && detected.Length == 0)
            {
                row.Reason = "không đoán được ngôn ngữ";
                row.Details = [$"→ Đặt vào thư mục con '{Cfg.Str("SubFolderName")}\\vi\\' (hoặc cs/en), hoặc thêm hậu tố .vi vào tên file."];
                continue;
            }
            var m = Resolve(idx, f, files.Count);
            row.Ref = m.Ref;
            row.How = m.How;
            if (m.Ref is null)
            {
                row.Reason = m.Reason;
                row.Details = m.Details;
                continue;
            }
            var newName = m.Ref.Canon + "." + row.Lang + Subprep.Suffix(f).ToLowerInvariant();
            var newPath = Py.Join(Subprep.Parent(f), newName);
            row.NewPath = newPath;
            var info = Episodes.ParseEpisode(Subprep.Stem(m.Ref.File), allowGuess: true);
            row.EpLabel = info?.Label ?? "?";
            var k = Py.NormCase(Py.AbsPath(newPath));
            if (Subprep.Name(f) == newName)
                row.Reason = "đã đúng tên";
            else if (seen.TryGetValue(k, out var other))
                row.Reason = $"trùng đích với '{other}'";
            else if (Path.Exists(newPath) && !Subprep.SamePath(newPath, f))
                row.Reason = "đích đã tồn tại";
            else
                seen[k] = Subprep.Name(f);
        }
        var ok = rows.Count(r => r.Ok);
        foreach (var r in rows)
        {
            if (r.Ok)
            {
                Job.Info($"  [{r.EpLabel}] {Subprep.Name(r.Src)}");
                Job.Ok($"        → {(r.NewPath is null ? "" : Subprep.Name(r.NewPath))}");
                if (r.Warn.Length > 0)
                    Job.Error($"        [!] {r.Warn} — kiểm tra kỹ trước khi đổi tên!");
            }
            else if (r.Reason == "đã đúng tên")
            {
                Job.Dim($"  [=] Đã đúng tên: {Subprep.Name(r.Src)}");
            }
            else
            {
                Job.Warn($"  [-] KHÔNG ĐỔI TÊN: {Subprep.Name(r.Src)} — {r.Reason}");
                foreach (var d in r.Details)
                    Job.Dim($"        {d}");
            }
        }
        Job.Head($"KẾ HOẠCH ĐỔI TÊN: {I(ok)} file (bỏ qua {I(rows.Count - ok)})");
        return rows;
    }

    /// <summary><c>execute_rename</c>.</summary>
    public RenameReport ExecuteRename(IReadOnlyList<RenameRow> rows)
    {
        var rep = new RenameReport { Skipped = rows.Count(r => !(r.Ok && r.Selected)) };
        foreach (var r in rows)
        {
            if (!(r.Ok && r.Selected) || r.NewPath is null)
                continue;
            if (Path.Exists(r.NewPath) && !Subprep.SamePath(r.NewPath, r.Src))
            {
                r.Status = "đích đã tồn tại";
                Job.Error($"  [-] File đích đã tồn tại: {Subprep.Name(r.NewPath)}");
                rep.Errors++;
                continue;
            }
            try
            {
                File.Move(r.Src, r.NewPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Add("RenameError", r.Src, r.NewPath, e.Message);
                r.Status = $"lỗi: {e.Message}";
                Job.Error($"  [!] Lỗi: {e.Message}");
                rep.Errors++;
                continue;
            }
            Log.Add(Undo.Rename, r.Src, r.NewPath, r.EpLabel);
            r.Status = "đã đổi tên";
            Job.Ok($"  [OK] {r.EpLabel}: {Subprep.Name(r.NewPath)}");
            rep.Ok++;
        }
        rep.LogPath = SaveLog();
        Job.Head($"ĐỔI TÊN: {I(rep.Ok)} file | {I(rep.Skipped)} bỏ qua | {I(rep.Errors)} lỗi");
        return rep;
    }

    // ------------------------------------------------------------------ 2. dong bo

    /// <summary><c>alass_flags</c>: cờ alass được hỗ trợ (đọc <c>alass --help</c> một lần cho mỗi bộ cờ).</summary>
    public List<string> AlassFlags(IEnumerable<string> wanted)
    {
        var key = wanted.ToList();
        var k = string.Join("\u0001", key);
        if (!_flagCache.TryGetValue(k, out var fl))
        {
            if (AlassCmd is null)
                return key;
            fl = Tools.AlassSupportedFlags(AlassCmd, key, Job);
            _flagCache[k] = fl;
            if (!fl.HelpRead && key.Count > 0)
                Job.Warn("  [!] Không đọc được 'alass --help' → cứ thử dùng đầy đủ tham số.");
            if (fl.Unsupported.Count > 0)
            {
                Job.Warn($"  [!] Bản alass này KHÔNG hỗ trợ: {string.Join(", ", fl.Unsupported)} — lớp chống cắt đoạn "
                         + "bị giảm hiệu lực, hãy tin vào ngưỡng 'dịch tối đa'.");
            }
        }
        return [.. fl.Ok];
    }

    /// <summary><c>_goc_ref</c>: phụ đề gốc của phim trong <c>srt_goc</c> (TĐ-18: dùng để đo lệch FPS khi mốc là audio).</summary>
    private (string File, List<double> Times)? GocRef(string canon)
    {
        if (_goc is null)
        {
            _goc = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
            if (ExDir is { } ex && Directory.Exists(ex))
            {
                foreach (var f in Tools.WalkFiles(ex, ScanSub, Cfg.List("SubExts")))
                {
                    var (@base, tag) = Naming.SplitLangTag(Subprep.Stem(f));
                    var k = @base.ToLowerInvariant();
                    if (!_goc.TryGetValue(k, out var cur) || (tag.Length == 0 && cur.Tag.Length > 0))
                        _goc[k] = (f, tag);
                }
            }
        }
        if (!_goc.TryGetValue(canon.ToLowerInvariant(), out var hit))
            return null;
        var t = Srt.CueTimes(hit.File);
        return t is { Count: > 0 } ? (hit.File, t) : null;
    }

    /// <summary><c>_pick_audio</c>: chọn audio track làm mốc. §6.5: LUÔN trích WAV — số thứ tự/ngôn ngữ không có thì dùng track
    /// Default (không có thì track đầu), không đưa thẳng file phim cho alass.</summary>
    private (int? Index, string Desc) PickAudio(string video, AudioChoice choice)
    {
        var key = Py.NormCase(Py.AbsPath(video));
        if (_audioCache.TryGetValue(key, out var hit))
            return hit;
        var streams = Probe.AudioStreams(Probe.FfprobeStreams(video, Ffprobe, "a", Job, FfprobeCache));
        var (idx, desc, miss) = Subprep.PickAudioStream(streams, choice);
        if (streams.Count == 0)
            Job.Warn($"  [!] Không đọc được audio: {Subprep.Name(video)} → thử track đầu.");
        else if (miss && choice.Mode == "index")
            Job.Warn($"  [!] {Subprep.Name(video)} chỉ có {I(streams.Count)} audio → dùng audio Default.");
        else if (miss)
            Job.Warn($"  [!] {Subprep.Name(video)}: không có audio '{(Py.Truthy(choice.Value) ? Py.Str(choice.Value) : "")}' → dùng audio Default.");
        (int?, string) res = (idx, desc);
        _audioCache[key] = res;
        return res;
    }

    /// <summary>
    /// <c>plan_sync</c>: bảng cặp đồng bộ (<c>Invoke-Sync</c>, phần lập cặp). Không ghi gì vào thư mục phim. <paramref name="lang"/>:
    /// <c>auto</c> = mọi ngôn ngữ; một mã = CHỈ phụ đề của ngôn ngữ đó (bản C#).
    /// </summary>
    public List<SyncPair> PlanSync(string mode = "sub", AudioChoice? audio = null, string? lang = "auto", RefIndex? idx = null)
    {
        var c = Cfg;
        lang = NormLang(lang);
        if (AlassCmd is null)
        {
            Job.Error("[LỖI] Không tìm thấy alass. Chọn đường dẫn alass trong Cài đặt.");
            return [];
        }
        idx ??= BuildIndex(mode);
        var all = ListSubFiles();
        if (all.Count == 0)
        {
            Job.Warn($"[!] Không có phụ đề nào trong '{c.Str("SubFolderName")}'.");
            return [];
        }
        if (idx is null)
            return [];
        // ngôn ngữ của file = hậu tố tên (đã đổi tên) hoặc thư mục con — đúng thứ tự TĐ-32 bên dưới
        var files = OnlyLang(all, lang, f => Naming.SplitLangTag(Subprep.Stem(f)).Lang is { Length: > 0 } t ? t : Naming.LangTagOfFile(f, SubDir));
        if (files.Count == 0)
            return [];
        audio ??= AudioChoice.FromConfig(c);
        var useWav = mode == "audio" && !string.IsNullOrEmpty(Ffmpeg) && !string.IsNullOrEmpty(Ffprobe);
        if (mode == "audio" && !useWav)
        {
            Job.Warn("[!] Thiếu ffmpeg/ffprobe → phải đưa thẳng file phim cho alass. MKV có font đính kèm "
                     + "(thường gặp ở anime) sẽ lỗi 'missing field codec_long_name'.");
        }
        var flags = AlassFlags(c.List(mode == "sub" ? "AlassArgsSubMode" : "AlassArgsAudioMode"));
        var extra = c.List("AlassExtraArgs");
        var outOverride = c.Str("OutFolderName") is { Length: > 0 } of ? Py.Join(Td, of) : null;
        var pairs = new List<SyncPair>();
        var seenOut = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            Job.Progress(i + 1, files.Count, Subprep.Name(f));
            var tag = Naming.SplitLangTag(Subprep.Stem(f)).Lang;
            if (tag.Length == 0) // TĐ-32: tên chưa có hậu tố (chưa đổi tên) -> lấy theo lựa chọn / thư mục con
                tag = lang != "auto" ? lang : Naming.LangTagOfFile(f, SubDir);
            var p = new SyncPair(f, tag, mode);
            pairs.Add(p);
            var m = Resolve(idx, f, files.Count);
            p.Ref = m.Ref;
            p.How = m.How;
            if (m.Ref is null)
            {
                p.Reason = m.Reason;
                p.Details = m.Details;
                continue;
            }
            var @ref = m.Ref;
            var outName = @ref.Canon + (tag.Length > 0 ? "." + tag : "") + Subprep.Suffix(f).ToLowerInvariant();
            var vid = @ref.IsVideo ? @ref.File : idx.VideoByName.GetValueOrDefault(@ref.Canon.ToLowerInvariant());
            p.Video = vid;
            var outDir = outOverride ?? (vid is not null ? Subprep.Parent(vid) : Subprep.Parent(@ref.File));
            p.OutPath = Py.Join(outDir, outName);
            var k = Py.NormCase(Py.AbsPath(p.OutPath));
            if (Subprep.SamePath(p.OutPath, f))
                p.Reason = "nguồn trùng đích";
            else if (c.Bool("SkipExistingOut") && Path.Exists(p.OutPath))
                p.Reason = "đã có kết quả";
            else if (seenOut.TryGetValue(k, out var other))
                p.Reason = $"trùng đích với '{other}'";
            if (p.Reason.Length > 0)
                continue;
            seenOut[k] = Subprep.Name(f);
            // Phân tích quan hệ thời gian (dự báo + phát hiện lệch FPS)
            if (c.Bool("AnalyzeTiming"))
            {
                if (mode == "sub")
                {
                    p.Relation = Timing.TimingRelationOf(@ref.Times(), Srt.CueTimes(f), NRef, NTgt);
                    p.RelationSrc = "phụ đề gốc";
                }
                else if (GocRef(@ref.Canon) is { } goc) // TĐ-18
                {
                    p.Relation = Timing.TimingRelationOf(goc.Times, Srt.CueTimes(f), NRef, NTgt);
                    p.RelationSrc = $"{c.Str("ExtractFolder")}\\{Subprep.Name(goc.File)}";
                }
            }
            p.Flags = [.. flags];
            p.Extra = extra;
            if (p.Relation is { FpsMismatch: true } && c.Bool("FpsAutoAllowGuess"))
            {
                p.Flags.Remove("--disable-fps-guessing");
                p.Notes.Add("Lệch FPS → cho phép alass tự sửa framerate cho file này.");
            }
            if (mode == "audio")
            {
                if (useWav)
                    (p.AudioIndex, p.AudioDesc) = PickAudio(@ref.File, audio);
                p.Attempts = [];
                if (p.AudioIndex is not null)
                {
                    p.Attempts.Add(new Attempt(true, true));
                    if (p.Flags.Count > 0)
                        p.Attempts.Add(new Attempt(true, false, "thử lại WAV không kèm tham số"));
                }
                p.Attempts.Add(new Attempt(false, true, p.Attempts.Count > 0 ? "thử lại với file phim" : ""));
                if (p.Flags.Count > 0)
                    p.Attempts.Add(new Attempt(false, false, "thử lại file phim không kèm tham số"));
            }
            else
            {
                p.Attempts = [new Attempt(false, true)];
                if (p.Flags.Count > 0)
                    p.Attempts.Add(new Attempt(false, false, "thử lại không kèm tham số"));
            }
        }
        var ok = pairs.Count(x => x.Ok);
        foreach (var p in pairs)
        {
            if (!p.Ok)
            {
                Job.Log($"  [-] {Subprep.Name(p.Sub)}: {p.Reason}", p.Reason == "đã có kết quả" ? "dim" : "warn");
                continue;
            }
            Job.Info($"  {Subprep.Name(p.Sub)}");
            Job.Dim($"     mốc ({p.RefKindText}): {Subprep.Name(p.Ref!.File)}" + (p.AudioDesc.Length > 0 ? $" — {p.AudioDesc}" : ""));
            Job.Ok($"     kết quả    : {p.OutPath}");
            foreach (var ln in p.Forecast())
                Job.Log($"     {ln}", ln.StartsWith("LỆCH", StringComparison.Ordinal) ? "error" : "dim");
        }
        Job.Head($"CÁC CẶP SẼ ĐỒNG BỘ: {I(ok)} (bỏ qua {I(pairs.Count - ok)})");
        return pairs;
    }

    /// <summary><c>preview_commands</c>: nút "Xem lệnh" — đúng các lệnh sẽ chạy (đường dẫn tạm ghi rõ), không ghi file nào.</summary>
    public List<string> PreviewCommands(SyncPair p)
    {
        if (!p.Ok || p.Ref is null || p.OutPath is null)
            return [];
        var tmp = Py.Join("<thư mục tạm>", "");
        var wav = tmp + "ref.wav";
        var work = tmp + Subprep.Name(p.Sub);
        var outp = tmp + Subprep.Name(p.OutPath);
        var lines = new List<string>();
        if (p.Mode == "audio" && p.AudioIndex is { } ai && p.Video is not null)
        {
            lines.Add(string.Join(' ', new[] { string.IsNullOrEmpty(Ffmpeg) ? "ffmpeg" : Ffmpeg, "-y", "-v", "error", "-i", p.Video, "-map",
                                               $"0:a:{ai.ToString(CultureInfo.InvariantCulture)}", "-vn", "-ac", "1", "-ar", "16000",
                                               "-c:a", "pcm_s16le", wav }.Select(Subprep.Quote)));
        }
        foreach (var at in p.Attempts)
        {
            var @ref = at.UseWav ? wav : (p.Mode == "audio" && p.Video is not null ? p.Video : p.Ref.PathForTiming);
            List<string> cmd = [.. AlassCmd ?? ["alass"], .. p.AlassArgs(at, @ref, work, outp)];
            lines.Add((at.Note.Length > 0 ? "# " + at.Note + "\n" : "") + string.Join(' ', cmd.Select(Subprep.Quote)));
        }
        lines.Add($"# rồi chép {outp} → {p.OutPath}");
        return lines;
    }

    /// <summary><c>_write_pending_ref</c>: phụ đề gốc đọc từ <c>.sup</c> bạn tự bỏ vào → ghi <c>.srt</c> cạnh nó (lúc thực thi, có nhật ký).</summary>
    private string WritePendingRef(RefItem @ref)
    {
        if (@ref.Pending is not { } pend)
            return @ref.File;
        if (!Path.Exists(pend.Dst) && File.Exists(pend.Tmp))
        {
            Tools.CopyFile(pend.Tmp, pend.Dst);
            Log.Add(Undo.Create, @ref.File, pend.Dst, "mốc thời gian đọc từ PGS");
            Job.Dim($"  [pgs] Đã ghi {Subprep.Name(pend.Dst)}");
        }
        return pend.Dst;
    }

    private string? Wav(SyncPair p, Dictionary<string, string?> cache)
    {
        var key = $"{Py.NormCase(Py.AbsPath(p.Video!))}|{p.AudioIndex!.Value.ToString(CultureInfo.InvariantCulture)}";
        if (cache.TryGetValue(key, out var hit))
            return hit;
        var d = Path.Combine(Temp, "wav");
        Directory.CreateDirectory(d);
        var wav = Path.Combine(d, $"ref_{Guid.NewGuid().ToString("N")[..8]}.wav");
        var r = Tools.Run([string.IsNullOrEmpty(Ffmpeg) ? "ffmpeg" : Ffmpeg, "-y", "-v", "error", "-i", p.Video!, "-map",
                           $"0:a:{p.AudioIndex.Value.ToString(CultureInfo.InvariantCulture)}", "-vn", "-ac", "1", "-ar", "16000",
                           "-c:a", "pcm_s16le", wav], Job);
        if (r.Code != 0 || !File.Exists(wav) || new FileInfo(wav).Length == 0)
        {
            Job.Warn($"  [!] ffmpeg không trích được audio {p.AudioIndex.Value.ToString(CultureInfo.InvariantCulture)} → thử đưa thẳng file phim cho alass.");
            cache[key] = null;
        }
        else
        {
            cache[key] = wav;
        }
        return cache[key];
    }

    /// <summary><c>execute_sync</c>: chạy đồng bộ theo kế hoạch. Kết quả chỉ được chép cạnh file phim khi đã xử lý xong (dừng giữa chừng
    /// không để lại file dở dang).</summary>
    public SyncReport ExecuteSync(IReadOnlyList<SyncPair> pairs)
    {
        var todo = pairs.Where(p => p.Ok && p.Selected).ToList();
        var rep = new SyncReport { Skipped = pairs.Count - todo.Count };
        var wavCache = new Dictionary<string, string?>(StringComparer.Ordinal);
        var startRows = HealthRows.Count;
        try
        {
            for (var n = 0; n < todo.Count; n++)
            {
                var p = todo[n];
                if (Job.ShouldStop())
                {
                    rep.Stopped = true;
                    break;
                }
                Job.Progress(n + 1, todo.Count, Subprep.Name(p.Sub));
                var res = SyncOne(p, n + 1, todo.Count, wavCache);
                p.Result = res;
                if (res.Ok)
                {
                    rep.Ok++;
                    var lv = res.Level ?? 0;
                    rep.Good += lv == 0 ? 1 : 0;
                    rep.Check += lv == 1 ? 1 : 0;
                    rep.Bad += lv == 2 ? 1 : 0;
                }
                else if (res.Rejected)
                {
                    rep.Rejected++;
                }
                else
                {
                    rep.Errors++;
                }
            }
        }
        catch (Cancelled)
        {
            rep.Stopped = true;
            Job.Warn("[!] Đã dừng ngay — file đang làm dở không được ghi.");
        }
        finally
        {
            TryDeleteDir(Path.Combine(Temp, "wav"));
            rep.LogPath = SaveLog();
        }
        Job.Head($"ĐỒNG BỘ: {I(rep.Ok)} thành công | {I(rep.Skipped + rep.Rejected)} bỏ qua | {I(rep.Errors)} lỗi");
        var rows = HealthRows.Skip(startRows).ToList();
        if (rows.Count > 0)
        {
            Job.Head($"KIỂM TRA: {I(rep.Good)} tốt | {I(rep.Check)} cần xem | {I(rep.Bad)} có vấn đề");
            var bad = rows.Where(r => r.Level >= 1).OrderBy(r => -r.Level).ToList();
            if (bad.Count > 0)
            {
                Job.Warn("CÁC FILE CẦN KIỂM TRA TAY:");
                foreach (var b in bad)
                {
                    var lv = Subprep.LevelLog[b.Level];
                    var d = $"      {I(b.Cues)} dòng";
                    if (b.DichDau.Length > 0)
                        d += $" | alass dịch {b.DichDau}s → {b.DichCuoi}s";
                    if (b.ConLech.Length > 0)
                        d += $" | còn lệch {b.ConLech}s ({b.MocKhop} mốc)";
                    if (b.CatDau != 0)
                        d += $" | đã cắt {I(b.CatDau)} dòng đầu";
                    Job.Log($"   {b.File}", lv);
                    Job.Log(d, lv);
                    if (b.Flags.Length > 0)
                        Job.Log($"      {b.Flags}", lv);
                }
            }
            if (Cfg.Bool("HealthReport"))
            {
                rep.HealthPath = SaveHealth(rows);
                Job.Info($"Báo cáo chi tiết: {Cfg.Str("SubPrepLogDir")}\\{Subprep.Name(rep.HealthPath)}");
            }
        }
        if (rep.LogPath is not null)
            Job.Info($"Nhật ký lần chạy (để hoàn tác): {Cfg.Str("SubPrepLogDir")}\\{Subprep.Name(rep.LogPath)}");
        return rep;
    }

    private SyncResult SyncOne(SyncPair p, int n, int total, Dictionary<string, string?> wavCache)
    {
        var c = Cfg;
        var res = new SyncResult();
        var refFile = WritePendingRef(p.Ref!);
        var workDir = Path.Combine(Temp, $"p{I(n)}_{Guid.NewGuid().ToString("N")[..6]}");
        Directory.CreateDirectory(Path.Combine(workDir, "out"));
        var work = Path.Combine(workDir, Subprep.Name(p.Sub)); // TĐ-12: BẢN SAO - file nguồn trong srt_can_sync không bị sửa
        if (c.Bool("NormalizeEncoding"))
        {
            var how = TextEncoding.ToUtf8(p.Sub, work, p.Lang);
            if (how == EncodingKind.Error)
            {
                Job.Warn($"  [!] Không đọc được để chuyển mã: {Subprep.Name(p.Sub)}");
                Tools.CopyFile(p.Sub, work);
            }
            else if (how is not (EncodingKind.Utf8 or EncodingKind.Empty))
            {
                res.Notes.Add($"{how} → UTF-8");
                Job.Dim($"  [enc] {Subprep.Name(p.Sub)}: {how} → UTF-8 (trên bản sao)");
            }
        }
        else
        {
            Tools.CopyFile(p.Sub, work);
        }

        Job.Info($"  [{I(n)}/{I(total)}] Đồng bộ ({p.RefKindText}): {Subprep.Name(p.Sub)}");
        var isSrt = Srt.IsSrtPath(work);
        var blocks0 = isSrt ? Srt.ReadBlocks(work) : null;
        var cuesBefore = blocks0 is { Count: > 0 } ? blocks0.Count : (Srt.CueTimes(work)?.Count ?? 0);
        var refTimes = p.Mode == "sub" ? p.Ref!.Times() : null;
        if (c.Bool("TrimLeadingExtra") && refTimes is { Count: > 0 })
        {
            if (isSrt)
            {
                var (keep, cut) = Subprep.RemoveLeadingExtra(blocks0, refTimes, c.Int("MinVerifyVotes"), NRef, NTgt);
                if (keep is not null && Srt.WriteBlocks(work, keep))
                {
                    res.Cut = cut;
                    cuesBefore -= cut;
                    Job.Warn($"      [cut] Đã cắt {I(cut)} dòng đầu không có trong phim (recap/giới thiệu)");
                }
            }
            else
            {
                res.Notes.Add($"không cắt recap ({Subprep.Suffix(work).TrimStart('.').ToUpperInvariant()})"); // Q7: không đổi định dạng
            }
        }
        var vidDur = p.Video is not null ? Probe.Duration(Probe.MkvJson(p.Video, Mkvmerge, Job, JsonCache)) : 0.0;
        var srcTimes = Srt.CueTimes(work);
        foreach (var ln in p.Notes)
            Job.Warn($"      [i] {ln}");

        var outTmp = Path.Combine(workDir, "out", Subprep.Name(p.OutPath!));
        long code = 1;
        var output = "";
        var tried = 0;
        for (var i = 0; i < p.Attempts.Count; i++)
        {
            var at = p.Attempts[i];
            string refPath;
            if (at.UseWav)
            {
                if (Wav(p, wavCache) is not { } w)
                    continue;
                refPath = w;
            }
            else if (p.Mode == "audio")
            {
                refPath = p.Video ?? p.Ref!.File;
            }
            else
            {
                refPath = p.Ref!.PathForTiming;
            }
            if (tried > 0 && at.Note.Length > 0)
                Job.Warn($"      [!] {at.Note}...");
            tried++;
            File.Delete(outTmp);
            var r = Tools.Run([.. AlassCmd!, .. p.AlassArgs(at, refPath, work, outTmp)], Job);
            code = r.Code;
            output = r.Output;
            if (code == 0)
            {
                res.Attempt = i;
                break;
            }
        }
        res.Code = code;
        var good = code == 0 && File.Exists(outTmp) && new FileInfo(outTmp).Length >= 32;
        if (!good)
        {
            Job.Error($"      [!] THẤT BẠI (mã {code.ToString(CultureInfo.InvariantCulture)})");
            res.OutputTail = [.. Py.SplitLines(output).TakeLast(4)];
            foreach (var ln in res.OutputTail)
                Job.Dim($"         {ln}");
            p.Status = $"thất bại (mã {code.ToString(CultureInfo.InvariantCulture)})";
            return res;
        }

        var newTimes = Srt.CueTimes(outTmp);
        res.Shift = Srt.MaxShift(srcTimes, newTimes);
        var maxShift = c.Double("MaxShiftSeconds");
        if (res.Shift is { } shift && maxShift > 0 && shift > maxShift)
        {
            if (p.Mode == "sub")
            {
                // Mốc là phụ đề -> đáng tin cậy: độ lệch lớn vẫn có thể đúng (bản dựng khác nhau, intro dài ngắn)
                Job.Warn($"      [!] Lưu ý: dịch tới {Subprep.Num(shift, 1)}s - lớn hơn bình thường. "
                         + "Vẫn giữ kết quả vì mốc là phụ đề gốc. Kiểm tra lại khi xem.");
                res.Notes.Add($"dịch {Subprep.Num(shift, 1)}s (lớn)");
            }
            else
            {
                Job.Error($"      [!] TỪ CHỐI: alass dịch {Subprep.Num(shift, 1)}s (ngưỡng {Subprep.Num(maxShift, 1)} s). "
                          + "Mốc là audio nên độ tin cậy thấp. Giữ nguyên file gốc.");
                res.Rejected = true;
                p.Status = $"từ chối (dịch {Subprep.Num(shift, 1)}s)";
                return res;
            }
        }
        // TĐ-20: LUÔN sửa khối 00:00:00 + kiểm tra sức khoẻ (kể cả khi tắt ngưỡng dịch tối đa)
        var maxZero = c.Int("MaxZeroStartCues");
        if (Srt.IsSrtPath(outTmp))
        {
            var (keep, fixedN) = Subprep.RepairCollapsedCues(Srt.ReadBlocks(outTmp), maxZero);
            if (keep is not null && Srt.WriteBlocks(outTmp, keep))
            {
                res.Fixed = fixedN;
                Job.Warn($"      [fix] Đã bỏ {I(fixedN)} dòng bị dồn về 00:00:00");
            }
        }
        else if ((newTimes ?? []).Count(t => t < 0.05) > maxZero)
        {
            res.Notes.Add($"không sửa khối 00:00:00 ({Subprep.Suffix(outTmp).TrimStart('.').ToUpperInvariant()})");
        }
        if (c.Bool("NormalizeEncoding"))
            TextEncoding.ToUtf8(outTmp, outTmp, p.Lang);
        var h = Subprep.SubHealth(outTmp, refTimes, vidDur, cuesBefore, srcTimes, c.Int("MinVerifyVotes"), maxZero, NRef, NTgt);
        res.Health = h;

        // Ghi kết quả cạnh file phim
        var outPath = p.OutPath!;
        EnsureDir(Subprep.Parent(outPath));
        if (Path.Exists(outPath))
        {
            if (c.Bool("SkipExistingOut"))
            {
                Job.Dim($"      [=] Đã có kết quả: {Subprep.Name(outPath)}");
                p.Status = "đã có kết quả";
                res.Rejected = true;
                return res;
            }
            if (!ReplaceExisting(outPath, "thay bằng kết quả đồng bộ mới"))
            {
                p.Status = "không ghi được";
                return res;
            }
        }
        Tools.CopyFile(outTmp, outPath);
        res.Ok = true;
        res.Out = outPath;
        p.Status = h.Text;
        var lvl = Subprep.LevelLog[h.Level];
        Job.Log($"      → {h.Text} : {Subprep.Name(outPath)} ({I(h.Cues)} dòng)", lvl);
        if (h.ShiftFirst is { } sf && h.ShiftLast is { } sl)
        {
            var sp = sl - sf;
            Job.Dim($"         alass dịch: đầu phim {Py.Fixed(sf, 2, plus: true)}s | cuối phim {Py.Fixed(sl, 2, plus: true)}s");
            var spTxt = Math.Abs(sp) <= 0.5 ? "(dịch đều, không co giãn)" : Math.Abs(sp) <= 5 ? "(co giãn nhẹ)" : "(CO GIÃN MẠNH)";
            Job.Log($"         chênh đầu-cuối: {Py.Fixed(sp, 2, plus: true)}s {spTxt}",
                    Math.Abs(sp) <= 0.5 ? "dim" : Math.Abs(sp) <= 5 ? "warn" : "error");
        }
        if (h.ResVotes >= 0 && h.ResOffset is { } ro)
        {
            var q = Math.Abs(ro);
            var qTxt = q <= 0.3 ? "KHỚP" : q <= 1.0 ? "gần khớp" : "CÒN LỆCH";
            var ex = h.ResFps.Length > 0 ? $" | LỆCH FPS: {h.ResFps}" : "";
            Job.Log($"         so với sub gốc: {qTxt} {Subprep.Signed(ro)}s ({I(h.ResVotes)} mốc){ex}", q <= 1.0 ? "ok" : "warn");
        }
        foreach (var fl in h.Flags)
            Job.Log($"         - {fl}", lvl);
        foreach (var nt in res.Notes)
            Job.Dim($"         ({nt})");

        var detail = new List<string>();
        if (h.ShiftFirst is { } a && h.ShiftLast is { } b)
            detail.Add($"đầu={Subprep.Num(a)}s cuối={Subprep.Num(b)}s");
        if (h.ResVotes >= 0 && h.ResOffset is { } ro2)
            detail.Add($"cònlệch={Subprep.Num(ro2)}s mốc={I(h.ResVotes)}");
        if (res.Cut != 0)
            detail.Add($"cắt={I(res.Cut)} dòng");
        if (res.Fixed != 0)
            detail.Add($"bỏ00={I(res.Fixed)} dòng");
        var txt = string.Join(' ', detail);
        if (h.Flags.Count > 0)
            txt += " | " + string.Join("; ", h.Flags);
        if (res.Notes.Count > 0)
            txt += " | " + string.Join("; ", res.Notes);
        Log.Add(Undo.Create, p.Sub, outPath, Py.Strip(txt, " |"));
        HealthRows.Add(new HealthRow(
            Subprep.Name(outPath), h.Level, h.Cues,
            h.ShiftFirst is { } x1 ? Subprep.Num(x1) : "",
            h.ShiftLast is { } x2 ? Subprep.Num(x2) : "",
            h.ResVotes >= 0 && h.ResOffset is { } x3 ? Subprep.Num(x3) : "",
            h.ResVotes >= 0 ? I(h.ResVotes) : "",
            res.Cut, string.Join(" | ", h.Flags)));
        Processed.Add(new Processed(p.Sub, refFile, outPath, p.Lang));
        return res;
    }

    private string SaveHealth(List<HealthRow> rows)
    {
        var d = Py.Join(Td, Cfg.Str("SubPrepLogDir"));
        Directory.CreateDirectory(d);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(d, $"health_{stamp}.csv");
        var k = 2;
        while (Path.Exists(path))
            path = Path.Combine(d, $"health_{stamp}_{I(k++)}.csv");
        var sb = new StringBuilder();
        PyCsv.WriteRowQuoteAll(sb, ["File", "Level", "Cues", "DichDau", "DichCuoi", "ConLech", "MocKhop", "CatDau", "Flags"]);
        foreach (var r in rows)
        {
            PyCsv.WriteRowQuoteAll(sb, [r.File, I(r.Level), I(r.Cues), r.DichDau, r.DichCuoi, r.ConLech, r.MocKhop, I(r.CatDau), r.Flags]);
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return path;
    }

    // ------------------------------------------------------------------ 3. don dep

    /// <summary>
    /// <c>cleanup_targets</c>: file nguồn của các cặp đã đồng bộ xong. <paramref name="lang"/> (bản C#): <c>auto</c> = mọi ngôn ngữ đã
    /// đồng bộ trong phiên; một mã = CHỈ phụ đề nguồn của ngôn ngữ đó (cùng phụ đề gốc chúng đã dùng), để không xoá nhầm ngôn ngữ
    /// chưa dọn.
    /// </summary>
    public CleanupPlan? CleanupTargets(string? lang = "auto")
    {
        lang = NormLang(lang);
        var done = lang == "auto" ? Processed : Processed.Where(p => p.Lang == lang);
        return Subprep.CleanupTargets(done, ExDir, Cfg.Bool("DeleteToRecycleBin"));
    }

    /// <summary><c>execute_cleanup</c>: <paramref name="which"/> = <c>both</c> | <c>subs</c> | <c>refs</c> | <c>none</c>.</summary>
    public CleanupReport ExecuteCleanup(CleanupPlan plan, string which)
    {
        var rep = new CleanupReport();
        var targets = plan.Targets(which);
        foreach (var t in targets)
        {
            if (Undo.Trash(t, plan.ToRecycleBin))
            {
                Log.Add(Undo.Delete, t, "", plan.Where);
                rep.Deleted++;
            }
            else
            {
                rep.Failed.Add(t);
                Job.Error($"  [!] Không xoá được: {Subprep.Name(t)}");
            }
        }
        if (targets.Count > 0)
            Job.Ok($"Đã xoá {I(rep.Deleted)} file ({plan.Where}).");
        foreach (var d in new[] { SubDir, ExDir })
        {
            if (d is not null && Directory.Exists(d))
            {
                var left = Tools.WalkFiles(d, true).Count;
                rep.Left[Subprep.Name(d)] = left;
                Job.Dim(left == 0 ? $"   ('{Subprep.Name(d)}' giờ đã trống)" : $"   ('{Subprep.Name(d)}' còn {I(left)} file chưa xử lý)");
            }
        }
        SaveLog();
        return rep;
    }

    // ------------------------------------------------------------------ dong
    private static void TryDeleteDir(string d)
    {
        try
        {
            if (Directory.Exists(d))
                Directory.Delete(d, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // shutil.rmtree(ignore_errors=True)
        }
    }

    /// <summary><c>close</c>: xoá thư mục tạm.</summary>
    public void Dispose() => TryDeleteDir(Temp);
}
