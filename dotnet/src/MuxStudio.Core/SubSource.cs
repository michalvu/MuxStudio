using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using JDict = System.Collections.Generic.OrderedDictionary<string, object?>;

namespace MuxStudio.Core;

/// <summary>
/// SubSource API (§6.11) — port <c>core/subsource.py</c>. Thay <c>Invoke-Sub</c>, <c>Find-Entry</c>, <c>Get-AllSubs</c>,
/// <c>Test-SubLang</c>, <c>Get-SubList</c>, <c>Get-LangSummary</c>, <c>Get-SubtitlePack</c>, <c>Select-FromPack</c> (SubFetch).
/// <list type="bullet">
/// <item>Gốc <c>https://api.subsource.net/api/v1</c>, header <c>X-API-Key</c>; 60 lượt/phút → nghỉ ~1,1 s.</item>
/// <item>Tìm: <c>/movies/search?searchType=imdb&amp;imdb=tt…</c> (ưu tiên), dự phòng <c>searchType=text&amp;q=…</c>. Phim bộ: MỖI SEASON
/// là một <c>movieId</c> riêng, có trường <c>season</c>.</item>
/// <item>Danh sách: <c>/subtitles?movieId=…&amp;page=N&amp;limit=100</c> (bị từ chối → 20), tối đa <c>MaxPages</c> trang. KHÔNG lọc phía
/// máy chủ (bản người dùng tự đăng để trống các trường lọc nên bị loại oan).</item>
/// <item>Tải: <c>/subtitles/{id}/download</c> → ZIP (gói season có nhiều tập).</item>
/// </list>
/// </summary>
public sealed partial class SubSourceClient
{
    public const string DefaultBase = "https://api.subsource.net/api/v1";

    private static readonly string[] RateHeaderHints = ["ratelimit", "rate-limit", "retry-after"];

    private readonly Action<double> _sleep;
    private readonly Func<double> _clock;
    private double _last = -1e9;
    private readonly Dictionary<(string Title, int? Year, int? Season, string? Imdb), JDict?> _entryCache = [];
    private readonly Dictionary<int, List<JDict>> _listCache = [];
    private readonly Dictionary<int, string?> _zipCache = [];
    private string? _cacheDir;

    /// <param name="sleep">(cho test) thay <c>time.sleep</c>.</param>
    /// <param name="clock">(cho test) thay <c>time.monotonic</c> (giây).</param>
    public SubSourceClient(string? apiKey, string? @base = DefaultBase, int pauseMs = 1100, int maxPages = 10, Http? http = null,
                           Action<double>? sleep = null, Func<double>? clock = null, Job? job = null, string? cacheDir = null)
    {
        ApiKey = Py.StripWs(apiKey ?? "");
        Base = (string.IsNullOrEmpty(@base) ? DefaultBase : @base).TrimEnd('/');
        Pause = Math.Max(0, pauseMs) / 1000.0;
        MaxPages = Math.Max(1, maxPages);
        HttpClient = http ?? Http.Shared;
        Job = job ?? Job.Null;
        _sleep = sleep ?? (s => Http.Sleep(s, Job.Token));
        _clock = clock ?? (() => Environment.TickCount64 / 1000.0);
        _cacheDir = string.IsNullOrEmpty(cacheDir) ? null : cacheDir;
    }

    public string ApiKey { get; }

    public string Base { get; }

    public double Pause { get; }

    public int MaxPages { get; }

    public Http HttpClient { get; }

    public Job Job { get; set; }

    public int Calls { get; private set; }

    public int LastStatus { get; private set; }

    public OrderedDictionary<string, string> LastHeaders { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Usable => ApiKey.Length > 0;

    /// <summary>(cho test) <c>_list_cache</c>.</summary>
    internal Dictionary<int, List<JDict>> ListCache => _listCache;

    // ------------------------------------------------------------------ nhip goi

    /// <summary><c>_pace</c>: nghỉ giữa các lượt (trần 60/phút). Lượt đầu không nghỉ.</summary>
    private void Pace()
    {
        if (Calls > 0)
        {
            var wait = Pause - (_clock() - _last);
            if (wait > 0)
                _sleep(wait);
        }
        Calls++;
        _last = _clock();
    }

    private void KeepHeaders(HttpResult r)
    {
        LastStatus = r.StatusCode;
        var h = new OrderedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in r.Headers)
        {
            var low = k.ToLowerInvariant();
            if (RateHeaderHints.Any(x => low.Contains(x, StringComparison.Ordinal)))
                h[k] = v;
        }
        LastHeaders = h;
    }

    private List<KeyValuePair<string, string>> KeyHeader => [new("X-API-Key", ApiKey)];

    // ------------------------------------------------------------------ goi API

    [GeneratedRegex(@"\s+")]
    private static partial Regex WsRe();

    /// <summary><c>get</c> = <c>Invoke-Sub</c>: 429 → chờ 20 s; 4xx → báo và dừng; lỗi mạng → thử 3 lần.</summary>
    public object? Get(string path, IEnumerable<KeyValuePair<string, object?>>? parameters = null)
    {
        if (!Usable)
            return null;
        var q = new List<KeyValuePair<string, string?>>();
        foreach (var (k, v) in parameters ?? [])
        {
            if (v is not null && Py.Str(v) is { Length: > 0 } s)
                q.Add(new(k, s));
        }
        Pace();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            HttpResult r;
            try
            {
                r = HttpClient.Request("GET", Base + path, q, KeyHeader, timeout: 25, ct: Job.Token);
            }
            catch (HttpNetworkException e)
            {
                if (attempt < 3)
                    _sleep(2 * attempt);
                else
                    Job.Error($"    [!] Lỗi mạng: {e.Message}");
                continue;
            }
            KeepHeaders(r);
            if (r.StatusCode == 200)
                return r.JsonOrNull();
            if (r.StatusCode == 429)
            {
                Job.Warn("    [!] Chạm trần rate limit, chờ 20 giây...");
                _sleep(20);
                continue;
            }
            if (r.StatusCode is >= 400 and < 500)
            {
                var msg = Py.Slice(WsRe().Replace(r.Text, " "), 0, 300);
                Job.Warn(string.Create(CultureInfo.InvariantCulture, $"    [!] API {r.StatusCode} : {msg}"));
                return null;
            }
            if (attempt < 3)
                _sleep(2 * attempt);
            else
                Job.Error(string.Create(CultureInfo.InvariantCulture, $"    [!] Lỗi máy chủ SubSource ({r.StatusCode})"));
        }
        return null;
    }

    private static KeyValuePair<string, object?> P(string k, object? v) => new(k, v);

    private static List<JDict>? DataOf(object? r) =>
        r is JDict d && d.Count > 0 && Py.Truthy(d.Get("data")) ? d.GetList("data").OfType<JDict>().ToList() : null;

    // ------------------------------------------------------------------ tim muc phim

    /// <summary><c>find_entry</c> = <c>Find-Entry</c>: tra IMDb trước, rồi theo tên; lọc season (phim bộ) / năm ±1 (phim lẻ);
    /// còn nhiều thì lấy giống tên nhất.</summary>
    public JDict? FindEntry(string cleanTitle, int? year, int? season, string? imdb = null)
    {
        var key = (cleanTitle, year, season, imdb);
        if (_entryCache.TryGetValue(key, out var hit))
            return hit;
        var cands = new List<JDict>();
        if (!string.IsNullOrEmpty(imdb) && DataOf(Get("/movies/search", [P("searchType", "imdb"), P("imdb", imdb)])) is { } d1)
            cands = d1;
        if (cands.Count == 0 && !string.IsNullOrEmpty(cleanTitle)
            && DataOf(Get("/movies/search", [P("searchType", "text"), P("q", cleanTitle)])) is { } d2)
            cands = d2;
        if (cands.Count == 0)
        {
            _entryCache[key] = null;
            return null;
        }
        if (season is { } se)
        {
            var bySeason = cands.Where(c => IntOf(c.Get("season")) == se).ToList();
            if (bySeason.Count == 0)
            {
                var have = cands.Where(c => c.Get("season") is not null).Select(c => Py.Str(c.Get("season"))).Distinct(StringComparer.Ordinal)
                                .OrderBy(x => x, StringComparer.Ordinal);
                Job.Warn(string.Create(CultureInfo.InvariantCulture, $"    [!] Không có season {se} trên SubSource (có: {string.Join(", ", have)})"));
                _entryCache[key] = null;
                return null;
            }
            cands = bySeason;
        }
        else if (year is { } y && y != 0)
        {
            var byYear = cands.Where(c => IntOf(c.Get("releaseYear")) != 0 && Math.Abs(IntOf(c.Get("releaseYear")) - y) <= 1).ToList();
            if (byYear.Count > 0)
                cands = byYear;
        }
        var want = Naming.TitleTokens(cleanTitle);
        JDict? best = null;
        var bestScore = -1.0;
        foreach (var c in cands)
        {
            var s = Math.Max(Naming.TitleScore(want, Naming.TitleTokens(c.GetStr("title"))),
                             Naming.TitleScore(want, Naming.TitleTokens(c.GetStr("alternateTitle"))));
            if (s > bestScore)
            {
                best = c;
                bestScore = s;
            }
        }
        _entryCache[key] = best;
        return best;
    }

    // ------------------------------------------------------------------ danh sach phu de

    /// <summary><c>list_all</c> = <c>Get-AllSubs</c>: lấy TOÀN BỘ phụ đề của một <c>movieId</c>, không lọc gì phía máy chủ.</summary>
    public List<JDict> ListAll(int movieId)
    {
        if (_listCache.TryGetValue(movieId, out var hit))
            return hit;
        var outp = new List<JDict>();
        long limit = 100;
        for (var page = 1; page <= MaxPages; page++)
        {
            var r = Get("/subtitles", [P("movieId", movieId), P("page", page), P("limit", limit)]);
            var data = DataOf(r);
            if (data is null)
            {
                if (page == 1 && limit == 100)
                {
                    limit = 20; // may chu co the khong nhan limit=100
                    r = Get("/subtitles", [P("movieId", movieId), P("page", 1), P("limit", limit)]);
                    data = DataOf(r);
                    if (data is null)
                        break;
                }
                else
                {
                    break;
                }
            }
            outp.AddRange(data);
            var pg = ((JDict)r!).Get("pagination");
            if (!Py.Truthy(pg))
                break;
            if (page >= IntOf(PyDict.AsDict(pg).Get("pages")))
                break;
        }
        _listCache[movieId] = outp;
        return outp;
    }

    /// <summary><c>list_for_lang</c>.</summary>
    public List<JDict> ListForLang(int movieId, string code) => ListAll(movieId).Where(s => LangMatches(s, code)).ToList();

    // ------------------------------------------------------------------ tai goi ZIP

    private string WorkDir()
    {
        _cacheDir ??= Directory.CreateTempSubdirectory("muxstudio_subsource_").FullName;
        Directory.CreateDirectory(_cacheDir);
        return _cacheDir;
    }

    public bool IsDownloaded(int subId) => _zipCache.TryGetValue(subId, out var d) && !string.IsNullOrEmpty(d);

    public string? PackDir(int subId) => _zipCache.GetValueOrDefault(subId);

    /// <summary><c>download_pack</c> = <c>Get-SubtitlePack</c>: tải ZIP MỘT lần (cache theo lượt), giải nén, trả thư mục.</summary>
    public string? DownloadPack(int subId)
    {
        if (_zipCache.TryGetValue(subId, out var hit))
            return hit;
        var work = WorkDir();
        var id = subId.ToString(CultureInfo.InvariantCulture);
        var zpath = Py.PathJoin(work, id + ".zip");
        var outDir = Py.PathJoin(work, id);
        Pace();
        byte[] data;
        try
        {
            var r = HttpClient.Request("GET", $"{Base}/subtitles/{id}/download", headers: KeyHeader, timeout: 90, ct: Job.Token);
            KeepHeaders(r);
            if (r.StatusCode != 200)
                throw new IOException(string.Create(CultureInfo.InvariantCulture, $"HTTP {r.StatusCode}"));
            data = r.Content;
        }
        catch (Exception e) when (e is IOException or HttpNetworkException or UnauthorizedAccessException)
        {
            Job.Error($"      [!] Tải thất bại (id={id}): {e.Message}");
            _zipCache[subId] = null;
            return null;
        }
        if (data.Length < 4)
        {
            _zipCache[subId] = null;
            return null;
        }
        if (data[0] != (byte)'P' || data[1] != (byte)'K')
        {
            var magic = data[0] == (byte)'R' ? "RAR" : data[0] == (byte)'7' ? "7Z" : "không rõ";
            Job.Warn($"      [!] File tải về không phải ZIP ({magic}) — cần 7-Zip, bỏ qua.");
            _zipCache[subId] = null;
            return null;
        }
        try
        {
            File.WriteAllBytes(zpath, data);
            SubSource.SafeExtract(zpath, outDir);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Job.Error($"      [!] Giải nén lỗi: {e.Message}");
            _zipCache[subId] = null;
            return null;
        }
        _zipCache[subId] = outDir;
        return outDir;
    }

    // ------------------------------------------------------------------ kiem tra ket noi

    /// <summary><c>check</c>.</summary>
    public ConnStatus Check()
    {
        if (ApiKey.Length == 0)
            return new ConnStatus(null, "SubSource: chưa điền key");
        var r = Get("/movies/search", [P("searchType", "text"), P("q", "The Matrix")]);
        if (r is null)
        {
            if (LastStatus is 401 or 403)
                return new ConnStatus(false, string.Create(CultureInfo.InvariantCulture, $"SubSource: key bị từ chối ({LastStatus})"));
            return new ConnStatus(false, "SubSource: không kết nối được");
        }
        var rem = SubSource.RateText(LastHeaders);
        return new ConnStatus(true, "SubSource: key hợp lệ" + (rem.Length > 0 ? $", {rem}" : ""));
    }

    /// <summary><c>_int</c>: <c>int(v)</c>, lỗi → 0.</summary>
    internal static int IntOf(object? v)
    {
        try
        {
            return Py.ToInt(v);
        }
        catch (Exception e) when (e is InvalidCastException or FormatException or OverflowException)
        {
            return 0;
        }
    }

    /// <summary><c>lang_matches</c> (gọi tắt).</summary>
    public static bool LangMatches(JDict sub, string code) => SubSource.LangMatches(sub, code);
}

/// <summary>Hàm không cần mạng của <c>core/subsource.py</c>.</summary>
public static partial class SubSource
{
    public static readonly IReadOnlyList<string> SubExts = [".srt", ".ass", ".ssa"];

    /// <summary><c>_rate_text</c>: header giới hạn lượt → <c>còn 59/60 lượt/phút, 1794/1800 lượt/giờ, 7194/7200 lượt/ngày</c>.
    /// Tên header thật (dò 2026-11-09): <c>X-RateLimit-Limit</c> / <c>-Remaining</c> (theo phút), <c>-Hour</c>, <c>-Day</c>.</summary>
    public static string RateText(IEnumerable<KeyValuePair<string, string>> h)
    {
        var low = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in h)
            low[k.ToLowerInvariant()] = v;
        var parts = new List<string>();
        foreach (var (suf, unit) in new[] { ("", "phút"), ("-hour", "giờ"), ("-day", "ngày") })
        {
            var rem = low.GetValueOrDefault($"x-ratelimit-remaining{suf}");
            var lim = low.GetValueOrDefault($"x-ratelimit-limit{suf}");
            if (!string.IsNullOrEmpty(rem) && !string.IsNullOrEmpty(lim))
                parts.Add($"{rem}/{lim} lượt/{unit}");
        }
        if (parts.Count > 0)
            return "còn " + string.Join(", ", parts);
        var rem2 = low.FirstOrDefault(kv => kv.Key.Contains("remaining", StringComparison.Ordinal)).Value; // ten header khac (du phong)
        var lim2 = low.FirstOrDefault(kv => kv.Key.EndsWith("limit", StringComparison.Ordinal) && !kv.Key.Contains("remaining", StringComparison.Ordinal)).Value;
        if (!string.IsNullOrEmpty(rem2) && !string.IsNullOrEmpty(lim2))
            return $"còn {rem2}/{lim2} lượt";
        if (!string.IsNullOrEmpty(rem2))
            return $"còn {rem2} lượt";
        return "";
    }

    // Ten ngon ngu SubSource that (do 2026-11-09): "chinese_bg_code" (gian the), "big_5_code" (phon the),
    // "portuguese" + "brazilian_portuguese", "farsi_persian"...
    private static readonly Dictionary<string, string[]> LangPrefix = new(StringComparer.Ordinal)
    {
        ["vi"] = ["viet"],
        ["cs"] = ["czech", "cze"],
        ["en"] = ["english"],
        ["zh"] = ["chinese", "big_5", "big5"],
        ["pt"] = ["portuguese", "brazilian_portuguese", "brazillian_portuguese"],
    };

    /// <summary><c>lang_matches</c> = <c>Test-SubLang</c>: <c>language</c> của SubSource (tên tiếng Anh viết thường) có phải ngôn ngữ
    /// <paramref name="code"/>. <c>language</c> để trống → không nhận (như .bat).</summary>
    public static bool LangMatches(JDict sub, string code)
    {
        var v = Py.StripWs(sub.GetStr("language")).ToLowerInvariant();
        if (v.Length == 0)
            return false;
        var lg = Naming.GetLang(code);
        var names = new HashSet<string>(StringComparer.Ordinal) { code };
        if (lg is not null)
        {
            names.Add(lg.SubSource);
            names.Add(lg.Iso2B);
            names.Add(lg.Iso2T);
        }
        names.Remove("");
        if (names.Contains(v))
            return true;
        if (v.StartsWith(code + "-", StringComparison.Ordinal) || v.StartsWith(code + "_", StringComparison.Ordinal))
            return true;
        return LangPrefix.TryGetValue(code, out var ps) && ps.Any(p => v.StartsWith(p, StringComparison.Ordinal));
    }

    /// <summary><c>lang_summary</c> = <c>Get-LangSummary</c>: <c>english(40), vietnamese(3), (trống)(2)</c> — nhiều nhất trước.</summary>
    public static List<string> LangSummary(IEnumerable<JDict> subs)
    {
        var counts = new OrderedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in subs)
        {
            var v = Py.StripWs(s.GetStr("language")).ToLowerInvariant();
            if (v.Length == 0)
                v = "(trống)";
            counts[v] = counts.GetValueOrDefault(v) + 1;
        }
        return counts.OrderBy(kv => -kv.Value).Select(kv => string.Create(CultureInfo.InvariantCulture, $"{kv.Key}({kv.Value})")).ToList();
    }

    /// <summary><c>safe_extract</c>: giải nén ZIP, chặn đường dẫn thoát ra ngoài thư mục (zip-slip). Tên file trong ZIP đọc như
    /// <c>zipfile</c>: cờ UTF-8 → UTF-8, không có → cp437.</summary>
    public static void SafeExtract(string zpath, string outDir)
    {
        if (Directory.Exists(outDir))
            Directory.Delete(outDir, recursive: true);
        else if (File.Exists(outDir))
            File.Delete(outDir);
        Directory.CreateDirectory(outDir);
        var root = Py.NormCase(Py.AbsPath(outDir));
        TextEncoding.EnsureCodePages();
        using var z = ZipFile.Open(zpath, ZipArchiveMode.Read, Encoding.GetEncoding(437));
        foreach (var info in z.Entries)
        {
            var name = info.FullName.Replace('\\', '/');
            if (name.Length == 0 || name.EndsWith('/'))
                continue;
            var parts = name.Split('/').Where(p => p is not ("" or "." or "..")).ToArray();
            var target = Py.AbsPath(Py.Join(outDir, parts));
            if (!Py.NormCase(target).StartsWith(root + "\\", StringComparison.Ordinal))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var src = info.Open();
            using var dst = File.Create(target);
            src.CopyTo(dst);
        }
    }

    /// <summary><c>pack_files</c>: file phụ đề trong gói (đệ quy): file ở thư mục trên trước, theo tên.</summary>
    public static List<string> PackFiles(string? d)
    {
        var outp = new List<string>();
        if (string.IsNullOrEmpty(d) || !Directory.Exists(d))
            return outp;
        void Walk(string p)
        {
            var entries = new DirectoryInfo(p).EnumerateFileSystemInfos().OrderBy(e => e.Name.ToLowerInvariant(), StringComparer.Ordinal).ToList();
            foreach (var e in entries)
            {
                if ((e.Attributes & FileAttributes.Directory) == 0 && File.Exists(e.FullName)
                    && SubExts.Contains(Py.SplitExt(e.Name).Ext.ToLowerInvariant()))
                    outp.Add(Py.PathJoin(p, e.Name));
            }
            foreach (var e in entries)
            {
                if ((e.Attributes & FileAttributes.Directory) != 0 && Directory.Exists(e.FullName))
                    Walk(Py.Join(p, e.Name));
            }
        }
        Walk(d);
        return outp;
    }

    [GeneratedRegex(@"\b(forced|sdh|hi\b|hearing|cc\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForcedSdhRe();

    [GeneratedRegex(@"\b(forced)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForcedRe();

    [GeneratedRegex(@"\b(sdh|hi\b|hearing|cc\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SdhRe();

    /// <summary>
    /// <c>select_from_pack</c> = <c>Select-FromPack</c>: chọn ĐÚNG file trong gói. Bỏ file có <c>forced</c>/<c>sdh</c>/<c>hi</c>/
    /// <c>hearing</c>/<c>cc</c> trong tên; cả gói chỉ có loại đó → <c>null</c>. Có số tập → chỉ giữ file CÙNG TẬP (nhận cả số tuyệt
    /// đối, TĐ-02); không có → <c>null</c>.
    /// </summary>
    public static string? SelectFromPack(string? d, EpisodeInfo? ep, IReadOnlyList<string> videoTokens, bool excludeForced = true,
                                         bool excludeSdh = true)
    {
        if (string.IsNullOrEmpty(d))
            return null;
        var files = PackFiles(d);
        if (files.Count == 0)
            return null;
        var rx = (excludeForced, excludeSdh) switch
        {
            (true, true) => ForcedSdhRe(),
            (true, false) => ForcedRe(),
            (false, true) => SdhRe(),
            _ => null,
        };
        if (rx is not null)
        {
            var normal = files.Where(f => !rx.IsMatch(Subprep.Stem(f))).ToList();
            if (normal.Count == 0)
                return null;
            files = normal;
        }
        if (ep is not null)
        {
            var hit = files.Where(f => Episodes.SameEpisode(ep, Episodes.ParseEpisode(Subprep.Stem(f)))).ToList();
            if (hit.Count == 0)
                return null;
            files = hit;
        }
        if (files.Count == 1)
            return files[0];
        string? best = null;
        var bestScore = -1.0;
        foreach (var f in files)
        {
            var s = Naming.TitleScore(videoTokens, Naming.TitleTokens(Subprep.Stem(f)));
            if (s > bestScore)
            {
                best = f;
                bestScore = s;
            }
        }
        return best;
    }
}
