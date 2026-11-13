using System.Globalization;
using System.Text;
using JDict = System.Collections.Generic.OrderedDictionary<string, object?>;

namespace MuxStudio.Core;

// ==========================================================================
//  ỨNG VIÊN
// ==========================================================================

/// <summary>Độ lệch đo được của một ứng viên (port <c>subfetch.Offset</c>). <see cref="Total"/> &gt; 0: ước tính từ preview (hiện
/// dấu ~); -1: đo thật.</summary>
public sealed record CandOffset(double Offset, int Votes, int Total);

/// <summary>Một ứng viên phụ đề (port <c>subfetch.Candidate</c> = <c>New-Cand</c>). <see cref="Src"/> ∈ <c>SubSource</c> |
/// <c>Titulky</c> | <c>OpenSub</c>; <see cref="Payload"/>: bảng JSON (SubSource / OpenSubtitles) hoặc <see cref="SearchRow"/> (Titulky).</summary>
public sealed class Candidate(string src, int tier, string fps, double score, string release, string info, object? payload,
                              CandOffset? off = null, bool machine = false, bool isPack = false)
{
    public string Src { get; } = src;

    /// <summary>1 = FPS khớp video, 2 = không khai báo, 3 = lệch.</summary>
    public int Tier { get; } = tier;

    public string Fps { get; } = fps;

    /// <summary>Độ giống tên bản release với tên file phim.</summary>
    public double Score { get; } = score;

    public string Release { get; } = release;
    public string Info { get; } = info;
    public object? Payload { get; } = payload;
    public CandOffset? Off { get; } = off;
    public bool Machine { get; } = machine;
    public bool IsPack { get; } = isPack;

    public JDict? PayloadDict => Payload as JDict;

    public (string Src, string Release) Ident => (Src, Release);

    /// <summary>Cột FPS của bảng ứng viên: KHỚP / không khai báo / LỆCH.</summary>
    public string FpsLabel => Tier switch
    {
        1 => Fps == "đo: khớp" ? "đo được: KHỚP" : $"{Fps} KHỚP",
        2 => "không khai báo",
        _ => Fps == "lệch" ? "đo được: LỆCH" : $"{Fps} LỆCH",
    };

    /// <summary>Cột Lệch: <c>+1.23s</c> (đo thật), <c>+1.23s~</c> (ước tính từ preview), <c>?</c> (chưa đo được).</summary>
    public string OffsetLabel
    {
        get
        {
            if (Off is null)
                return "?";
            var sign = Off.Offset >= 0 ? "+" : "";
            var txt = $"{sign}{Py.Str(Py.Round(Off.Offset, 2))}s";
            return Off.Total > 0 ? txt + "~" : txt;
        }
    }
}

/// <summary>Phụ đề gốc làm mốc thời gian (port <c>subfetch.RefInfo</c>). <see cref="Pending"/>: (file tạm, đích trong
/// <c>srt_goc</c>) — ghi lúc thực thi.</summary>
public sealed record RefInfo(List<double> Times, string Path, string Source, (string Tmp, string Final)? Pending = null);

/// <summary>Phụ đề bạn tự bỏ vào <c>srt_can_sync</c> được nhận cho tập này (port <c>subfetch.ExistingMatch</c>).
/// <see cref="Votes"/> = -1: không đo được.</summary>
public sealed record ExistingMatch(string File, int Votes, double NameScore, bool FpsBad)
{
    public string Reason => Votes >= 0
        ? Votes.ToString(CultureInfo.InvariantCulture) + " mốc khớp"
        : "tên giống " + Py.Str(Py.Round(NameScore, 2));
}

/// <summary>Một file phim của bước ① (port <c>subfetch.VideoInfo</c>).</summary>
public sealed class VideoInfo(string path, string stem, EpisodeInfo? ep, string title, int? year, double? fps, List<string> tokens)
{
    public string Path { get; } = path;
    public string Stem { get; } = stem;
    public EpisodeInfo? Ep { get; } = ep;
    public string Title { get; } = title;
    public int? Year { get; } = year;
    public double? Fps { get; } = fps;
    public List<string> Tokens { get; } = tokens;
    public RefInfo? Ref { get; set; }
    public JDict? Entry { get; set; }
    public bool EntryChecked { get; set; }
    public string? Imdb { get; set; }

    /// <summary>Số tập tuyệt đối → (season, tập) theo TMDB.</summary>
    public (int Season, int Episode)? Mapped { get; set; }

    public bool LangReported { get; set; }

    /// <summary>Tên chính thức theo TMDB (đủ dấu câu) — để hỏi các nguồn so khớp nguyên văn (Titulky).</summary>
    public string Official { get; set; } = "";

    /// <summary>Ngôn ngữ → (file, "trong MKV: …" / "cạnh phim").</summary>
    public Dictionary<string, (string File, string Where)> HasSubs { get; } = new(StringComparer.Ordinal);

    public string KindText => Ep is null ? "phim lẻ" : $"phim bộ {Ep.Label}";
}

/// <summary>Một phim × một ngôn ngữ (port <c>subfetch.FetchItem</c>).</summary>
public sealed class FetchItem(VideoInfo video, string lang, string langDir)
{
    public VideoInfo Video { get; } = video;
    public string Lang { get; } = lang;
    public string LangDir { get; } = langDir;
    public string? Already { get; set; }

    /// <summary>"trong MKV" / "cạnh phim" (TĐ-36); "" = file đã nằm trong <c>srt_can_sync</c>.</summary>
    public string AlreadyNote { get; set; } = "";

    public ExistingMatch? Existing { get; set; }
    public List<Candidate> Candidates { get; set; } = [];
    public bool Gathered { get; set; }
    public Candidate? Chosen { get; set; }
    public string ChoiceReason { get; set; } = "";
    public bool Skip { get; set; }
    public string Status { get; set; } = "";
    public string? Result { get; set; }
    public string ResultSrc { get; set; } = "";
    public string TimingNote { get; set; } = "";

    public string MemoryKey
    {
        get
        {
            var e = Video.Entry;
            var b = e is { Count: > 0 } && e.Get("movieId") is { } mid ? Py.Str(mid) : Video.Stem;
            return $"{b}|{Lang}";
        }
    }

    public string Dest(string ext = ".srt") => Py.PathJoin(LangDir, $"{Video.Stem}.{Lang}{ext.ToLowerInvariant()}");

    public bool NeedsChoice => !(Already is not null || Existing is not null || Skip || Chosen is not null) && Candidates.Count > 0;
}

/// <summary>Tổng kết lượt chạy bước ① (port <c>subfetch.FetchReport</c>).</summary>
public sealed class FetchReport
{
    public int Ok { get; set; }
    public int UsedExisting { get; set; }
    public int Already { get; set; }
    public int Missing { get; set; }
    public int Skipped { get; set; }
    public int OsRemaining { get; set; } = -1;
    public int OsAllowed { get; set; } = -1;
    public string OsReset { get; set; } = "";
    public string? LogPath { get; set; }
    public string? ReportPath { get; set; }
    public bool Stopped { get; set; }
}

// ==========================================================================
//  NHỚ LỰA CHỌN (Select-Candidate)
// ==========================================================================

/// <summary>Ghi nhớ lựa chọn theo season + ngôn ngữ; KHÔNG dùng lại bản riêng của một tập khác (port <c>subfetch.ChoiceMemory</c>).</summary>
public sealed class ChoiceMemory(Job? job = null)
{
    private readonly Dictionary<string, Candidate> _m = new(StringComparer.Ordinal);

    public Job Job { get; set; } = job ?? Job.Null;

    public Candidate? Recall(string key, string videoName, IEnumerable<Candidate> cands)
    {
        if (!_m.TryGetValue(key, out var prev))
            return null;
        if (Episodes.EpisodeConflict(videoName, prev.Release))
        {
            Job.Dim("  [i] Lựa chọn trước là bản riêng của tập khác → chọn lại cho tập này.");
            _m.Remove(key);
            return null;
        }
        foreach (var c in cands)
        {
            if (c.Ident == prev.Ident)
            {
                Job.Info($"  [i] Dùng lại lựa chọn trước: {prev.Src} - {prev.Release}");
                return c;
            }
        }
        return null;
    }

    public bool Remember(string key, string videoName, Candidate cand)
    {
        if (Episodes.EpisodeConflict(videoName, cand.Release))
            return false;
        _m[key] = cand;
        return true;
    }

    public void Forget(string key, string? release = null)
    {
        if (_m.TryGetValue(key, out var c) && (release is null || c.Release == release))
            _m.Remove(key);
    }

    public Candidate? Get(string key) => _m.GetValueOrDefault(key);
}

// ==========================================================================
//  PHỤ ĐỀ GỐC LÀM MỐC (Get-RefTimes + Find-RefInFolder)
// ==========================================================================

/// <summary>Port <c>subfetch.RefProvider</c>.</summary>
public sealed class RefProvider(string targetDir, Config cfg, string? mkvmerge, string? mkvextract, string tempDir, Job job,
                                Dictionary<string, JDict?> jsonCache)
{
    private static readonly string[] RefExts = [".srt", ".ass", ".ssa"];
    private readonly Dictionary<string, RefInfo?> _cache = new(StringComparer.Ordinal);

    public Job Job { get; set; } = job;

    public string? ExDir => cfg.Bool("KeepExtractedRef") && cfg.Str("ExtractFolder").Length > 0 ? Py.Join(targetDir, cfg.Str("ExtractFolder")) : null;

    public RefInfo? Get(string video, JDict? j = null)
    {
        var key = Py.NormCase(Py.AbsPath(video));
        if (!_cache.TryGetValue(key, out var v))
        {
            v = GetCore(video, j);
            _cache[key] = v;
        }
        return v;
    }

    private RefInfo? GetCore(string video, JDict? j)
    {
        var ex = ExDir;
        var stem = Subprep.Stem(video);
        var exname = cfg.Str("ExtractFolder");
        // TD-08: srt_goc TRUOC, voi moi file phim (ca MKV khong co phu de, ca MP4)
        if (ex is not null && Directory.Exists(ex))
        {
            foreach (var ext in RefExts)
            {
                var p = Py.PathJoin(ex, stem + ext);
                if (File.Exists(p))
                {
                    var t = Srt.CueTimes(p);
                    if (t is { Count: > 10 })
                    {
                        Job.Dim(string.Create(CultureInfo.InvariantCulture, $"    Mốc gốc: dùng lại {exname}\\{Subprep.Name(p)} ({t.Count} dòng)"));
                        return new RefInfo(t, p, $"dùng lại {exname}\\{Subprep.Name(p)}");
                    }
                }
            }
            if (FindInFolder(video, ex) is { } found)
                return found;
        }
        if (Subprep.Suffix(video).ToLowerInvariant() != ".mkv" || string.IsNullOrEmpty(mkvmerge) || string.IsNullOrEmpty(mkvextract))
            return null;
        j ??= Probe.MkvJson(video, mkvmerge, Job, jsonCache);
        var trk = Probe.PickRefSubTrack(j, cfg.List("RefSubLangs"), cfg.Bool("PgsTimingFallback"));
        if (trk is null)
            return null;
        Directory.CreateDirectory(tempDir);
        var raw = Py.PathJoin(tempDir, $"ref_{Guid.NewGuid():N}"[..12] + trk.Ext);
        Tools.Run([mkvextract, "tracks", video, $"{trk.Id.ToString(CultureInfo.InvariantCulture)}:{raw}"], Job, TimeSpan.FromSeconds(600));
        if (!File.Exists(raw) || new FileInfo(raw).Length <= 32)
        {
            Job.Warn($"    [!] Không trích được phụ đề gốc từ {Subprep.Name(video)}");
            return null;
        }
        string path, finalName;
        if (trk.IsPgs)
        {
            var outp = Py.SplitExt(raw).Root + ".srt";
            var n = Pgs.PgsToTimingSrt(raw, outp, cfg.Str("PgsTimingText"));
            File.Delete(raw);
            if (n == 0)
            {
                Job.Warn($"    [!] Không đọc được mốc thời gian từ PGS: {Subprep.Name(video)}");
                return null;
            }
            (path, finalName) = (outp, stem + ".srt");
        }
        else
        {
            (path, finalName) = (raw, stem + trk.Ext); // Q7: ASS giu la .ass
        }
        var times = Srt.CueTimes(path);
        if (times is null || times.Count == 0)
            return null;
        (string, string)? pending = ex is not null ? (path, Py.PathJoin(ex, finalName)) : null;
        var desc = $"trích mới từ MKV: {trk.Describe()}" + (trk.IsPgs ? " — đọc mốc từ PGS" : "");
        Job.Dim(string.Create(CultureInfo.InvariantCulture, $"    Mốc gốc: {trk.Describe()}, {times.Count} dòng"));
        return new RefInfo(times, path, desc, pending);
    }

    /// <summary><c>Find-RefInFolder</c>: file mốc trong <c>srt_goc</c> khi tên KHÔNG trùng tên phim. Ưu tiên đúng số tập; không có số
    /// tập thì so độ giống tên (≥ 0,35). Nhận cả <c>.sup</c>.</summary>
    private RefInfo? FindInFolder(string video, string ex)
    {
        var vstem = Subprep.Stem(video);
        var cands = Tools.WalkFiles(ex, false, [.. RefExts, ".sup"]).Where(f => !Episodes.EpisodeConflict(vstem, Subprep.Stem(f))).ToList();
        if (cands.Count == 0)
            return null;
        var myEp = Episodes.ParseEpisode(vstem);
        if (myEp is not null)
        {
            var exact = cands.Where(f => Episodes.SameEpisode(myEp, Episodes.ParseEpisode(Subprep.Stem(f)))).ToList();
            if (exact.Count > 0)
                cands = exact;
        }
        var vt = Naming.TitleTokens(vstem);
        string? best = null;
        var bs = -1.0;
        foreach (var c in cands)
        {
            var sc = Naming.TitleScore(vt, Naming.TitleTokens(Subprep.Stem(c))) + (myEp is not null ? 1.0 : 0.0);
            if (sc > bs)
            {
                best = c;
                bs = sc;
            }
        }
        if (best is null || bs < 0.35)
            return null;
        var exname = cfg.Str("ExtractFolder");
        (string, string)? pending = null;
        var path = best;
        if (Subprep.Suffix(best).ToLowerInvariant() == ".sup")
        {
            var conv = Py.SplitExt(best).Root + ".srt";
            if (File.Exists(conv))
            {
                path = conv;
            }
            else
            {
                Directory.CreateDirectory(tempDir);
                var tmp = Py.PathJoin(tempDir, $"pgs_{Guid.NewGuid():N}"[..12] + ".srt");
                if (Pgs.PgsToTimingSrt(best, tmp, cfg.Str("PgsTimingText")) == 0)
                    return null;
                Job.Info($"    [pgs] {Subprep.Name(best)} → {Subprep.Name(conv)}");
                (path, pending) = (tmp, (tmp, conv));
            }
        }
        var t = Srt.CueTimes(path);
        if (t is null || t.Count <= 10)
            return null;
        Job.Dim(string.Create(CultureInfo.InvariantCulture, $"    Mốc gốc: {exname}\\{Subprep.Name(best)} ({t.Count} dòng)"));
        return new RefInfo(t, path, $"{exname}\\{Subprep.Name(best)} (tìm theo tập/tên)", pending);
    }
}

// ==========================================================================
//  Hàm không cần mạng
// ==========================================================================

/// <summary>Hàm cấp module của <c>core/subfetch.py</c>.</summary>
public static class SubFetch
{
    public static readonly IReadOnlyList<string> SubExts = [".srt", ".ass", ".ssa"];
    public static readonly IReadOnlyList<string> SideExts = [".srt", ".ass", ".ssa", ".vtt"];

    /// <summary><c>fps_tier</c> = <c>Get-FpsTier</c>: 1 = khai báo khớp video (chênh &lt; 0,05), 2 = không khai báo, 3 = lệch.</summary>
    public static int FpsTier(object? declared, double? videoFps)
    {
        if (videoFps is not { } vf || vf == 0)
            return 2;
        var f = Timing.ToDouble(declared);
        if (f <= 0)
            return 2;
        return Math.Abs(f - vf) < 0.05 ? 1 : 3;
    }

    /// <summary><c>sort_candidates</c>: hạng FPS → đã đo được độ lệch → bản thường trước bản dịch máy (Q2) → |độ lệch| nhỏ → giống tên.
    /// KHÔNG cộng điểm lượt tải hay codec.</summary>
    public static List<Candidate> SortCandidates(IEnumerable<Candidate> cands) =>
        [.. cands.OrderBy(c => c.Tier).ThenBy(c => c.Off is not null ? 0 : 1).ThenBy(c => c.Machine ? 1 : 0)
                 .ThenBy(c => c.Off is { } o ? Math.Abs(o.Offset) : 999.0).ThenBy(c => -c.Score)];

    /// <summary><c>embedded_sub_langs</c>: ngôn ngữ đã có track phụ đề CHỮ trong MKV → mô tả track (TĐ-36). Tính cả SDH; không tính
    /// Forced, phụ đề dạng ảnh và track không ghi ngôn ngữ (<c>und</c>).</summary>
    public static OrderedDictionary<string, string> EmbeddedSubLangs(JDict? j)
    {
        var outp = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in Probe.Tracks(j, "subtitles"))
        {
            var p = t.GetDict("properties");
            var ext = Probe.SubExtByCodec(p.GetStr("codec_id"), allowPgs: false);
            if (string.IsNullOrEmpty(ext) || Probe.IsForced(p))
                continue;
            var lang = Probe.TrackLang(t);
            if (lang.Length == 0 || lang == "und" || outp.ContainsKey(lang))
                continue;
            var name = Py.StripWs(p.GetStr("track_name"));
            outp[lang] = $"track {Py.Str(t.Get("id", "?"))} {ext[1..].ToUpperInvariant()}" + (name.Length > 0 ? $" '{name}'" : "");
        }
        return outp;
    }

    /// <summary><c>sidecar_sub_langs</c>: ngôn ngữ đã có file phụ đề nằm CẠNH phim: <c>&lt;tên phim&gt;.&lt;mã&gt;.srt|ass|ssa|vtt</c>
    /// (kết quả của bước ②). Bỏ file Forced. File không có hậu tố ngôn ngữ không tính.</summary>
    public static OrderedDictionary<string, string> SidecarSubLangs(string video)
    {
        var outp = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        var prefix = Subprep.Stem(video).ToLowerInvariant() + ".";
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
            var name = e.Name;
            var low = name.ToLowerInvariant();
            if (!low.StartsWith(prefix, StringComparison.Ordinal) || !SideExts.Any(x => low.EndsWith(x, StringComparison.Ordinal)))
                continue;
            if ((e.Attributes & FileAttributes.Directory) != 0 || !File.Exists(e.FullName))
                continue;
            var rest = name[prefix.Length..];
            var dot = rest.LastIndexOf('.');
            var parts = (dot >= 0 ? rest[..dot] : rest).Split('.');
            if (parts.Length == 0 || parts[0].Length == 0 || parts.Any(x => x.ToLowerInvariant() == "forced"))
                continue;
            var lang = Naming.LangFromWord(parts[0]);
            if (lang.Length > 0 && !outp.ContainsKey(lang))
                outp[lang] = Py.PathJoin(Subprep.Parent(video), name);
        }
        return outp;
    }
}

// ==========================================================================
//  PHIÊN LÀM VIỆC BƯỚC ①
// ==========================================================================

/// <summary>
/// Bước ① — Tải phụ đề (thay SubFetch_v30.bat; port <c>subfetch.FetchSession</c>). docs/SPEC.md mục 4.1, §6.11. Hai bước:
/// <list type="number">
/// <item><b>Lập kế hoạch</b> (<see cref="Scan"/> → <see cref="Gather"/> → <see cref="AutoSelect"/>/<see cref="Choose"/>): KHÔNG ghi gì vào
/// thư mục phim, KHÔNG tốn quota. Phụ đề gốc trích từ MKV nằm ở thư mục tạm; gói ZIP của SubSource (miễn phí) có thể tải trước vào thư
/// mục tạm để đo thẳng từng tập.</item>
/// <item><b>Thực thi</b> (<see cref="Execute"/>): tải/chép đúng kế hoạch, ghi nhật ký hoàn tác <c>_subfetch_logs\actions_*.csv</c> và báo
/// cáo <c>fetch_*.csv</c>.</item>
/// </list>
/// Nguồn theo thứ tự: file có sẵn trong <c>srt_can_sync</c> → SubSource → Titulky.com (chỉ tiếng Séc) → OpenSubtitles. Bước ① KHÔNG
/// sửa thời gian phụ đề — chỉ đo và báo. Phụ đề ASS giữ nguyên là ASS (Q7).
/// </summary>
public sealed class FetchSession : IDisposable
{
    private readonly Func<string, JDict?> _probe;
    private readonly bool _missingKeySs;
    private bool _connected;

    /// <param name="toolPaths">(cho test) <c>mkvmerge</c> / <c>mkvextract</c>; bỏ trống = tự tìm.</param>
    /// <param name="videoProbe">(cho test) thay <c>mkvmerge -J</c>.</param>
    /// <param name="languages">bỏ trống = <c>Languages</c> trong cấu hình.</param>
    /// <param name="sources">bật/tắt nguồn (<c>subsource</c>, <c>titulky</c>, <c>opensubtitles</c>) đè lên cấu hình.</param>
    public FetchSession(string targetDir, Config cfg, IReadOnlyDictionary<string, string>? secrets, Job? job = null, Http? http = null,
                        Action<double>? sleep = null, ToolPaths? toolPaths = null, Func<string, JDict?>? videoProbe = null,
                        IEnumerable<string>? languages = null, IReadOnlyDictionary<string, bool>? sources = null)
    {
        Td = targetDir;
        Cfg = cfg;
        Job = job ?? new Job();
        secrets ??= new Dictionary<string, string>();
        Languages = [.. (languages ?? cfg.List("Languages")).Where(c => Py.StripWs(c).Length > 0).Select(c => Py.StripWs(c).ToLowerInvariant())];
        foreach (var c in Languages)
            Naming.RegisterLanguage(c);
        var src = new OrderedDictionary<string, bool>(StringComparer.Ordinal)
        {
            ["subsource"] = true, ["titulky"] = cfg.Bool("UseTitulky"), ["opensubtitles"] = cfg.Bool("UseOpenSubtitles"),
        };
        foreach (var (k, v) in sources ?? new Dictionary<string, bool>())
            src[k] = v;
        Sources = src;
        Temp = Directory.CreateTempSubdirectory("muxstudio_fetch_").FullName;
        if (toolPaths is not null)
        {
            Mkvmerge = toolPaths.Mkvmerge;
            Mkvextract = toolPaths.Mkvextract;
        }
        else
        {
            Mkvmerge = Tools.FindTool("mkvmerge", cfg, Td);
            Mkvextract = Tools.FindTool("mkvextract", cfg, Td);
        }
        _probe = videoProbe ?? (p => Probe.MkvJson(p, Mkvmerge, Job, JsonCache));
        Refs = new RefProvider(Td, cfg, Mkvmerge, Mkvextract, Py.PathJoin(Temp, "refs"), Job, JsonCache);
        Memory = new ChoiceMemory(Job);

        var key = secrets.GetValueOrDefault("SubSourceApiKey", "");
        if (src["subsource"] && key.Length > 0)
            Ss = new SubSourceClient(key, cfg.Str("SubSourceApiBase"), cfg.Int("ApiPauseMs"), cfg.Int("MaxPages"), http, sleep, job: Job,
                                     cacheDir: Py.PathJoin(Temp, "packs"));
        var tkey = secrets.GetValueOrDefault("TmdbApiKey", "");
        Tmdb = tkey.Length > 0 ? new TmdbClient(tkey, "en-US", cfg.Int("TmdbRetry"), http, sleep, Job) : null;
        if (src["opensubtitles"] && secrets.GetValueOrDefault("OsApiKey", "").Length > 0)
        {
            Os = new OpenSubtitlesClient(secrets.GetValueOrDefault("OsApiKey", ""), secrets.GetValueOrDefault("OsUsername", ""),
                secrets.GetValueOrDefault("OsPassword", ""), cfg.Str("OsUserAgent"), cfg.Int("ApiPauseMs"), cfg.Int("OsWarnRemaining"),
                cfg.Int("OsStopRemaining"), http, sleep, Job, Py.PathJoin(Temp, "os"));
        }
        // Titulky.com: chi tieng Sec; tai khoan khong bat buoc (khach van tim / tai duoc, hay phai go CAPTCHA)
        if (src["titulky"] && Languages.Contains(Titulky.Lang))
        {
            Tt = new TitulkyClient(secrets.GetValueOrDefault("TitulkyUsername", ""), secrets.GetValueOrDefault("TitulkyPassword", ""),
                                   cfg.Int("ApiPauseMs"), http, sleep, Job, Py.PathJoin(Temp, "titulky"));
        }
        _missingKeySs = src["subsource"] && key.Length == 0;
    }

    public string Td { get; }
    public Config Cfg { get; }
    public Job Job { get; private set; }
    public List<string> Languages { get; }
    public OrderedDictionary<string, bool> Sources { get; }
    public string Temp { get; }
    public string? Mkvmerge { get; }
    public string? Mkvextract { get; }
    public Dictionary<string, JDict?> JsonCache { get; } = new(StringComparer.Ordinal);
    public RefProvider Refs { get; }
    public ChoiceMemory Memory { get; }
    public SubSourceClient? Ss { get; }
    public TmdbClient? Tmdb { get; }
    public OpenSubtitlesClient? Os { get; }
    public TitulkyClient? Tt { get; }

    /// <summary>Hàm hỏi mã CAPTCHA của Titulky <c>(ảnh, ghi chú) → mã</c> — giao diện gán vào; <c>null</c> = không hỏi được.</summary>
    public Func<byte[], string, string?>? Captcha { get; set; }

    /// <summary>(cho test) thay <see cref="Undo.Trash"/> (bỏ file OpenSubtitles kiểm chứng yếu).</summary>
    public Func<string, bool, bool> Trash { get; set; } = Undo.Trash;

    public List<FetchItem> Items { get; private set; } = [];
    public List<VideoInfo> Videos { get; private set; } = [];

    /// <summary><c>set_job</c>: giao diện — phiên sống qua nhiều lần bấm nút, mỗi lần một <see cref="Core.Job"/> mới.</summary>
    public void SetJob(Job job)
    {
        Job = job;
        Memory.Job = job;
        Refs.Job = job;
        if (Ss is not null)
            Ss.Job = job;
        if (Tmdb is not null)
            Tmdb.Job = job;
        if (Os is not null)
            Os.Job = job;
        if (Tt is not null)
            Tt.Job = job;
    }

    // ------------------------------------------------------------------ chuan bi
    public string SubDir => Py.PathJoin(Td, Cfg.Str("SubFolderName"));

    public string LangDir(string lang) => Cfg.Bool("UseLangSubFolders") ? Py.PathJoin(SubDir, lang) : SubDir;

    /// <summary><c>excluded_dirs</c>: Q6/TĐ-11 — không tải phụ đề cho video trong Output, thư mục audio nguồn, kho, srt_goc, log.</summary>
    public List<string> ExcludedDirs()
    {
        var c = Cfg;
        List<string> names = [c.Str("SubFolderName"), "Output", c.Str("DonorFolder"), Tools.LegacyPoolFolder, c.Str("ExtractFolder"),
                              c.Str("SubFetchLogDir"), c.Str("SubPrepLogDir"), c.Str("MuxLogDir"), .. c.List("RefExcludeFolders")];
        return [.. names.Where(n => n.Length > 0).Select(n => Py.PathJoin(Td, n))];
    }

    /// <summary><c>connect</c>.</summary>
    public void Connect()
    {
        if (_connected)
            return;
        _connected = true;
        var c = Cfg;
        if (_missingKeySs)
            Job.Warn("[!] Chưa điền SubSource API key → bỏ qua nguồn SubSource (các nguồn khác vẫn chạy).");
        var unknown = Languages.Where(lg => Naming.GetLang(lg) is not { SubSource.Length: > 0 }).ToList();
        if (unknown.Count > 0 && Ss is not null)
            Job.Warn($"[!] Chưa biết tên SubSource cho: {string.Join(", ", unknown)} — sẽ chỉ tìm trên OpenSubtitles.");
        if (string.IsNullOrEmpty(Mkvmerge))
            Job.Warn("[!] Không tìm thấy mkvmerge → không đọc được framerate video.");
        if (c.Bool("VerifyTiming") && string.IsNullOrEmpty(Mkvextract))
            Job.Warn("[!] Thiếu mkvextract → chỉ dùng được phụ đề gốc có sẵn trong srt_goc.");
        if (c.Bool("StrictFpsMatch"))
        {
            Job.Dim("[i] Ràng buộc framerate: BẬT (" + (c.Bool("AllowUnknownFps") ? "cho phép bản không khai báo fps"
                                                                                    : "chỉ lấy bản khai báo đúng fps") + ").");
        }
        if (Tmdb is null)
            Job.Dim("[i] Chưa có TMDB key → tìm theo tên thay vì IMDb ID.");
        Os?.Login();
        if (Tt is not null)
        {
            if (Tt.HasAccount)
                Tt.Login();
            else
                Job.Dim("[i] Titulky: dùng kiểu khách (chưa điền tài khoản) — lúc tải có thể phải gõ mã CAPTCHA.");
        }
    }

    // ------------------------------------------------------------------ 1. quet

    /// <summary><c>scan</c>: liệt kê phim × ngôn ngữ, đọc FPS, tìm phụ đề gốc, nhận file có sẵn. Không ghi gì.</summary>
    public List<FetchItem> Scan()
    {
        Connect();
        var c = Cfg;
        var vids = Tools.WalkFiles(Td, c.Bool("ScanSubFolders"), c.List("VideoExts"), ExcludedDirs());
        Items = [];
        Videos = [];
        if (vids.Count == 0)
        {
            Job.Error("[!] Không tìm thấy file video nào.");
            return [];
        }
        Job.Info(string.Create(CultureInfo.InvariantCulture, $"Tìm thấy {vids.Count} file video. Ngôn ngữ cần tải: {string.Join(", ", Languages)}"));
        for (var i = 0; i < vids.Count; i++)
        {
            var v = vids[i];
            Job.Check();
            Job.Progress(i + 1, vids.Count, Subprep.Name(v));
            var info = VideoInfoOf(v);
            Videos.Add(info);
            foreach (var lang in Languages)
            {
                var it = new FetchItem(info, lang, LangDir(lang));
                CheckExisting(it);
                Items.Add(it);
            }
        }
        return Items;
    }

    private VideoInfo VideoInfoOf(string v)
    {
        var stem = Subprep.Stem(v);
        var ep = Episodes.ParseEpisode(stem);
        string title;
        int? year;
        if (ep is not null)
        {
            title = ep.Series.Length > 0 ? ep.Series : Naming.SplitReleaseName(stem).Title;
            year = ep.Year;
        }
        else
        {
            var rn = Naming.SplitReleaseName(stem);
            (title, year) = (rn.Title, rn.Year);
        }
        var j = _probe(v);
        var info = new VideoInfo(v, stem, ep, title, year, Probe.VideoFps(j), Naming.TitleTokens(stem));
        Job.Head($"=== {Subprep.Name(v)}");
        Job.Dim($"    Nhận diện: '{title}'" + (year is { } y && y != 0 ? string.Create(CultureInfo.InvariantCulture, $" ({y})") : "")
                + $" - {info.KindText}" + (info.Fps is { } f && f != 0 ? $" - {Py.Str(f)} fps" : ""));
        if (Cfg.Bool("SkipIfVideoHasSub"))
        {
            // TD-36: phim da co phu de ngon ngu do (file canh phim truoc, roi track trong MKV) -> khong tim nua
            foreach (var (lang, file) in SubFetch.SidecarSubLangs(v))
                info.HasSubs[lang] = (file, "cạnh phim");
            foreach (var (lang, desc) in SubFetch.EmbeddedSubLangs(j))
                info.HasSubs.TryAdd(lang, (v, $"trong MKV: {desc}"));
        }
        if (Cfg.Bool("VerifyTiming") && !Languages.All(info.HasSubs.ContainsKey))
            info.Ref = Refs.Get(v, j); // du moi ngon ngu roi thi khoi trich phu de goc lam moc
        return info;
    }

    private void CheckExisting(FetchItem it)
    {
        if (it.Video.HasSubs.TryGetValue(it.Lang, out var have))
        {
            (it.Already, it.AlreadyNote) = have;
            it.Status = "phim đã có";
            var what = have.File == it.Video.Path ? have.Where : $"cạnh phim: {Subprep.Name(have.File)}";
            Job.Dim($"    [=] {it.Lang} : phim đã có phụ đề ({what}), không tìm nữa.");
            return;
        }
        if (Cfg.Bool("SkipIfExists"))
        {
            foreach (var ext in SubFetch.SubExts)
            {
                var p = it.Dest(ext);
                if (File.Exists(p))
                {
                    it.Already = p;
                    it.Status = "đã có";
                    Job.Dim($"    [=] {it.Lang} : đã có sẵn, bỏ qua tải.");
                    return;
                }
            }
        }
        if (Cfg.Bool("UseExistingSubs") && MatchExisting(it) is { } m)
        {
            it.Existing = m;
            it.Status = $"dùng file có sẵn ({m.Reason})";
        }
    }

    private static bool EndsWithLangTag(string stem, string lang) =>
        stem.Length > lang.Length && stem.EndsWith(lang, StringComparison.OrdinalIgnoreCase) && stem[^(lang.Length + 1)] is '.' or '_' or '-';

    /// <summary><c>match_existing</c> = <c>Use-ExistingSub</c> (phần chọn): đo được thời gian thì CHỈ tin phép đo (≥ <c>MinVerifyVotes</c>);
    /// không đo được mới dựa vào tên (≥ <c>ExistingMinScore</c>). Tên ghi số tập khác → loại ngay.</summary>
    public ExistingMatch? MatchExisting(FetchItem it)
    {
        var v = it.Video;
        var lang = it.Lang;
        var cands = new List<string>();
        if (Directory.Exists(it.LangDir))
            cands.AddRange(Tools.WalkFiles(it.LangDir, false, SubFetch.SubExts));
        var root = SubDir;
        if (Directory.Exists(root) && Py.NormCase(Py.AbsPath(root)) != Py.NormCase(Py.AbsPath(it.LangDir)))
            cands.AddRange(Tools.WalkFiles(root, false, SubFetch.SubExts).Where(f => EndsWithLangTag(Subprep.Stem(f), lang)));
        if (cands.Count == 0)
            return null;
        var before = cands.Count;
        cands = cands.Where(f => !Episodes.EpisodeConflict(v.Stem, Subprep.Stem(f))).ToList();
        if (before - cands.Count > 0)
            Job.Dim(string.Create(CultureInfo.InvariantCulture, $"    [i] {lang} : bỏ {before - cands.Count} file thuộc tập khác."));
        if (cands.Count == 0)
            return null;
        var refT = v.Ref?.Times is { Count: > 0 } rt ? rt : null;
        (double Rank, ExistingMatch M)? best = null;
        foreach (var f in cands)
        {
            var ns = Naming.TitleScore(v.Tokens, Naming.TitleTokens(Subprep.Stem(f)));
            var votes = -1;
            var fpsBad = false;
            if (refT is not null)
            {
                var t = Srt.CueTimes(f);
                if (t is { Count: >= 30 } && Timing.TimingRelationOf(refT, t, Cfg.Int("FpsSampleRef"), Cfg.Int("FpsSampleTgt")) is { } rel)
                    (votes, fpsBad) = (rel.Votes, rel.FpsMismatch);
            }
            var rank = votes >= 0 ? votes * 100 + ns : ns;
            if (best is null || rank > best.Value.Rank)
                best = (rank, new ExistingMatch(f, votes, ns, fpsBad));
        }
        var m = best!.Value.M;
        var measured = m.Votes >= 0;
        var okTime = measured && m.Votes >= Cfg.Int("MinVerifyVotes");
        var okName = !measured && m.NameScore >= Cfg.Double("ExistingMinScore");
        if (!okTime && !okName)
        {
            Job.Dim(string.Create(CultureInfo.InvariantCulture, $"    [i] {lang} : có {cands.Count} file sẵn nhưng không file nào khớp phim này."));
            if (measured)
            {
                Job.Dim(string.Create(CultureInfo.InvariantCulture,
                    $"        gần nhất: {Subprep.Name(m.File)} - chỉ {m.Votes} mốc khớp (cần >= {Py.Str(Cfg["MinVerifyVotes"])})"));
            }
            else
            {
                Job.Dim($"        gần nhất: {Subprep.Name(m.File)} - tên giống {Py.Str(Py.Round(m.NameScore, 2))} (cần >= {Py.Str(Cfg["ExistingMinScore"])})");
            }
            return null;
        }
        Job.Ok($"    [=] {lang} : DÙNG FILE CÓ SẴN ({m.Reason}) — {Subprep.Name(m.File)}");
        if (m.FpsBad)
            Job.Warn("        [!] Đo được LỆCH FPS - bước ② sẽ cần xử lý.");
        return m;
    }

    // ------------------------------------------------------------------ 2. gom ung vien
    private void EnsureEntry(VideoInfo v)
    {
        if (v.EntryChecked)
            return;
        v.EntryChecked = true;
        var ep = v.Ep;
        var isTv = ep is not null;
        int? season = null;
        if (Tmdb is not null && v.Title.Length > 0)
        {
            if (ep is not null)
            {
                v.Imdb = Tmdb.ImdbId(v.Title, v.Year, true, ep.HasSeason ? ep.Season : null, ep.Episode);
                if (!ep.HasSeason)
                {
                    var show = Tmdb.FindShow(v.Title, v.Year, null, ep.Episode);
                    if (show is { Count: > 0 })
                    {
                        v.Mapped = Tmdb.AbsoluteToSeason(Py.ToInt(show.Get("id")), ep.Episode);
                        if (v.Mapped is { } mp)
                            Job.Info(string.Create(CultureInfo.InvariantCulture, $"    [i] Số tập tuyệt đối {ep.RefLabel} → S{mp.Season:00}E{mp.Episode:00} (TMDB)"));
                    }
                }
            }
            else
            {
                v.Imdb = Tmdb.ImdbId(v.Title, v.Year, false);
            }
            if (!string.IsNullOrEmpty(v.Imdb))
                Job.Dim($"    IMDb: {v.Imdb}");
            v.Official = Tmdb.OfficialTitle(v.Title, v.Year, isTv, ep is { HasSeason: true } ? ep.Season : null, ep?.Episode) ?? "";
        }
        if (ep is not null)
            season = ep.HasSeason ? ep.Season : v.Mapped?.Season;
        if (Ss is null)
            return;
        if (isTv && season is null)
        {
            Job.Warn("    [!] Số tập tuyệt đối, không xác định được season → bỏ qua SubSource cho tập này.");
            return;
        }
        v.Entry = Ss.FindEntry(v.Title, v.Year, season, v.Imdb);
        if (v.Entry is { Count: > 0 } e)
        {
            Job.Dim($"    SubSource: {Py.Str(e.Get("title"))}" + (Py.Truthy(e.Get("season")) ? $" - season {Py.Str(e.Get("season"))}" : "")
                    + $" (movieId={Py.Str(e.Get("movieId"))}, {Py.Str(e.Get("subtitleCount"))} phụ đề)");
        }
        else
        {
            Job.Warn("    [-] Không tìm thấy trên SubSource.");
        }
    }

    private static (int? Season, int? Episode) OsEpisode(VideoInfo v)
    {
        var ep = v.Ep;
        if (ep is null)
            return (null, null);
        if (ep.HasSeason)
            return (ep.Season, ep.Episode);
        if (v.Mapped is { } m)
            return (m.Season, m.Episode);
        return (null, ep.Episode);
    }

    private string? PackFile(VideoInfo v, string? d)
    {
        var c = Cfg;
        var f = SubSource.SelectFromPack(d, v.Ep, v.Tokens, c.Bool("ExcludeForced"), c.Bool("ExcludeSdh"));
        if (f is null && v.Mapped is { } m && v.Ep is not null)
        {
            var mep = new EpisodeInfo(v.Ep.Series, v.Ep.Year, m.Season, m.Episode, null, true, true);
            f = SubSource.SelectFromPack(d, mep, v.Tokens, c.Bool("ExcludeForced"), c.Bool("ExcludeSdh"));
        }
        return f;
    }

    /// <summary><c>online_candidates</c> = <c>Get-OnlineCandidates</c>: SubSource → Titulky (chỉ <c>cs</c>) → OpenSubtitles (chạm mạng;
    /// tìm không tốn lượt tải).</summary>
    public List<Candidate> OnlineCandidates(FetchItem it)
    {
        var c = Cfg;
        var v = it.Video;
        var outp = new List<Candidate>();
        var refT = v.Ref?.Times is { Count: > 0 } rt ? rt : null;
        var lg = Naming.GetLang(it.Lang);
        if (Ss is not null && v.Entry is { Count: > 0 } && lg is { SubSource.Length: > 0 })
        {
            foreach (var s in Ss.ListForLang(Py.ToInt(v.Entry.Get("movieId")), it.Lang))
            {
                if (c.Bool("ExcludeSdh") && s.Get("hearingImpaired") is true)
                    continue;
                if (c.Bool("ExcludeForced") && s.Get("foreignParts") is true)
                    continue;
                var rels = s.Get("releaseInfo");
                var rel = rels is System.Collections.IList rl ? (rl.Count > 0 ? Py.Str(rl[0]) : "") : (Py.Truthy(rels) ? Py.Str(rels) : "");
                var sc = Naming.TitleScore(v.Tokens, Naming.TitleTokens(rel));
                var nfiles = SubSourceClient.IntOf(Py.Truthy(s.Get("files")) ? s.Get("files") : 0L);
                var isPack = nfiles > 1;
                CandOffset? off = null;
                var sid = s.Get("subtitleId");
                // Goi da tai ve -> DO THANG file cua dung tap nay (khong ton them luot tai)
                if (refT is not null && sid is not null && Ss.IsDownloaded(Py.ToInt(sid)) && PackFile(v, Ss.PackDir(Py.ToInt(sid))) is { } f)
                {
                    var tt = Srt.CueTimes(f);
                    if (tt is { Count: >= 30 } && Timing.TimingRelationOf(refT, tt, c.Int("FpsSampleRef"), c.Int("FpsSampleTgt")) is { } rr
                        && rr.Votes >= c.Int("MinVerifyVotes"))
                        off = new CandOffset(rr.Offset, rr.Votes, -1);
                }
                // Chua tai -> uoc tinh tu preview (preview cua goi season chi thuoc MOT tap)
                if (off is null && refT is not null && Py.Truthy(s.Get("preview"))
                    && Timing.QuickOffsetOf(refT, Srt.PreviewTimes(Py.Str(s.Get("preview")))) is { } q)
                    off = new CandOffset(q.Offset, q.Votes, q.Total);
                var note = $"{Py.Str(s.Get("files"))} file, {Py.Str(s.Get("downloads"))} tải" + (isPack ? " (gói season)" : "");
                outp.Add(new Candidate("SubSource", SubFetch.FpsTier(s.Get("framerate"), v.Fps), s.GetStr("framerate"), sc, rel, note, s, off,
                                       false, isPack));
            }
        }
        if (Tt is { Disabled: false } && it.Lang == Titulky.Lang)
            outp.AddRange(TitulkyCandidates(it));
        if (Os is { Disabled: false })
        {
            var (season, epn) = OsEpisode(v);
            foreach (var o in Os.Search(v.Imdb, season, epn, it.Lang, v.Title, v.Year))
            {
                if (!OpenSubs.Usable(o, c.Bool("ExcludeSdh"), c.Bool("ExcludeForced"), c.Bool("OsExcludeMachine")))
                    continue;
                var a = o.GetDict("attributes");
                var rel = a.GetStr("release");
                var mt = OpenSubs.IsMachine(o);
                var info = (mt ? "dịch máy, " : "") + $"{Py.Str(a.Get("download_count"))} tải";
                outp.Add(new Candidate("OpenSub", SubFetch.FpsTier(a.Get("fps"), v.Fps), a.GetStr("fps"),
                                       Naming.TitleScore(v.Tokens, Naming.TitleTokens(rel)), rel, info, o, null, mt));
            }
        }
        return outp;
    }

    /// <summary><c>_titulky_keep</c>: lọc kết quả Titulky — chỉ tiếng Séc (<c>CZ</c>), đúng phim (tên + năm) / đúng tập.</summary>
    private static List<SearchRow> TitulkyKeep(VideoInfo v, List<SearchRow> rows, int? season, int? epn)
    {
        var wants = new[] { v.Title, v.Official }.Distinct(StringComparer.Ordinal).Where(t => t.Length > 0).Select(Naming.TitleTokens).ToList();
        bool SameTitle(string name)
        {
            var got = Naming.TitleTokens(name);
            return wants.Any(w => Naming.TitleScore(w, got) >= 0.6);
        }
        var keep = new List<SearchRow>();
        foreach (var r in rows)
        {
            if (r.Lang != "CZ")
                continue;
            var name = r.Series.Length == 0 || r.Title.ToUpperInvariant().Contains(r.Series, StringComparison.Ordinal) ? r.Title : $"{r.Title} {r.Series}";
            var rep = Episodes.ParseEpisode(name);
            bool ok;
            if (v.Ep is null)
            {
                if (rep is not null || r.Series.Length > 0)
                    continue; // mot tap cua phim bo trung ten
                if (v.Year is { } vy && vy != 0 && r.Year is { } ry && ry != 0 && Math.Abs(vy - ry) > 1)
                    continue;
                ok = SameTitle(r.Title) || (r.Release.Length > 0 && Naming.TitleScore(v.Tokens, Naming.TitleTokens(r.Release)) >= 0.5);
            }
            else
            {
                if (rep is null)
                    continue;
                var same = season is not null ? rep.Season == season && rep.Episode == epn && rep.HasSeason : Episodes.SameEpisode(v.Ep, rep);
                ok = same && SameTitle(rep.Series.Length > 0 ? rep.Series : r.Title);
            }
            if (ok)
                keep.Add(r);
        }
        if (v.Ep is null && v.Year is { } y && y != 0 && keep.Any(r => r.Year is { } yy && yy != 0))
            keep = keep.Where(r => r.Year is { } yy && yy != 0).ToList(); // da co ban ghi dung nam -> bo ban KHONG ghi nam
        return keep;
    }

    /// <summary><c>_titulky_rows</c>: tìm trên Titulky rồi lọc. Trang so khớp NGUYÊN VĂN ⇒ hỏi lần lượt: tên chính thức theo TMDB → tên
    /// từ tên file → (nếu vẫn 0 kết quả) từng MẢNH của tên rồi lọc lại.</summary>
    private List<SearchRow> TitulkyRows(VideoInfo v)
    {
        var tt = Tt!;
        int? season = null, epn = null;
        if (v.Ep is not null)
        {
            if (v.Mapped is { } m)
                (season, epn) = (m.Season, m.Episode);
            else if (v.Ep.HasSeason)
                (season, epn) = (v.Ep.Season, v.Ep.Episode);
        }
        var titles = new[] { v.Official, v.Title }.Distinct(StringComparer.Ordinal).Where(t => t.Length > 0).ToList();
        var asked = 0;
        foreach (var t in titles)
        {
            var q = Titulky.SearchQuery(t, season, epn);
            var rows = tt.Search(q, pages: 3);
            var keep = TitulkyKeep(v, rows, season, epn);
            asked += rows.Count;
            Job.Dim(string.Create(CultureInfo.InvariantCulture,
                $"    [i] Titulky: tìm '{q}' → {rows.Count} kết quả, giữ {keep.Count} bản tiếng Séc đúng phim / đúng tập."));
            if (keep.Count > 0)
                return keep;
        }
        if (asked > 0)
            return []; // ten co tren trang nhung khong co ban tieng Sec dung phim -> khong hoi them
        foreach (var frag in titles.Count > 0 ? Titulky.TitleFragments(titles[0]) : [])
        {
            var q = Titulky.SearchQuery(frag, season, epn);
            var rows = tt.Search(q, pages: 4);
            var keep = TitulkyKeep(v, rows, season, epn);
            Job.Dim(string.Create(CultureInfo.InvariantCulture, $"    [i] Titulky: thử một phần tên '{q}' → {rows.Count} kết quả, giữ {keep.Count}."));
            if (keep.Count > 0)
                return keep;
        }
        return [];
    }

    private static readonly string[] TitulkyKinds = ["srt", "ass", "ssa"];

    /// <summary><c>_titulky_candidates</c>: ứng viên từ Titulky.com (tiếng Séc). Với <c>TitulkyProbeTop</c> bản giống tên nhất: xem trang
    /// chi tiết (FPS) và bản xem trước (ước tính độ lệch, không tốn lượt tải).</summary>
    private List<Candidate> TitulkyCandidates(FetchItem it)
    {
        var c = Cfg;
        var v = it.Video;
        var tt = Tt!;
        var refT = v.Ref?.Times is { Count: > 0 } rt ? rt : null;
        var rows = TitulkyRows(v)
                   .OrderBy(r => -Naming.TitleScore(v.Tokens, Naming.TitleTokens(r.Release.Length > 0 ? r.Release : r.Title)))
                   .ThenBy(r => -r.Downloads).ToList();
        var top = Math.Max(0, c.Int("TitulkyProbeTop"));
        var outp = new List<Candidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var shown = rows.Take(c.Int("MaxShowCandidates")).ToList();
        for (var i = 0; i < shown.Count; i++)
        {
            var r = shown[i];
            if (i < top)
                tt.FillDetail(r);
            if (r.Cds > 1 || (r.Kind.Length > 0 && !TitulkyKinds.Contains(r.Kind)))
                continue; // phu de chia nhieu CD / loai file MuxStudio khong dung duoc (vd .sub)
            CandOffset? off = null;
            if (i < top && refT is not null)
            {
                var pv = Srt.PreviewTimes(tt.Preview(r));
                // dong dau cua nhieu ban la quang cao "www.titulky.com" -> thu bo 1, 2 dong dau
                foreach (var skip in new[] { 0, 1, 2 })
                {
                    if (Timing.QuickOffsetOf(refT, pv.Skip(skip).Take(8).ToList()) is { } q)
                    {
                        off = new CandOffset(q.Offset, q.Votes, q.Total);
                        break;
                    }
                }
            }
            var rel = r.Release.Length > 0 ? r.Release : r.Title;
            if (seen.Contains(rel))
                rel = string.Create(CultureInfo.InvariantCulture, $"{rel} #{r.Id}");
            seen.Add(rel);
            outp.Add(new Candidate("Titulky", SubFetch.FpsTier(r.Fps, v.Fps), r.Fps, Naming.TitleScore(v.Tokens, Naming.TitleTokens(rel)), rel,
                                   string.Create(CultureInfo.InvariantCulture, $"{r.Downloads} tải"), r, off));
        }
        return outp;
    }

    /// <summary><c>gather</c>: gom ứng viên từ MỌI nguồn rồi xếp hạng (phần chính của SubFetch cho một tập × ngôn ngữ).</summary>
    public List<Candidate> Gather(FetchItem it)
    {
        var c = Cfg;
        it.Gathered = true;
        if (it.Already is not null || it.Existing is not null || it.Skip)
            return [];
        var v = it.Video;
        EnsureEntry(v);
        var cands = OnlineCandidates(it);
        if (cands.Count == 0)
        {
            it.Status = "không nguồn nào có";
            Job.Warn($"    [-] {it.Lang} : không nguồn nào có phụ đề.");
            if (Ss is not null && v.Entry is { Count: > 0 } && !v.LangReported)
            {
                var summ = SubSource.LangSummary(Ss.ListAll(Py.ToInt(v.Entry.Get("movieId"))));
                Job.Dim($"        (SubSource có: {string.Join(", ", summ)})");
                v.LangReported = true;
            }
            it.Candidates = [];
            return [];
        }
        var before = cands.Count;
        if (c.Bool("StrictFpsMatch"))
            cands = cands.Where(x => x.Tier != 3).ToList();
        if (!c.Bool("AllowUnknownFps"))
            cands = cands.Where(x => x.Tier != 2).ToList();
        if (before - cands.Count > 0)
            Job.Dim(string.Create(CultureInfo.InvariantCulture, $"    [i] {it.Lang} : loại {before - cands.Count} bản lệch framerate."));
        if (cands.Count == 0)
        {
            it.Status = "mọi bản đều lệch framerate";
            Job.Warn(string.Create(CultureInfo.InvariantCulture, $"    [-] {it.Lang} : có {before} bản nhưng đều lệch framerate."));
        }
        it.Candidates = [.. SubFetch.SortCandidates(cands).Take(c.Int("MaxShowCandidates"))];
        return it.Candidates;
    }

    // ------------------------------------------------------------------ 3. chon

    /// <summary><c>auto_select</c>: phần không cần hỏi của <c>Select-Candidate</c>. <c>null</c> = cần bạn chọn trên bảng.</summary>
    public Candidate? AutoSelect(FetchItem it)
    {
        if (it.Candidates.Count == 0 || it.Chosen is not null || it.Skip)
            return it.Chosen;
        var c = Cfg;
        if (c.Bool("RememberChoice") && Memory.Recall(it.MemoryKey, it.Video.Stem, it.Candidates) is { } r)
        {
            (it.Chosen, it.ChoiceReason) = (r, "dùng lại lựa chọn trước");
            return r;
        }
        if (!c.Bool("Interactive"))
        {
            (it.Chosen, it.ChoiceReason) = (it.Candidates[0], "tự động (bản đứng đầu)");
            return it.Chosen;
        }
        if (c.Bool("AutoPickWhenSingle") && it.Candidates.Count == 1)
        {
            Job.Info("  [i] Chỉ có 1 ứng viên → lấy luôn.");
            (it.Chosen, it.ChoiceReason) = (it.Candidates[0], "chỉ có 1 ứng viên");
            if (c.Bool("RememberChoice"))
                Memory.Remember(it.MemoryKey, it.Video.Stem, it.Chosen);
            return it.Chosen;
        }
        return null;
    }

    /// <summary><c>choose</c>: bạn chọn một ứng viên (bấm đúp trên bảng). <paramref name="remember"/> = ô "Dùng lại lựa chọn này…".</summary>
    public void Choose(FetchItem it, Candidate cand, bool? remember = null)
    {
        (it.Chosen, it.Skip, it.ChoiceReason) = (cand, false, "bạn chọn");
        if (remember ?? Cfg.Bool("RememberChoice"))
            Memory.Remember(it.MemoryKey, it.Video.Stem, cand);
    }

    /// <summary><c>skip</c>: nút "Bỏ qua ngôn ngữ này".</summary>
    public void Skip(FetchItem it) => (it.Skip, it.Chosen, it.Status) = (true, null, "bỏ qua");

    /// <summary><c>prefetch</c>: tải trước gói SubSource (miễn phí) để đo thẳng file của từng tập trong gói.</summary>
    public bool Prefetch(Candidate cand)
    {
        if (cand.Src != "SubSource" || Ss is null)
            return false;
        return Ss.DownloadPack(Py.ToInt(cand.PayloadDict.Get("subtitleId"))) is not null;
    }

    /// <summary><c>remeasure</c>: gom lại ứng viên (đo lại) cho các mục chưa chạy — dùng sau <see cref="Prefetch"/>.</summary>
    public void Remeasure(IEnumerable<FetchItem>? items = null)
    {
        foreach (var it in (items ?? Items).ToList())
        {
            if (it.Result is null && it.Already is null && it.Existing is null && !it.Skip)
            {
                var keep = it.Chosen;
                Gather(it);
                if (keep is not null)
                    it.Chosen = it.Candidates.FirstOrDefault(x => x.Ident == keep.Ident) ?? keep;
            }
        }
    }

    /// <summary><c>plan_all</c>: lập trọn kế hoạch: quét → gom → tự chọn; <paramref name="ask"/> được gọi cho mục cần bạn chọn
    /// (<c>null</c> = bỏ qua ngôn ngữ đó). Dùng cho chế độ tự động và cho test.</summary>
    public List<FetchItem> PlanAll(Func<FetchItem, Candidate?>? ask = null)
    {
        if (Items.Count == 0)
            Scan();
        foreach (var it in Items)
        {
            Job.Check();
            if (it.Already is not null || it.Existing is not null)
                continue;
            Gather(it);
            if (it.Candidates.Count == 0)
                continue;
            if (AutoSelect(it) is null)
            {
                var pick = ask?.Invoke(it);
                if (pick is null)
                    Skip(it);
                else
                    Choose(it, pick);
            }
            if (it.Chosen is { Src: "SubSource", IsPack: true } ch && Ss is not null && !Ss.IsDownloaded(Py.ToInt(ch.PayloadDict.Get("subtitleId"))))
                Prefetch(ch);
        }
        return Items;
    }

    // ------------------------------------------------------------------ 4. thuc thi

    /// <summary><c>_fetch</c> = <c>Get-ChosenSubtitle</c>: lấy về MỘT file (chưa đặt vào đích).</summary>
    private string? Fetch(FetchItem it, Candidate cd)
    {
        switch (cd.Src)
        {
            case "SubSource":
            {
                if (Ss is null)
                    return null;
                var d = Ss.DownloadPack(Py.ToInt(cd.PayloadDict.Get("subtitleId")));
                if (d is null)
                    return null;
                var f = PackFile(it.Video, d);
                if (f is null)
                    Job.Warn("      (gói này không có tập cần tìm)");
                return f;
            }
            case "Titulky":
            {
                if (Tt is null)
                    return null;
                var d = Tt.Download((SearchRow)cd.Payload!, Captcha);
                if (d is null)
                    return null;
                var files = Tt.SubtitleFiles(d);
                if (files.Count == 0)
                {
                    Job.Warn("      (file Titulky tải về không phải .srt / .ass / .ssa — MuxStudio không dùng được loại này)");
                    return null;
                }
                if (files.Count == 1)
                    return files[0];
                var f = PackFile(it.Video, d);
                if (f is null)
                    Job.Warn("      (gói này không có tập cần tìm)");
                return f;
            }
            case "OpenSub":
                return Os?.Download(cd.PayloadDict!);
            default:
                return null;
        }
    }

    /// <summary><c>report_timing</c> = <c>Report-Timing</c>: CHỈ BÁO CÁO, không sửa gì. Trả (mức, chữ, số mốc khớp; -1 = không đo).</summary>
    public (string Level, string Note, int Votes) ReportTiming(string path, RefInfo? refInfo)
    {
        if (!Cfg.Bool("VerifyTiming") || refInfo is null)
            return ("", "", -1);
        var t = Srt.CueTimes(path);
        if (t is null || t.Count < 30)
            return ("", "", -1);
        var rel = Timing.TimingRelationOf(refInfo.Times, t, Cfg.Int("FpsSampleRef"), Cfg.Int("FpsSampleTgt"));
        if (rel is null)
            return ("", "", -1);
        if (rel.Votes < Cfg.Int("MinVerifyVotes"))
            return ("warn", string.Create(CultureInfo.InvariantCulture, $"Kiểm chứng yếu: chỉ {rel.Votes} mốc khớp - kiểm tra kỹ khi xem."), rel.Votes);
        if (rel.FpsMismatch)
            return ("warn", $"Đo được LỆCH FPS ({rel.Name}) - bước ② sẽ cần sửa khi đồng bộ.", rel.Votes);
        var sign = rel.Offset >= 0 ? "+" : "";
        return ("ok", $"Khớp thời gian, lệch {sign}{Py.Str(Py.Round(rel.Offset, 2))}s (bước ② sẽ đồng bộ)", rel.Votes);
    }

    /// <summary><c>execute</c>: chạy đúng kế hoạch. Ghi nhật ký hoàn tác + báo cáo. Bước ① không sửa thời gian phụ đề.</summary>
    public FetchReport Execute(IEnumerable<FetchItem>? items = null)
    {
        var list = (items ?? Items).ToList();
        var c = Cfg;
        var rep = new FetchReport();
        var log = new RunLog(Td, c.Str("SubFetchLogDir"), "subfetch", c.Int("KeepUndoRuns"));
        var rows = new List<OrderedDictionary<string, string>>();
        KeepRefs(list.Select(i => i.Video).Distinct(), log);
        for (var n = 0; n < list.Count; n++)
        {
            var it = list[n];
            if (Job.ShouldStop())
            {
                rep.Stopped = true;
                break;
            }
            Job.Progress(n + 1, list.Count, $"{Subprep.Name(it.Video.Path)} [{it.Lang}]");
            if (it.Already is not null)
            {
                rep.Already++;
                continue;
            }
            if (it.Existing is not null)
            {
                ApplyExisting(it, log);
                rep.UsedExisting++;
                continue;
            }
            if (it.Skip && it.Candidates.Count > 0)
            {
                rep.Skipped++;
                Job.Dim($"    [>] Bỏ qua {it.Lang}: {Subprep.Name(it.Video.Path)}");
                continue;
            }
            if (it.Chosen is null)
            {
                rep.Missing++;
                continue;
            }
            if (FetchItemCore(it, log, rows))
                rep.Ok++;
            else
                rep.Missing++;
        }
        rep.LogPath = log.Save();
        if (rows.Count > 0)
            rep.ReportPath = SaveReport(rows);
        if (Os is not null)
            (rep.OsRemaining, rep.OsAllowed, rep.OsReset) = (Os.Remaining, Os.Allowed, Os.ResetMsg);
        Job.Head(string.Create(CultureInfo.InvariantCulture,
            $"TỔNG KẾT: {rep.Ok} phụ đề đã tải | {rep.UsedExisting} dùng file có sẵn | {rep.Missing} không tìm được")
            + (rep.Skipped > 0 ? string.Create(CultureInfo.InvariantCulture, $" | {rep.Skipped} bỏ qua") : ""));
        if (Os is not null && rep.OsRemaining >= 0)
        {
            var lvl = rep.OsRemaining <= c.Int("OsWarnRemaining") ? "warn" : "info";
            Job.Log(string.Create(CultureInfo.InvariantCulture, $"OpenSubtitles: còn {rep.OsRemaining}/{rep.OsAllowed} lượt tải hôm nay"), lvl);
        }
        if (rep.LogPath is not null)
            Job.Info($"Nhật ký thao tác (để hoàn tác): {c.Str("SubFetchLogDir")}\\{Subprep.Name(rep.LogPath)}");
        return rep;
    }

    /// <summary><c>_keep_refs</c>: giữ phụ đề gốc vừa trích vào <c>srt_goc</c> cho bước ② (§6.11).</summary>
    private void KeepRefs(IEnumerable<VideoInfo> videos, RunLog log)
    {
        foreach (var v in videos)
        {
            if (v.Ref?.Pending is not { } pend)
                continue;
            var (tmp, final) = pend;
            if (File.Exists(final) || Directory.Exists(final) || !File.Exists(tmp))
                continue;
            var parent = Subprep.Parent(final);
            var newDir = !Directory.Exists(parent) && !File.Exists(parent);
            Tools.CopyFile(tmp, final);
            if (newDir)
                log.Add(Undo.Mkdir, "", parent);
            log.Add(Undo.Create, v.Path, final, "phụ đề gốc");
            v.Ref = new RefInfo(v.Ref.Times, final, v.Ref.Source, null);
            Job.Dim($"    Giữ phụ đề gốc: {Cfg.Str("ExtractFolder")}\\{Subprep.Name(final)}");
        }
    }

    private static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    private static void EnsureDir(string d, RunLog log)
    {
        if (Exists(d))
            return;
        var missing = new List<string>();
        var p = d;
        while (!Exists(p))
        {
            missing.Add(p);
            var parent = Path.GetDirectoryName(p);
            if (string.IsNullOrEmpty(parent) || parent == p)
                break;
            p = parent;
        }
        Directory.CreateDirectory(d);
        for (var i = missing.Count - 1; i >= 0; i--)
            log.Add(Undo.Mkdir, "", missing[i]);
    }

    private void ApplyExisting(FetchItem it, RunLog log)
    {
        var m = it.Existing!;
        var dest = it.Dest(Subprep.Suffix(m.File));
        if (Py.NormCase(Py.AbsPath(m.File)) == Py.NormCase(Py.AbsPath(dest)))
            return;
        EnsureDir(Subprep.Parent(dest), log);
        if (Cfg.Str("ExistingRenameMode") == "move")
        {
            File.Move(m.File, dest, overwrite: true);
            log.Add(Undo.Move, m.File, dest, m.Reason);
        }
        else
        {
            Tools.CopyFile(m.File, dest); // CHEP, khong di chuyen (§6.11)
            log.Add(Undo.Copy, m.File, dest, m.Reason);
        }
        (it.Result, it.ResultSrc) = (dest, "có sẵn");
        Job.Ok($"    [=] {it.Lang} : {Subprep.Name(m.File)} → {Subprep.Name(dest)}");
    }

    private bool FetchItemCore(FetchItem it, RunLog log, List<OrderedDictionary<string, string>> rows)
    {
        var c = Cfg;
        var chosen = it.Chosen!;
        var tryList = new List<Candidate> { chosen };
        tryList.AddRange(it.Candidates.Where(x => x.Ident != chosen.Ident));
        foreach (var cd in tryList)
        {
            Job.Check();
            if (ReferenceEquals(cd, chosen))
                Job.Info($"    [~] Đang lấy: [{cd.Src}] {cd.Release}");
            else
                Job.Warn($"    [~] Thử bản khác: [{cd.Src}] {cd.Release}");
            var got = Fetch(it, cd);
            if (got is null)
            {
                if (c.Bool("RememberChoice"))
                    Memory.Forget(it.MemoryKey, cd.Release);
                continue;
            }
            var (lvl, note, votes) = ReportTiming(got, it.Video.Ref);
            if (cd.Src == "OpenSub" && !c.Bool("KeepPaidDownloads") && lvl == "warn" && votes >= 0 && votes < c.Int("MinVerifyVotes"))
            {
                Trash(got, c.Bool("DeleteToRecycleBin"));
                Job.Warn($"      [!] {note} → bỏ file (KeepPaidDownloads tắt), thử bản kế tiếp.");
                continue;
            }
            var suf = Subprep.Suffix(got);
            var dest = it.Dest(SubFetch.SubExts.Contains(suf.ToLowerInvariant()) ? suf : ".srt");
            EnsureDir(Subprep.Parent(dest), log);
            Tools.CopyFile(got, dest); // chep nguyen byte: KHONG doi thoi gian, KHONG doi ma
            log.Add(Undo.Create, cd.Release, dest, cd.Src);
            (it.Result, it.ResultSrc, it.Chosen, it.TimingNote) = (dest, cd.Src, cd, note);
            Job.Ok($"      → ĐÃ LƯU: {Path.GetRelativePath(Td, dest)}");
            if (note.Length > 0)
                Job.Log($"      [{(lvl == "ok" ? "v" : "!")}] {note}", lvl == "ok" ? "ok" : "warn");
            rows.Add(new OrderedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["Video"] = Subprep.Name(it.Video.Path), ["Lang"] = it.Lang, ["Nguon"] = cd.Src, ["Release"] = cd.Release,
                ["Score"] = Py.Str(Py.Round(cd.Score, 2)), ["Fps"] = cd.Fps, ["Lech"] = cd.OffsetLabel, ["KiemChung"] = note,
            });
            return true;
        }
        it.Status = "lấy thất bại";
        Job.Warn($"    [-] {it.Lang} : lấy thất bại.");
        return false;
    }

    private string SaveReport(List<OrderedDictionary<string, string>> rows)
    {
        var d = Py.PathJoin(Td, Cfg.Str("SubFetchLogDir"));
        Directory.CreateDirectory(d);
        var p = Py.PathJoin(d, $"fetch_{DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}.csv");
        var sb = new StringBuilder();
        PyCsv.WriteRowQuoteAll(sb, rows[0].Keys);
        foreach (var r in rows)
            PyCsv.WriteRowQuoteAll(sb, rows[0].Keys.Select(k => r.GetValueOrDefault(k, "")));
        File.WriteAllText(p, sb.ToString(), new UTF8Encoding(true));
        return p;
    }

    // ------------------------------------------------------------------ don dep

    /// <summary><c>close</c>: xoá thư mục tạm.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(Temp, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
