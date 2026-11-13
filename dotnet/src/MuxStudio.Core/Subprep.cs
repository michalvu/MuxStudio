using System.Globalization;

namespace MuxStudio.Core;

// ==========================================================================
//  Bước ② — Chuẩn bị phụ đề (thay SubPrep_v20.bat). Port core/subprep.py, docs/SPEC.md mục 4.2, §6.5.
//  File này: kiểu dữ liệu + hàm thuần (không chạy tiến trình con). Phiên làm việc: PrepSession.cs.
// ==========================================================================

/// <summary>Sức khoẻ phụ đề sau khi đồng bộ (port <c>subprep.Health</c>). <see cref="Level"/>: 0 TỐT, 1 CẦN XEM, 2 CÓ VẤN ĐỀ.
/// <see cref="Codes"/>: mã dấu hiệu (zero overlap lost overrun weak fps offset stretch badchar few unreadable).</summary>
public sealed class Health(int level, List<string> flags, List<string> codes, int cues, string fmt = "")
{
    public int Level { get; set; } = level;
    public List<string> Flags { get; } = flags;
    public List<string> Codes { get; } = codes;
    public int Cues { get; } = cues;

    /// <summary>alass dịch ở đầu phim (giây).</summary>
    public double? ShiftFirst { get; set; }

    /// <summary>... ở cuối phim.</summary>
    public double? ShiftLast { get; set; }

    /// <summary>Còn lệch so với phụ đề gốc.</summary>
    public double? ResOffset { get; set; }

    /// <summary>Số mốc khớp với phụ đề gốc (-1 = không đo).</summary>
    public int ResVotes { get; set; } = -1;

    /// <summary>Còn lệch FPS (tên tỉ lệ).</summary>
    public string ResFps { get; set; } = "";

    public string Fmt { get; } = fmt;

    public string Text => Subprep.LevelText[Level];

    public double? Spread => ShiftFirst is { } a && ShiftLast is { } b ? b - a : null;
}

/// <summary>Một file mốc: file phim (mốc audio) hoặc phụ đề gốc (port <c>subprep.RefItem</c>).</summary>
public sealed class RefItem(string file, EpisodeInfo? info, bool isVideo, string canon, bool hasTag, List<string> tokens, int? year,
                            string? timingPath = null, (string Tmp, string Dst)? pending = null)
{
    private List<double>? _times;
    private bool _timesRead;

    public string File { get; } = file;

    /// <summary>Cả phép đoán "không chắc" (<c>Confident = false</c>).</summary>
    public EpisodeInfo? Info { get; } = info;

    public bool IsVideo { get; } = isVideo;

    /// <summary>Tên đã bỏ hậu tố ngôn ngữ: '.en'/'.vi'/'' của cùng một phim gom làm một.</summary>
    public string Canon { get; } = canon;

    public bool HasTag { get; } = hasTag;
    public List<string> Tokens { get; } = tokens;
    public int? Year { get; } = year;

    /// <summary>File đọc mốc (khác <see cref="File"/> khi là .sup: bản SRT tạm).</summary>
    public string? TimingPath { get; } = timingPath;

    /// <summary>(.srt tạm, .srt cạnh .sup) — ghi lúc thực thi.</summary>
    public (string Tmp, string Dst)? Pending { get; } = pending;

    public string Key => Info?.Label ?? "?";

    public bool Confident => Info is { Confident: true };

    public string PathForTiming => TimingPath ?? File;

    public List<double>? Times()
    {
        if (!_timesRead)
        {
            _times = IsVideo ? null : Srt.CueTimes(PathForTiming);
            _timesRead = true;
        }
        return _times;
    }
}

/// <summary><c>Get-RefIndex</c>: chỉ mục các file mốc theo tập, theo số tập, theo tên (port <c>subprep.RefIndex</c>).</summary>
public sealed class RefIndex
{
    public RefIndex(string kind, List<RefItem> items, List<RefItem> videos, string root)
    {
        Kind = kind;
        Items = items;
        Videos = videos;
        Root = root;
        foreach (var r in items)
        {
            if (r.Info is not null)
            {
                if (!ByKey.TryGetValue(r.Key, out var lst))
                    ByKey[r.Key] = lst = [];
                lst.Add(r);
            }
        }
        foreach (var lst in ByKey.Values)
        {
            foreach (var r in lst)
            {
                if (!ByEp.TryGetValue(r.Info!.Episode, out var e))
                    ByEp[r.Info.Episode] = e = [];
                e.Add(r);
            }
        }
        foreach (var r in items)
        {
            foreach (var nm in new[] { Subprep.Stem(r.File).ToLowerInvariant(), r.Canon.ToLowerInvariant() })
            {
                if (!ByName.TryGetValue(nm, out var cur) || (!r.HasTag && cur.HasTag))
                    ByName[nm] = r;
            }
        }
        foreach (var v in videos)
            VideoByName.TryAdd(v.Canon.ToLowerInvariant(), v.File);
    }

    /// <summary><c>sub</c> | <c>video</c>.</summary>
    public string Kind { get; }

    public List<RefItem> Items { get; }
    public List<RefItem> Videos { get; }
    public string Root { get; }
    public OrderedDictionary<string, List<RefItem>> ByKey { get; } = new(StringComparer.Ordinal);
    public Dictionary<int, List<RefItem>> ByEp { get; } = [];
    public Dictionary<string, RefItem> ByName { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> VideoByName { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// <c>build</c>: dựng chỉ mục từ danh sách file đã quét. <paramref name="supFn"/><c>(path) → (srt_tạm, đích) | null</c> đọc mốc
    /// từ <c>.sup</c> bạn tự bỏ vào (<c>Convert-LooseSupFiles</c>) — chỉ khi chưa có <c>.srt</c> cùng tên.
    /// </summary>
    public static RefIndex Build(IEnumerable<string> files, string kind, string root, IEnumerable<string> videoExts,
                                 Func<string, (string Tmp, string Dst)?>? supFn = null)
    {
        var vexts = videoExts.Select(e => e.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var items = new List<RefItem>();
        var videos = new List<RefItem>();
        foreach (var f in files)
        {
            var ext = Subprep.Suffix(f).ToLowerInvariant();
            var isVid = vexts.Contains(ext);
            string? timingPath = null;
            (string, string)? pending = null;
            if (ext == ".sup")
            {
                if (kind == "video" || supFn is null || System.IO.File.Exists(Path.ChangeExtension(f, ".srt")))
                    continue;
                var got = supFn(f);
                if (got is null)
                    continue;
                timingPath = got.Value.Tmp;
                pending = got;
            }
            var stem = Subprep.Stem(f);
            var (@base, tag) = Naming.SplitLangTag(stem);
            var canon = isVid ? stem : @base;
            var it = new RefItem(f, Episodes.ParseEpisode(stem, allowGuess: true), isVid, canon, tag.Length > 0,
                                 Naming.TitleTokens(canon), Naming.TitleYear(canon), timingPath, pending);
            if (isVid)
                videos.Add(it);
            if ((kind == "video" && !isVid) || (kind == "sub" && isVid))
                continue;
            items.Add(it);
        }
        return new RefIndex(kind, items, videos, root);
    }

    public string Summary()
    {
        var what = Kind == "sub" ? "phụ đề gốc" : "file phim";
        return $"[i] Mốc: {Items.Count.ToString(CultureInfo.InvariantCulture)} {what} - {ByKey.Count.ToString(CultureInfo.InvariantCulture)} tập.";
    }

    /// <summary><c>source_dirs</c>: mốc lấy từ những thư mục nào (để phát hiện quét nhầm).</summary>
    public List<(string Dir, int Count)> SourceDirs(int top = 4)
    {
        var counts = new OrderedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in Items)
        {
            var d = Path.GetDirectoryName(r.File) ?? "";
            if (Tools.IsUnder(d, Root))
            {
                d = Path.GetRelativePath(Root, d);
                d = d == "." ? "" : d;
            }
            var k = d.Length > 0 ? d : "(thư mục gốc)";
            counts[k] = counts.GetValueOrDefault(k) + 1;
        }
        return [.. counts.Select((kv, i) => (kv.Key, kv.Value, i)).OrderBy(x => -x.Value).ThenBy(x => x.i).Take(top).Select(x => (x.Key, x.Value))];
    }

    public string SourceText() =>
        string.Join(", ", SourceDirs().Select(x => $"{x.Dir} ({x.Count.ToString(CultureInfo.InvariantCulture)})"));

    public List<string> Canons() => [.. Items.Select(r => r.Canon).Distinct(StringComparer.Ordinal)];
}

/// <summary>Kết quả ghép phụ đề ↔ mốc. <see cref="Reason"/>: lý do KHÔNG ghép được (cột "Lý do").</summary>
public sealed record RefMatch(RefItem? Ref, string How = "", string Reason = "", List<string>? Details = null)
{
    public List<string> Details { get; init; } = Details ?? [];
}

/// <summary>Một file MKV cần trích phụ đề gốc (<c>Invoke-ExtractRefSubs</c>). <see cref="Reason"/> rỗng = sẽ trích.</summary>
public sealed class ExtractRow(string video, RefTrack? track, string? final, string reason = "")
{
    public string Video { get; } = video;
    public RefTrack? Track { get; } = track;
    public string? Final { get; } = final;
    public string Reason { get; set; } = reason;
    public bool Selected { get; set; } = true;
    public string Status { get; set; } = "";
    public bool Ok => Reason.Length == 0;
}

/// <summary>Một dòng của bảng xem trước đổi tên (<c>Invoke-Rename</c>).</summary>
public sealed class RenameRow(string src, string lang, string detected)
{
    public string Src { get; } = src;

    /// <summary>Mã ngôn ngữ sẽ gắn ('' = không rõ).</summary>
    public string Lang { get; } = lang;

    /// <summary>Mã đoán từ thư mục con / hậu tố.</summary>
    public string Detected { get; } = detected;

    public string? NewPath { get; set; }
    public RefItem? Ref { get; set; }
    public string How { get; set; } = "";

    /// <summary>'' = sẽ đổi tên.</summary>
    public string Reason { get; set; } = "";

    public List<string> Details { get; set; } = [];

    /// <summary>Cảnh báo ĐỎ (bản Python: file nằm trong thư mục ngôn ngữ khác lựa chọn — bản C# không còn trường hợp này).</summary>
    public string Warn { get; set; } = "";

    public string EpLabel { get; set; } = "?";
    public bool Selected { get; set; } = true;
    public string Status { get; set; } = "";
    public bool Ok => Reason.Length == 0;
}

/// <summary>Audio track làm mốc (port <c>subprep.AudioChoice</c>): <c>auto</c> (track Default, không có thì track đầu),
/// <c>index</c> (số thứ tự từ 0), <c>lang</c> (theo ngôn ngữ).</summary>
public sealed record AudioChoice(string Mode = "auto", object? Value = null)
{
    public static AudioChoice FromConfig(Config cfg)
    {
        var m = cfg.Str("SyncAudioMode");
        if (m == "index")
            return new AudioChoice("index", Py.ToInt(cfg["SyncAudioIndex"]));
        if (m == "lang")
            return new AudioChoice("lang", Py.Str(cfg["SyncAudioLang"]));
        return new AudioChoice("auto"); // 'ask' = GUI hiện bảng để chọn; lỗi không có lựa chọn -> tự động
    }

    /// <summary><c>describe</c> (bản Python thụt lề nhầm vào trong <c>audio_layout_groups</c> nên không gọi được).</summary>
    public string Describe() => Mode switch
    {
        "index" => $"theo số thứ tự = {Py.Str(Value)}",
        "lang" => $"theo ngôn ngữ = {Py.Str(Value)}",
        _ => "tự động (audio Default của file)",
    };
}

/// <summary>Audio track của một file phim (bảng audio ở thẻ ② — TĐ-35: liệt kê MỌI file, không chỉ file mẫu).
/// <see cref="EpLabel"/>: 'S01E05' / 'E643' / '' (phim lẻ).</summary>
public sealed record VideoAudio(string Video, string EpLabel, List<AudioStream> Streams)
{
    /// <summary>Bố cục audio để so giữa các file: (ngôn ngữ, codec, số kênh) theo thứ tự track.</summary>
    public List<(string Lang, string Codec, int Channels)> Layout =>
        [.. Streams.Select(a => (a.Lang.ToLowerInvariant(), a.Codec.ToLowerInvariant(), a.Channels))];

    internal string LayoutKey => string.Join("\u0002", Layout.Select(x => $"{x.Lang}\u0001{x.Codec}\u0001{x.Channels.ToString(CultureInfo.InvariantCulture)}"));
}

/// <summary>Một lần thử alass. <see cref="UseWav"/>: mốc = WAV trích ra (true) hay file phim / phụ đề gốc (false).</summary>
public sealed record Attempt(bool UseWav, bool WithFlags, string Note = "");

/// <summary>Kết quả đồng bộ một cặp (port <c>subprep.SyncResult</c>). <see cref="Attempt"/>: lần thử nào thành công (đếm từ 0).</summary>
public sealed class SyncResult
{
    public bool Ok { get; set; }
    public long Code { get; set; } = 1;
    public Health? Health { get; set; }
    public int Cut { get; set; }
    public int Fixed { get; set; }
    public double? Shift { get; set; }
    public bool Rejected { get; set; }
    public string? Out { get; set; }
    public int Attempt { get; set; } = -1;
    public List<string> Notes { get; } = [];
    public List<string> OutputTail { get; set; } = [];
    public int? Level => Health?.Level;
}

/// <summary>Một dòng của bảng cặp đồng bộ (<c>Invoke-Sync</c>). <see cref="Mode"/>: <c>sub</c> (mốc = phụ đề gốc) | <c>audio</c>
/// (mốc = audio của file phim). <see cref="Reason"/> rỗng = sẽ đồng bộ.</summary>
public sealed class SyncPair(string sub, string lang, string mode)
{
    public string Sub { get; } = sub;
    public string Lang { get; } = lang;
    public string Mode { get; } = mode;
    public RefItem? Ref { get; set; }
    public string How { get; set; } = "";
    public string Reason { get; set; } = "";
    public List<string> Details { get; set; } = [];
    public string? OutPath { get; set; }

    /// <summary>File phim (thời lượng; mốc audio).</summary>
    public string? Video { get; set; }

    /// <summary>Dự báo trước khi đồng bộ.</summary>
    public TimingRelation? Relation { get; set; }

    public string RelationSrc { get; set; } = "";

    /// <summary><c>0:a:N</c> để trích WAV (<c>null</c> = không trích được WAV).</summary>
    public int? AudioIndex { get; set; }

    public string AudioDesc { get; set; } = "";
    public List<string> Flags { get; set; } = [];
    public List<string> Extra { get; set; } = [];
    public List<Attempt> Attempts { get; set; } = [];
    public List<string> Notes { get; } = [];
    public bool Selected { get; set; } = true;
    public string Status { get; set; } = "";
    public SyncResult? Result { get; set; }
    public bool Ok => Reason.Length == 0;

    public string RefKindText => Mode == "sub"
        ? "phụ đề gốc"
        : AudioIndex is { } i ? $"audio #{i.ToString(CultureInfo.InvariantCulture)}" : "video";

    /// <summary>Dòng "dự báo" của bảng cặp (độ lệch, hệ số, cảnh báo lệch FPS).</summary>
    public List<string> Forecast()
    {
        if (Relation is not { } r)
            return [];
        var outp = new List<string>
        {
            $"dự báo: lệch {Subprep.Signed(r.Offset)}s, hệ số {Subprep.Num(r.Scale, 4)} ({r.Votes.ToString(CultureInfo.InvariantCulture)} mốc)",
        };
        if (r.FpsMismatch)
            outp.Add($"LỆCH FPS: {r.Name} — nếu chỉ dịch thời gian, cuối phim sẽ sai khoảng {Subprep.Num(r.Drift, 1)}s.");
        return outp;
    }

    public List<string> AlassArgs(Attempt at, string refPath, string subPath, string outPath)
    {
        var a = new List<string>();
        if (at.WithFlags)
            a.AddRange(Flags);
        a.AddRange(Extra);
        return [.. a, refPath, subPath, outPath];
    }
}

/// <summary>Cặp đã đồng bộ xong (để dọn dẹp). <see cref="Lang"/>: ngôn ngữ của phụ đề (bản C# thêm — dọn đúng ngôn ngữ đã chọn).</summary>
public sealed record Processed(string Sub, string Ref, string Out, string Lang = "");

/// <summary>§5.2 mục 1: số phim, số phụ đề trong <c>srt_can_sync</c>, số phụ đề gốc trong <c>srt_goc</c>.</summary>
public sealed record PrepCounts(int Videos, int Subs, int Refs);

public sealed class ExtractReport
{
    public int Ok { get; set; }
    public int Skipped { get; set; }
    public int Errors { get; set; }
    public bool Stopped { get; set; }
    public string? LogPath { get; set; }
}

public sealed class RenameReport
{
    public int Ok { get; set; }
    public int Skipped { get; set; }
    public int Errors { get; set; }
    public string? LogPath { get; set; }
}

public sealed class SyncReport
{
    public int Ok { get; set; }
    public int Skipped { get; set; }
    public int Rejected { get; set; }
    public int Errors { get; set; }
    public int Good { get; set; }
    public int Check { get; set; }
    public int Bad { get; set; }
    public bool Stopped { get; set; }
    public string? LogPath { get; set; }
    public string? HealthPath { get; set; }
}

/// <summary>Một dòng báo cáo sức khoẻ (<c>health_*.csv</c>, đúng thứ tự cột của bản Python).</summary>
public sealed record HealthRow(string File, int Level, int Cues, string DichDau, string DichCuoi, string ConLech, string MocKhop, int CatDau,
                               string Flags);

/// <summary>Hộp thoại dọn dẹp (<c>Invoke-Cleanup</c>): chỉ file nguồn của cặp đã có kết quả &gt; 32 byte cạnh phim.</summary>
public sealed record CleanupPlan(int Done, List<string> SubFiles, List<string> RefFiles, bool ToRecycleBin)
{
    public string Where => ToRecycleBin ? "Thùng rác (khôi phục được)" : "XOÁ HẲN (không khôi phục được)";

    /// <summary><paramref name="which"/>: <c>both</c> (cả hai) | <c>subs</c> (chỉ srt_can_sync) | <c>refs</c> (chỉ srt_goc) | <c>none</c>.</summary>
    public List<string> Targets(string which)
    {
        var outp = new List<string>();
        if (which is "both" or "subs")
            outp.AddRange(SubFiles);
        if (which is "both" or "refs")
            outp.AddRange(RefFiles);
        return outp;
    }
}

public sealed class CleanupReport
{
    public int Deleted { get; set; }
    public List<string> Failed { get; } = [];

    /// <summary>Tên thư mục → số file còn lại.</summary>
    public OrderedDictionary<string, int> Left { get; } = new(StringComparer.Ordinal);
}

/// <summary>Hàm thuần của bước ② (port phần cấp module của <c>core/subprep.py</c>).</summary>
public static class Subprep
{
    public static readonly IReadOnlyDictionary<int, string> LevelText = new Dictionary<int, string>
    {
        [0] = "TỐT", [1] = "CẦN XEM", [2] = "CÓ VẤN ĐỀ",
    };

    public static readonly IReadOnlyDictionary<int, string> LevelLog = new Dictionary<int, string>
    {
        [0] = "ok", [1] = "warn", [2] = "error",
    };

    /// <summary><c>_num</c>: số như PowerShell in ra (<c>[math]::Round(x, d)</c>): dấu chấm, bỏ số 0 thừa.</summary>
    public static string Num(double x, int d = 2) => Timing.FmtInv(Py.Round(x, d), d);

    /// <summary><c>_signed</c>.</summary>
    public static string Signed(double x, int d = 2)
    {
        var s = Num(x, d);
        return s.StartsWith('-') ? s : "+" + s;
    }

    internal static bool SamePath(string a, string b) =>
        string.Equals(Py.NormCase(Py.AbsPath(a)), Py.NormCase(Py.AbsPath(b)), StringComparison.Ordinal);

    /// <summary><c>_quote</c>: đặt trong ngoặc kép khi rỗng hoặc có ký tự đặc biệt của cmd.</summary>
    internal static string Quote(string a) =>
        a.Length == 0 || a.AsSpan().IndexOfAny(" \t\"&()[]{}^=;!'+,`~") >= 0 ? $"\"{a}\"" : a;

    // ---- đường dẫn kiểu pathlib ----
    internal static string Name(string p) => Py.Basename(p);

    internal static string Suffix(string p) => Py.SplitExt(Name(p)).Ext;

    internal static string Stem(string p) => Py.SplitExt(Name(p)).Root;

    internal static string Parent(string p) => Path.GetDirectoryName(p) ?? "";

    // ==========================================================================
    //  CẮT RECAP, SỬA KHỐI 00:00:00 (thao tác trên khối SRT — không ghi file)
    // ==========================================================================

    /// <summary>
    /// <c>remove_leading_extra</c> (<c>Remove-LeadingExtra</c>): cắt khối "tóm tắt tập trước" ở đầu file mà phim không có.
    /// Cần ≥ 40 dòng. Đo quan hệ thời gian trên 70% CUỐI (≥ <paramref name="minVotes"/> mốc khớp); tìm dòng đầu tiên mà 5 dòng liên
    /// tiếp đều có mốc gốc trong ~±1,5 s (bảng băm ô 1 giây); chỉ tìm trong 30% đầu; huỷ nếu phần giữ lại &lt; 60%.
    /// Trả (khối giữ lại, số dòng cắt) hoặc (<c>null</c>, 0) nếu không cắt.
    /// </summary>
    public static (List<SrtBlock>? Keep, int Cut) RemoveLeadingExtra(IReadOnlyList<SrtBlock>? blocks, IReadOnlyList<double>? refTimes,
                                                                    int minVotes = 20, int nRef = 400, int nTgt = 100)
    {
        if (refTimes is null || refTimes.Count == 0 || blocks is null || blocks.Count < 40)
            return (null, 0);
        var n = blocks.Count;
        var skip = (int)Math.Round(n * 0.30); // [int] của PowerShell: làm tròn về số chẵn
        var tail = blocks.Skip(skip).Select(b => b.Start).ToList();
        var rel = Timing.TimingRelationOf(refTimes, tail, nRef, nTgt);
        if (rel is null || rel.Votes < minVotes)
            return (null, 0);
        var bins = new RefBins(refTimes);
        var firstGood = -1;
        var limit = Math.Min(n - 5, (int)Math.Round(n * 0.30));
        for (var i = 0; i <= limit; i++)
        {
            var run = 0;
            for (var j = i; j < Math.Min(i + 5, n); j++)
            {
                if (bins.Near(blocks[j].Start * rel.Scale + rel.Offset))
                    run++;
                else
                    break;
            }
            if (run >= 5)
            {
                firstGood = i;
                break;
            }
        }
        if (firstGood <= 0)
            return (null, 0);
        var keep = blocks.Skip(firstGood).ToList();
        if (keep.Count < n * 0.6) // cắt quá nhiều -> nghi ngờ
            return (null, 0);
        return (keep, firstGood);
    }

    /// <summary><c>repair_collapsed_cues</c> (<c>Repair-CollapsedCues</c>): &gt; <paramref name="maxZero"/> dòng bắt đầu &lt; 0,05 s
    /// (alass đẩy khối về trước mốc 0) → bỏ các dòng đó, nếu còn ≥ 10 dòng. Trả (khối giữ lại, số dòng bỏ) hoặc (<c>null</c>, 0).</summary>
    public static (List<SrtBlock>? Keep, int Fixed) RepairCollapsedCues(IReadOnlyList<SrtBlock>? blocks, int maxZero = 2)
    {
        if (blocks is null || blocks.Count == 0)
            return (null, 0);
        var zero = blocks.Count(b => b.Start < 0.05);
        if (zero <= maxZero)
            return (null, 0);
        var keep = blocks.Where(b => b.Start >= 0.05).ToList();
        if (keep.Count < 10)
            return (null, 0);
        return (keep, zero);
    }

    // ==========================================================================
    //  SỨC KHOẺ PHỤ ĐỀ SAU KHI ĐỒNG BỘ (Test-SubHealth)
    // ==========================================================================

    /// <summary>
    /// <c>sub_health</c> (<c>Test-SubHealth</c>): 7 dấu hiệu, mức 0/1/2.
    /// Khác .bat (TĐ-14): đọc mốc của SRT, ASS/SSA, VTT (bản cũ chỉ đọc SRT nên mọi kết quả ASS bị báo "CÓ VẤN ĐỀ: không đọc được
    /// nội dung"). Với ASS bỏ dấu hiệu "chồng lấn" vì ASS hiện nhiều dòng cùng lúc là bình thường.
    /// </summary>
    public static Health SubHealth(string path, IReadOnlyList<double>? refTimes, double videoDur, int cuesBefore,
                                   IReadOnlyList<double>? srcTimes, int minVotes = 20, int maxZero = 2, int nRef = 400, int nTgt = 100)
    {
        var sp = Srt.CueSpans(path);
        if (sp is null || sp.Value.Spans.Count == 0)
            return new Health(2, ["không đọc được nội dung"], ["unreadable"], 0);
        var (fmt, spans) = sp.Value;
        if (fmt == "ass")
            spans = [.. spans.OrderBy(s => s.Start).ThenBy(s => s.End)];
        var n = spans.Count;
        var outTimes = spans.Select(s => s.Start).ToList(); // SRT: thứ tự trong file (như .bat)
        var h = new Health(0, [], [], n, fmt);

        void Add(string code, string msg, int lv)
        {
            h.Codes.Add(code);
            h.Flags.Add(msg);
            h.Level = Math.Max(h.Level, lv);
        }

        if (srcTimes is { Count: > 1 } && n > 1)
        {
            var m = Math.Min(srcTimes.Count, n);
            h.ShiftFirst = outTimes[0] - srcTimes[0];
            h.ShiftLast = outTimes[m - 1] - srcTimes[m - 1];
        }
        static string I(int x) => x.ToString(CultureInfo.InvariantCulture);
        // 1. Dòng bị dồn về 00:00:00
        var zero = outTimes.Count(s => s < 0.05);
        if (zero > maxZero)
            Add("zero", $"{I(zero)} dòng dính ở 00:00:00", 2);
        // 2. Chồng lấn
        if (fmt != "ass")
        {
            var ov = 0;
            for (var i = 1; i < n; i++)
            {
                if (spans[i].Start < spans[i - 1].End - 0.05)
                    ov++;
            }
            if (ov > Math.Max(5, n * 0.05))
                Add("overlap", $"{I(ov)} chỗ chồng lấn", 1);
        }
        // 3. Mất dòng so với trước khi sync
        if (cuesBefore > 0 && n < cuesBefore * 0.95)
            Add("lost", $"mất {I(cuesBefore - n)}/{I(cuesBefore)} dòng", 1);
        // 4. Vượt thời lượng phim
        var lastEnd = fmt == "srt" ? spans[n - 1].End : spans.Max(s => s.End);
        if (videoDur > 0 && n > 0 && lastEnd > videoDur + 60)
            Add("overrun", $"dòng cuối {Num(lastEnd / 60, 1)}p > phim {Num(videoDur / 60, 1)}p", 2);
        // 5. Đối chiếu với mốc gốc
        if (refTimes is { Count: > 0 })
        {
            var rel = Timing.TimingRelationOf(refTimes, outTimes, nRef, nTgt);
            if (rel is not null)
            {
                h.ResOffset = rel.Offset;
                h.ResVotes = rel.Votes;
                if (rel.FpsMismatch)
                    h.ResFps = rel.Name;
                if (rel.Votes < minVotes)
                    Add("weak", $"chỉ {I(rel.Votes)} mốc khớp", 2);
                else if (rel.FpsMismatch)
                    Add("fps", $"còn lệch FPS ({rel.Name})", 1);
                else if (Math.Abs(rel.Offset) > 1.0)
                    Add("offset", $"vẫn lệch {Num(rel.Offset)}s", 1);
            }
        }
        // 6. Co giãn bất thường
        if (h.Spread is { } spread && Math.Abs(spread) > 5.0)
            Add("stretch", $"co giãn {Num(spread, 1)}s giữa đầu và cuối", 1);
        // 7. Hỏng mã hoá / quá ít dòng
        try
        {
            if (TextEncoding.ReadText(path).Contains('�', StringComparison.Ordinal))
                Add("badchar", "ký tự hỏng (sai bảng mã)", 1);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        if (n < 50)
            Add("few", $"chỉ có {I(n)} dòng", 1);
        return h;
    }

    // ==========================================================================
    //  GHÉP PHỤ ĐỀ VỚI MỐC (Select-RefVariant, Resolve-Ref)
    // ==========================================================================

    /// <summary><c>select_ref_variant</c> (<c>Select-RefVariant</c>): nhiều file cùng một phim → video &gt; phụ đề không hậu tố &gt; còn lại.</summary>
    public static RefItem? SelectRefVariant(IReadOnlyList<RefItem> cands)
    {
        if (cands.Count == 0)
            return null;
        if (cands.Count == 1)
            return cands[0];
        return cands.FirstOrDefault(r => r.IsVideo) ?? cands.FirstOrDefault(r => !r.HasTag) ?? cands[0];
    }

    /// <summary><c>_infos_conflict</c>: như <see cref="Episodes.EpisodeConflict"/> nhưng trên kết quả đã nhận diện (chỉ xét khi cả hai CHẮC CHẮN).</summary>
    private static bool InfosConflict(EpisodeInfo? a, EpisodeInfo? b)
    {
        if (a is null || b is null || !a.Confident || !b.Confident)
            return false;
        if (a.HasSeason && b.HasSeason)
            return a.Season != b.Season || a.Episode != b.Episode;
        return a.Episode != b.Episode;
    }

    /// <summary>
    /// <c>resolve_ref</c> (<c>Resolve-Ref</c>): tìm file mốc cho một phụ đề — đúng 4 tầng của .bat, trả kèm LÝ DO khi không ghép.
    /// <list type="number">
    /// <item>Trùng tên tuyệt đối (sau khi đã đổi tên).</item>
    /// <item>Theo TÊN PHIM: điểm từ khoá, lọc năm; ≥ <paramref name="minScore"/>, một phim duy nhất, hơn hạng nhì ≥ <paramref name="minMargin"/>.</item>
    /// <item>Theo season + số tập — chỉ khi số tập là thật (không phải phép đoán).</item>
    /// <item>Cả hai bên chỉ có đúng một file → ghép thẳng.</item>
    /// </list>
    /// Sửa lỗi .bat (TĐ-31): mốc ghi số tập KHÁC số tập của phụ đề bị loại trước tầng 2 và tầng 4; tầng 3 báo "nhập nhằng" thật khi
    /// nhiều phim khác nhau cùng khớp.
    /// </summary>
    public static RefMatch ResolveRef(RefIndex idx, string subBaseName, int subFileCount, double minScore = 0.5, double minMargin = 0.15)
    {
        var (@base, _) = Naming.SplitLangTag(subBaseName);
        // 1. Khớp tuyệt đối theo tên
        if (idx.ByName.TryGetValue(@base.ToLowerInvariant(), out var hit))
            return new RefMatch(hit, "trùng tên");

        var myInfo = Episodes.ParseEpisode(@base);
        var pool = idx.Items.Where(r => !InfosConflict(myInfo, r.Info)).ToList();
        var nConflict = idx.Items.Count - pool.Count;

        // 2. Theo TÊN PHIM (dành cho phim lẻ)
        var myTok = Naming.TitleTokens(@base);
        var myYear = Naming.TitleYear(@base);
        if (myTok.Count > 0)
        {
            var scored = new List<(RefItem R, double S)>();
            foreach (var r in pool)
            {
                if (myYear is { } my && my != 0 && r.Year is { } ry && ry != 0 && my != ry)
                    continue; // năm khác nhau rõ ràng -> chắc chắn không phải cùng phim
                var sc = Naming.TitleScore(myTok, r.Tokens);
                if (sc > 0)
                    scored.Add((r, sc));
            }
            if (scored.Count > 0)
            {
                scored = [.. scored.OrderBy(x => -x.S)];
                var best = scored[0].S;
                var top = scored.Where(x => x.S == best).Select(x => x.R).ToList();
                var topCanon = top.Select(r => r.Canon).Distinct(StringComparer.Ordinal).ToList();
                var lower = scored.Where(x => x.S < best).Select(x => x.S).ToList();
                var second = lower.Count > 0 ? lower[0] : 0.0;
                if (best >= minScore && topCanon.Count == 1 && best - second >= minMargin)
                    return new RefMatch(SelectRefVariant(top), $"theo tên phim (điểm {Num(best)})");
                if (best >= minScore && topCanon.Count > 1)
                    return new RefMatch(null, Reason: $"nhập nhằng theo tên phim ({topCanon.Count.ToString(CultureInfo.InvariantCulture)} mốc cùng điểm {Num(best)})",
                                        Details: [.. topCanon.Take(4)]);
            }
        }

        // 3. Theo season + số tập - CHỈ khi số tập là thật
        var info = Episodes.ParseEpisode(@base, allowGuess: true);
        if (info is null)
            return new RefMatch(null, Reason: "không nhận diện được số tập");
        if (!info.Confident)
        {
            var det = new List<string>
            {
                "Từ khoá đọc ra: [" + string.Join(", ", myTok) + "]" + (myYear is { } y && y != 0 ? $" năm {y.ToString(CultureInfo.InvariantCulture)}" : ""),
            };
            var near = idx.Items.Select(r => (S: Naming.TitleScore(myTok, r.Tokens), R: r)).OrderBy(x => -x.S).Take(3);
            foreach (var (s, r) in near)
                det.Add($"gần nhất: {Num(s)}  {r.Canon}" + (r.Year is { } ry && ry != 0 ? $" ({ry.ToString(CultureInfo.InvariantCulture)})" : ""));
            det.Add($"→ Cần điểm ≥ {Num(minScore)}. Đổi tên phụ đề cho giống tên phim hơn.");
            return new RefMatch(null, Reason: "không tìm được phim tương ứng", Details: det);
        }
        List<RefItem> cands;
        if (info.HasSeason)
        {
            cands = idx.ByKey.TryGetValue(info.Label, out var k) ? [.. k] : [];
            if (cands.Count == 0)
                cands = [.. idx.ByEp.GetValueOrDefault(info.Episode, []).Where(r => !(r.Info is { HasSeason: true }))];
        }
        else
        {
            cands = [.. idx.ByEp.GetValueOrDefault(info.Episode, [])];
        }
        cands = [.. cands.Where(r => r.Confident)];

        if (cands.Count == 0)
        {
            // 4. Chỉ ghép thẳng khi CẢ HAI bên đều chỉ có đúng một file -> không thể nhầm
            var canons = idx.Canons();
            if (canons.Count == 1 && subFileCount == 1 && pool.Count > 0)
                return new RefMatch(SelectRefVariant(idx.Items), "ghép thẳng (chỉ có 1 phụ đề và 1 mốc)");
            var det = new List<string>
            {
                $"đọc ra: {info.Label}, tên phim: '{Naming.TitleKey(@base)}'",
                "Các mốc đang có: " + string.Join(" | ", canons.Take(4)),
            };
            if (nConflict > 0)
                det.Add($"(đã loại {nConflict.ToString(CultureInfo.InvariantCulture)} mốc ghi số tập khác)");
            return new RefMatch(null, Reason: "không tìm được mốc", Details: det);
        }

        var distinct = cands.Select(r => r.Canon).Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count > 1)
            return new RefMatch(null, Reason: $"nhập nhằng tập {info.Label} ({distinct.Count.ToString(CultureInfo.InvariantCulture)} file mốc khớp)",
                                Details: [.. distinct, "→ Đặt tên phụ đề kèm season (vd S02E05) rồi chạy lại."]);
        return new RefMatch(SelectRefVariant(cands), $"theo tập {info.Label}");
    }

    // ==========================================================================
    //  AUDIO LÀM MỐC
    // ==========================================================================

    /// <summary>
    /// <c>pick_audio_stream</c>: audio track làm mốc của MỘT file theo lựa chọn → (số thứ tự, mô tả, không_có). <c>Missing</c> = số thứ
    /// tự / ngôn ngữ đã chọn không có trong file này nên phải dùng track Default (không có thì track đầu).
    /// </summary>
    public static (int Index, string Desc, bool Missing) PickAudioStream(IReadOnlyList<AudioStream> streams, AudioChoice choice)
    {
        (int, string) Auto(string prefix = "")
        {
            foreach (var st in streams)
            {
                if (st.Default)
                    return (st.Index, prefix + st.Describe());
            }
            return (0, prefix + (streams.Count > 0 ? streams[0].Describe() : "track đầu"));
        }

        if (streams.Count == 0)
            return (0, "không đọc được danh sách audio → thử track đầu", false);
        if (choice.Mode == "index")
        {
            var i = Py.ToInt(Py.Truthy(choice.Value) ? choice.Value : 0);
            if (i >= 0 && i < streams.Count)
                return (i, streams[i].Describe(), false);
            var (a, d) = Auto($"không có #{i.ToString(CultureInfo.InvariantCulture)} → ");
            return (a, d, true);
        }
        if (choice.Mode == "lang")
        {
            var want = Py.Truthy(choice.Value) ? Py.Str(choice.Value) : "";
            var hit = streams.FirstOrDefault(st => Naming.LangMatch(st.Lang, want));
            if (hit is not null)
                return (hit.Index, hit.Describe(), false);
            var (a, d) = Auto($"không có '{want}' → ");
            return (a, d, true);
        }
        var (a0, d0) = Auto();
        return (a0, d0, false);
    }

    /// <summary><c>audio_layout_groups</c>: gom các file theo bố cục audio, nhóm nhiều file nhất đứng trước (bằng nhau: nhóm gặp trước).</summary>
    public static List<List<VideoAudio>> AudioLayoutGroups(IEnumerable<VideoAudio> files)
    {
        var groups = new OrderedDictionary<string, List<VideoAudio>>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            if (!groups.TryGetValue(f.LayoutKey, out var g))
                groups[f.LayoutKey] = g = [];
            g.Add(f);
        }
        return [.. groups.Values.OrderBy(g => -g.Count)];
    }

    // ==========================================================================
    //  DỌN DẸP
    // ==========================================================================

    /// <summary><c>cleanup_targets</c>: file nguồn (phụ đề trong srt_can_sync, phụ đề gốc trong srt_goc) của các cặp đã có kết quả
    /// &gt; 32 byte. Không có gì → <c>null</c>.</summary>
    public static CleanupPlan? CleanupTargets(IEnumerable<Processed> processed, string? exDir, bool toRecycleBin = true)
    {
        var safe = processed.Where(p => System.IO.File.Exists(p.Out) && new FileInfo(p.Out).Length > 32).ToList();
        if (safe.Count == 0)
            return null;
        var subs = safe.Select(p => p.Sub).Distinct(StringComparer.Ordinal).Where(System.IO.File.Exists).ToList();
        var refs = safe.Select(p => p.Ref).Distinct(StringComparer.Ordinal)
                       .Where(x => System.IO.File.Exists(x) && exDir is not null && Tools.IsUnder(x, exDir)).ToList();
        if (subs.Count == 0 && refs.Count == 0)
            return null;
        return new CleanupPlan(safe.Count, subs, refs, toRecycleBin);
    }
}
