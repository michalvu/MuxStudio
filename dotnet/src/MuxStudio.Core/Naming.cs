using System.Globalization;
using System.Text.RegularExpressions;

namespace MuxStudio.Core;

/// <summary>Một ngôn ngữ trong bảng chung (<c>naming.Lang</c>).</summary>
/// <param name="Code">Mã 2 chữ: 'vi'.</param>
/// <param name="Iso2B">ISO 639-2/B: 'vie', 'cze', 'ger' (mkvmerge &lt; v50).</param>
/// <param name="Iso2T">ISO 639-2/T: 'vie', 'ces', 'deu'.</param>
/// <param name="Name">Tên tiếng Anh: 'Vietnamese' (đặt tên track).</param>
/// <param name="SubSource">Tên SubSource: 'vietnamese' ('' = SubSource không hỗ trợ).</param>
/// <param name="Aliases">Cách viết khác.</param>
/// <param name="Codepage">Bảng mã cũ khi file không phải UTF-8.</param>
/// <param name="Charset">Tên bảng mã cho mkvmerge <c>--sub-charset</c>.</param>
public sealed record Lang(string Code, string Iso2B, string Iso2T, string Name, string SubSource,
    IReadOnlyList<string> Aliases, int Codepage = 1252, string Charset = "windows-1252")
{
    public HashSet<string> Words()
    {
        var s = new HashSet<string>(StringComparer.Ordinal) { Code, Iso2B, Iso2T };
        foreach (var a in Aliases)
            s.Add(a);
        return s;
    }
}

/// <summary>Tên phim + năm tách từ tên release.</summary>
public sealed record ReleaseName(string Title, int? Year);

/// <summary>Kết quả tra một tập (<c>Resolve-TmdbEpisode</c> / <c>Resolve-TvdbEpisode</c>).</summary>
/// <param name="Mapped">Số tuyệt đối → season/tập của database.</param>
public sealed record EpisodeHit(int Season, int Episode, string? Title = null, bool Mapped = false, bool Found = false);

/// <summary>Một kết quả để người dùng chọn ở dòng "Cần chọn".</summary>
public sealed record NameCandidate(long Id, string Title, int? Year, string Original = "")
{
    public string Describe(string db) =>
        $"{Title} ({(Year is { } y && y != 0 ? y.ToString(CultureInfo.InvariantCulture) : "????")})  {db}id-{Id.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>
/// Tên: ngôn ngữ, từ khoá tên phim, tách tên + năm, tên file an toàn, tên chuẩn Jellyfin.
/// Port <c>core/naming.py</c> (phần không cần database; phần đặt tên theo TMDB/TVDB port ở GĐ2 cùng tmdb/tvdb).
/// </summary>
public static partial class Naming
{
    // ==================================================================
    //  NGÔN NGỮ
    // ==================================================================

    // Thứ tự = thứ tự ưu tiên khi nhận diện (giữ đúng thứ tự của Get-LangCode ở MuxTool,
    // rồi tới các ngôn ngữ còn lại của $LangMap trong SubFetch).
    private static readonly Lang[] LangList =
    [
        new("vi", "vie", "vie", "Vietnamese", "vietnamese", ["vn", "viet", "vietnamese", "tiengviet"], 1258, "windows-1258"),
        new("cs", "cze", "ces", "Czech", "czech", ["cz", "czech", "sec"], 1250, "windows-1250"),
        new("en", "eng", "eng", "English", "english", ["english", "anh"], 1252, "windows-1252"),
        new("ja", "jpn", "jpn", "Japanese", "japanese", ["jp", "japanese"], 932, "shift_jis"),
        new("ko", "kor", "kor", "Korean", "korean", ["korean"], 949, "euc-kr"),
        new("zh", "chi", "zho", "Chinese", "chinese_bg_code", ["chinese", "cmn", "yue"], 936, "gbk"), // SubSource thật
        new("fr", "fre", "fra", "French", "french", ["french"], 1252, "windows-1252"),
        new("de", "ger", "deu", "German", "german", ["german"], 1252, "windows-1252"),
        new("es", "spa", "spa", "Spanish", "spanish", ["spanish"], 1252, "windows-1252"),
        new("th", "tha", "tha", "Thai", "thai", ["thai"], 874, "windows-874"),
        new("id", "ind", "ind", "Indonesian", "indonesian", ["indonesian"], 1252, "windows-1252"),
        new("ar", "ara", "ara", "Arabic", "arabic", ["arabic"], 1256, "windows-1256"),
        new("pt", "por", "por", "Portuguese", "portuguese", ["portuguese"], 1252, "windows-1252"),
        new("ru", "rus", "rus", "Russian", "russian", ["russian"], 1251, "windows-1251"),
        new("it", "ita", "ita", "Italian", "italian", ["italian"], 1252, "windows-1252"),
        new("pl", "pol", "pol", "Polish", "polish", ["polish"], 1250, "windows-1250"),
        new("nl", "dut", "nld", "Dutch", "dutch", ["dutch"], 1252, "windows-1252"),
        new("tr", "tur", "tur", "Turkish", "turkish", ["turkish"], 1254, "windows-1254"),
    ];

    private static readonly Lock LangLock = new();

    /// <summary>Mã 2 chữ → Lang (giữ thứ tự ưu tiên).</summary>
    public static OrderedDictionary<string, Lang> Langs { get; } = BuildLangs();

    private static readonly Dictionary<string, string> WordToCode = new(StringComparer.Ordinal);

    private static OrderedDictionary<string, Lang> BuildLangs()
    {
        var d = new OrderedDictionary<string, Lang>(StringComparer.Ordinal);
        foreach (var lg in LangList)
            d[lg.Code] = lg;
        return d;
    }

    static Naming() => RebuildIndex();

    private static void RebuildIndex()
    {
        WordToCode.Clear();
        foreach (var lg in Langs.Values)
        {
            foreach (var w in lg.Words())
                WordToCode.TryAdd(w.ToLowerInvariant(), lg.Code);
        }
    }

    /// <summary>12 ngôn ngữ có ô tick trong SubFetch (<c>$Lang_vi</c> … <c>$Lang_ar</c>), đúng thứ tự.</summary>
    public static readonly IReadOnlyList<string> FetchLangSwitches =
        ["vi", "en", "cs", "ja", "ko", "zh", "fr", "de", "es", "th", "id", "ar"];

    [GeneratedRegex(@"^[a-z]{2,3}\z")]
    private static partial Regex LangCodeRe();

    /// <summary>Thêm mã ngôn ngữ lạ (từ <c>LangExtra</c> / cấu hình) để mọi bước nhận ra nó.</summary>
    public static void RegisterLanguage(string? code)
    {
        var c = (code ?? "").Trim().ToLowerInvariant();
        lock (LangLock)
        {
            if (c.Length == 0 || Langs.ContainsKey(c) || !LangCodeRe().IsMatch(c) || c.EndsWith('\n'))
                return;
            Langs[c] = new Lang(c, c, c, c.ToUpperInvariant(), "", []);
            RebuildIndex();
        }
    }

    /// <summary>Đưa bảng ngôn ngữ về mặc định (chỉ dùng trong test: Python chạy mỗi test file trong cùng tiến trình theo thứ tự).</summary>
    internal static void ResetLanguages()
    {
        lock (LangLock)
        {
            Langs.Clear();
            foreach (var lg in LangList)
                Langs[lg.Code] = lg;
            RebuildIndex();
        }
    }

    public static Lang? GetLang(string? code)
    {
        var c = (code ?? "").Trim().ToLowerInvariant().TrimStart('.');
        return Langs.TryGetValue(c, out var lg) ? lg : null;
    }

    /// <summary>Một từ → mã 2 chữ, '' nếu không nhận ra (<c>ConvertTo-LangCode</c>/<c>ConvertTo-PoolLangCode</c>).</summary>
    public static string LangFromWord(string? w)
    {
        if (string.IsNullOrEmpty(w))
            return "";
        var x = w.Trim().ToLowerInvariant();
        return WordToCode.TryGetValue(x, out var c) ? c : "";
    }

    [GeneratedRegex("[a-z0-9]+")]
    private static partial Regex AlnumLowerRe();

    /// <summary>
    /// Ngôn ngữ của track MKV → mã 2 chữ (<c>Get-LangCode</c>).
    /// Xét chuỗi <c>"&lt;language&gt; &lt;language_ietf&gt;"</c> theo thứ tự ưu tiên của bảng.
    /// Mã lạ trả nguyên (chữ thường); không có/<c>und</c> → <c>'und'</c>.
    /// </summary>
    public static string LangFromTrack(string? language, string? languageIetf = null)
    {
        var s = $"{language ?? ""} {languageIetf ?? ""}".ToLowerInvariant();
        var toks = AlnumLowerRe().Matches(s).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var lg in Langs.Values)
        {
            if (toks.Overlaps(lg.Words()))
                return lg.Code;
        }
        var lang = (language ?? "").Trim();
        if (lang.Length > 0 && lang.ToLowerInvariant() != "und")
            return lang.ToLowerInvariant();
        return "und";
    }

    /// <summary>Tên tiếng Anh để đặt tên track (<c>Get-LangName</c>). <c>und</c> → ''.</summary>
    public static string LangName(string? code)
    {
        var c = (code ?? "").Trim().ToLowerInvariant();
        if (c is "" or "und")
            return "";
        return Langs.TryGetValue(c, out var lg) ? lg.Name : c.ToUpperInvariant();
    }

    /// <summary>Mã ngôn ngữ cho mkvmerge (<c>Get-LangForMux</c>): v50+ hiểu mã 2 chữ, bản cũ cần ISO 639-2/B.</summary>
    public static string LangForMux(string code, int mkvMajorVersion)
    {
        if (mkvMajorVersion >= 50)
            return code;
        return Langs.TryGetValue(code, out var lg) ? lg.Iso2B : code;
    }

    /// <summary>
    /// Ngôn ngữ <paramref name="tag"/> (vd 'cze cs', 'ger', 'vi') có phải <paramref name="want"/> không (thay <c>Test-LangMatch</c>).
    /// Sửa lỗi TĐ-04: chuẩn hoá cả hai phía về mã 2 chữ rồi so.
    /// </summary>
    public static bool LangMatch(string? tag, string? want)
    {
        if (string.IsNullOrEmpty(tag) || string.IsNullOrEmpty(want))
            return false;
        var w = want.Trim().ToLowerInvariant();
        var wantC = Or(LangFromWord(w), w);
        foreach (Match m in AlnumLowerRe().Matches(tag.ToLowerInvariant()))
        {
            if (Or(LangFromWord(m.Value), m.Value) == wantC)
                return true;
        }
        if (!Langs.ContainsKey(wantC)) // mã lạ: giữ cách so lỏng của .bat
        {
            var t = tag.Trim().ToLowerInvariant();
            if (t == w || (t.Length >= 2 && w.Length >= 2 && t[..2] == w[..2]))
                return true;
        }
        return false;
    }

    private static string Or(string a, string b) => a.Length > 0 ? a : b;

    private static bool KnownTag(string g) => WordToCode.ContainsKey(g.ToLowerInvariant()) && g.Length <= 3;

    [GeneratedRegex(@"\.([A-Za-z]{2,3})$")]
    private static partial Regex LangTagSuffixRe();

    /// <summary><c>"Phim.S01E01.vi"</c> → <c>("Phim.S01E01", "vi")</c> (<c>Split-LangTag</c>). Hậu tố chuẩn hoá: <c>cz</c> → <c>cs</c>.</summary>
    public static (string Base, string Lang) SplitLangTag(string @base)
    {
        var m = LangTagSuffixRe().Match(@base);
        if (m.Success && KnownTag(m.Groups[1].Value))
            return (@base[..m.Index], LangFromWord(m.Groups[1].Value));
        return (@base, "");
    }

    [GeneratedRegex(@"[\\/]")]
    private static partial Regex PathSepRe();

    [GeneratedRegex(@"[\._\-\[\(]([A-Za-z]{2,12})$")]
    private static partial Regex FileLangSuffixRe();

    [GeneratedRegex(@"[\._\-]")]
    private static partial Regex FileNamePartSepRe();

    /// <summary>
    /// Đoán ngôn ngữ một file phụ đề (<c>Get-FileLangTag</c> + <c>Get-PoolLang</c>). Trả mã hoặc ''.
    /// 1) Tên thư mục con CẤP 1 ngay dưới <paramref name="root"/> (vd <c>srt_can_sync\vi\...</c>) — chỉ cấp 1.
    /// 2) Hậu tố cuối tên file: <c>Phim.vi.srt</c>, <c>Phim_vietnamese.srt</c>, <c>Phim[cs].srt</c>…
    /// </summary>
    public static string LangTagOfFile(string path, string? root = null)
    {
        var full = path;
        var b = Py.SplitExt(Py.Basename(full)).Root;
        if (!string.IsNullOrEmpty(root))
        {
            var rel = full;
            if (rel.ToLowerInvariant().StartsWith(root.ToLowerInvariant(), StringComparison.Ordinal))
                rel = rel[root.Length..];
            var segs = PathSepRe().Split(rel).Where(s => s.Length > 0).ToList();
            if (segs.Count > 1)
            {
                var c = LangFromWord(segs[0]);
                if (c.Length > 0)
                    return c;
            }
        }
        var m = FileLangSuffixRe().Match(b);
        if (m.Success)
        {
            var c = LangFromWord(m.Groups[1].Value);
            if (c.Length > 0)
                return c;
        }
        var parts = FileNamePartSepRe().Split(b);
        if (parts.Length > 1)
        {
            var c = LangFromWord(parts[^1]);
            if (c.Length > 0)
                return c;
        }
        return "";
    }

    // ==================================================================
    //  TỪ KHOÁ TÊN PHIM + ĐIỂM GIỐNG NHAU
    // ==================================================================

    /// <summary>Từ kỹ thuật / ngôn ngữ / thể loại — không mang thông tin nhận dạng phim (bản SubPrep, ⊇ SubFetch).</summary>
    public static readonly IReadOnlySet<string> NoiseWords = new HashSet<string>(
        """
        bluray blu ray bdrip brrip bdremux remux webdl web dl webrip web hdtv dvdrip hdrip hddvd
        x264 x265 h264 h265 hevc avc xvid aac ac3 eac3 dd ddp dts dtshd truehd atmos flac opus mp3 mp4 mkv avi
        hdr dovi sdr imax proper repack extended uncut unrated remastered criterion multi dual vietsub subs sub
        subtitles bit amzn nf dsnp hmax atvp hulu pcok itunes internal uhd 4k dolby vision ma
        eng rus vie viet vietnamese cze czech ces cz cs en fra ger spa ita jpn kor chi
        action drama comedy horror thriller adventure animation
        """.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> StopWords = new HashSet<string>(
        "the of a an and in on at to for".Split(' '), StringComparer.Ordinal);

    [GeneratedRegex(@"\([^\)]*\)")]
    private static partial Regex ParenRe();

    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex BracketRe();

    [GeneratedRegex("[^A-Za-z0-9]+")]
    private static partial Regex NonAlnumRunRe();

    [GeneratedRegex(@"^\d+p?\z")]
    private static partial Regex NumOrResRe();

    /// <summary>
    /// Tách tên file thành tập từ khoá nhận dạng phim (<c>Get-TitleTokens</c>).
    /// Bỏ phần trong ( ) và [ ], tách theo ký tự không phải chữ/số ASCII, bỏ từ &lt; 2 ký tự,
    /// số thuần, <c>1080p</c>, từ dừng, từ nhiễu. Giữ thứ tự, không trùng.
    /// </summary>
    public static List<string> TitleTokens(string name)
    {
        var t = ParenRe().Replace(name, " ");
        t = BracketRe().Replace(t, " ");
        var outp = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in NonAlnumRunRe().Split(t))
        {
            if (w.Length == 0)
                continue;
            var lw = w.ToLowerInvariant();
            if (lw.Length < 2)
                continue;
            if (NumOrResRe().IsMatch(lw))
                continue;
            if (StopWords.Contains(lw) || NoiseWords.Contains(lw))
                continue;
            if (seen.Add(lw))
                outp.Add(lw);
        }
        return outp;
    }

    /// <summary>Số từ chung / số từ của bên nhiều hơn (<c>Get-TitleScore</c>/<c>Get-Score</c>).</summary>
    public static double TitleScore(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
            return 0.0;
        var bs = b.ToHashSet(StringComparer.Ordinal);
        var common = a.Count(bs.Contains);
        return (double)common / Math.Max(a.Count, b.Count);
    }

    // ==================================================================
    //  TÁCH TÊN PHIM + NĂM
    // ==================================================================

    private const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    [GeneratedRegex(@"\[[0-9A-Fa-f]{8}\]")]
    private static partial Regex CrcRe();

    [GeneratedRegex(@"\b(?:19|20)\d{2}\b")]
    private static partial Regex YearRe();

    /// <summary>Từ kỹ thuật CHẮC CHẮN (không bao giờ là một phần tên phim): cắt tên tại đây.</summary>
    [GeneratedRegex(@"\b(4320p|2160p|1440p|1080p|720p|576p|480p|360p|4k|uhd|blu-?ray|blu ray|bdrip|brrip|bdremux|remux|" +
                    @"web-?dl|web dl|webrip|hdtv|dvdrip|hdrip|x ?26[45]|h ?26[45]|hevc|avc|xvid|aac\d*|e?ac3|ddp?\d?|" +
                    @"dts(?:-hd)?|truehd|hdr\d*|dovi|10 ?bit|8 ?bit|S\d{1,2}E\d{1,4})\b", IC)]
    private static partial Regex StrongJunkRe();

    /// <summary>Từ kỹ thuật có thể trùng với từ trong tên phim ("Charlotte's Web", "Uncut Gems"): chỉ cắt khi KHÔNG có năm.</summary>
    [GeneratedRegex(@"\b(web|atmos|flac|opus|mp3|sdr|imax|proper|repack|extended|uncut|unrated|remastered|criterion|" +
                    @"multi|dual|vietsub|thuyet minh|long tieng|amzn|nf|dsnp|hmax|atvp|hulu|pcok|itunes|internal)\b", IC)]
    private static partial Regex WeakJunkRe();

    [GeneratedRegex(@"[\(\)\[\]\{\}]")]
    private static partial Regex AnyBracketCharRe();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WsRunRe();

    [GeneratedRegex(@"[\._]+")]
    private static partial Regex DotUnderscoreRunRe();

    internal static string CleanTitle(string s)
    {
        s = AnyBracketCharRe().Replace(s, " ");
        s = WsRunRe().Replace(s, " ");
        return Py.Strip(s, " -_");
    }

    private static string NormalizeRelease(string name)
    {
        var t = CrcRe().Replace(name, " ");
        t = BracketRe().Replace(t, " ");
        t = DotUnderscoreRunRe().Replace(t, " ");
        return WsRunRe().Replace(t, " ").Trim();
    }

    /// <summary>
    /// Năm = năm CUỐI CÙNG hợp lệ trong <paramref name="region"/>; trả (phần tên trước năm, năm).
    /// Không phải năm nếu: lớn hơn năm hiện tại + 1 (<c>Blade Runner 2049</c>), hoặc bỏ đi thì tên rỗng
    /// (<c>2001 A Space Odyssey</c>, <c>1917</c>). (TĐ-05.)
    /// </summary>
    public static (string? Title, int? Year) PickYear(string region, int? currentYear = null)
    {
        var cy = currentYear is { } c && c != 0 ? c : DateTime.Today.Year;
        var ms = YearRe().Matches(region);
        for (var i = ms.Count - 1; i >= 0; i--)
        {
            var m = ms[i];
            var val = Py.Int(m.Value);
            if (val > cy + 1)
                continue;
            var before = CleanTitle(region[..m.Index]);
            if (before.Length == 0)
                continue;
            return (before, val);
        }
        return (null, null);
    }

    /// <summary>
    /// Tên release → tên phim + năm (thay <c>Split-ReleaseName</c>/<c>Get-CleanTitle</c>/<c>Get-Year</c>).
    /// <c>Avengers.Endgame.2019.2160p.BluRay.x265-GRP</c> → ("Avengers Endgame", 2019)
    /// </summary>
    public static ReleaseName SplitReleaseName(string name, int? currentYear = null)
    {
        var t = NormalizeRelease(name);
        var m = StrongJunkRe().Match(t);
        var region = m.Success ? t[..m.Index] : t;
        var (title, year) = PickYear(region, currentYear);
        if (title is not null)
            return new ReleaseName(title, year);
        var w = WeakJunkRe().Match(region);
        if (w.Success && CleanTitle(region[..w.Index]).Length > 0)
            region = region[..w.Index];
        return new ReleaseName(CleanTitle(region), null);
    }

    public static int? TitleYear(string name, int? currentYear = null) => SplitReleaseName(name, currentYear).Year;

    [GeneratedRegex(@"\b(4320p|2160p|1440p|1080p|720p|576p|480p|360p|4k|uhd|bluray|blu ray|bdrip|brrip|bdremux|remux|" +
                    @"web-?dl|webrip|web|hdtv|dvdrip|hdrip|x ?26[45]|h ?26[45]|hevc|avc|xvid|aac\d*|ac3|eac3|ddp?\d?|" +
                    @"dts(-hd)?|truehd|atmos|flac|opus|mp3|hdr\d*|dovi|sdr|imax|proper|repack|extended|uncut|unrated|" +
                    @"remastered|criterion|multi|dual|vietsub|thuyet minh|10 ?bit|8 ?bit|amzn|nf|dsnp|hmax|atvp|hulu|" +
                    @"pcok|itunes|internal|repack)\b", IC)]
    private static partial Regex TitleKeyJunkRe();

    [GeneratedRegex(@"\b(19|20)\d{2}\b", IC)]
    private static partial Regex YearIcRe();

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonAlnumCharRe();

    /// <summary><c>Get-TitleKey</c> (SubPrep, chỉ để hiển thị): <c>28.Years.Later.2025.1080p…</c> → <c>28yearslater</c>.</summary>
    public static string TitleKey(string name)
    {
        var t = BracketRe().Replace(name, " ");
        t = ParenRe().Replace(t, " ");
        t = DotUnderscoreRunRe().Replace(t, " ");
        t = WsRunRe().Replace(t, " ");
        var m = TitleKeyJunkRe().Match(t);
        if (m.Success)
            t = t[..m.Index];
        t = YearIcRe().Replace(t, " ");
        return NonAlnumCharRe().Replace(t, "").ToLowerInvariant();
    }

    /// <summary><c>Get-NormTitle</c>: chỉ giữ chữ + số ASCII, chữ thường.</summary>
    public static string NormTitle(string? t) => NonAlnumCharRe().Replace(t ?? "", "").ToLowerInvariant();

    // ==================================================================
    //  TÊN FILE AN TOÀN + TÊN CHUẨN JELLYFIN
    // ==================================================================

    [GeneratedRegex("""[\\/:*?"<>|]""")]
    private static partial Regex ForbiddenCharRe();

    /// <summary><c>Get-SafeFileName</c>: <c>:</c> → <c> -</c>, bỏ ký tự Windows cấm, gộp khoảng trắng, bỏ dấu chấm cuối.</summary>
    public static string SafeFilename(string n)
    {
        n = n.Replace(":", " -", StringComparison.Ordinal);
        n = ForbiddenCharRe().Replace(n, "");
        n = WsRunRe().Replace(n, " ");
        return n.Trim().TrimEnd('.');
    }

    [GeneratedRegex(@"[\[\]\(\)\{\}]")]
    private static partial Regex JellyBracketRe();

    [GeneratedRegex(@"[\[\]\{\}]")]
    private static partial Regex SquareCurlyRe();

    /// <summary>Tên phim/series sạch ngoặc (Jellyfin đọc nhầm phần trong ngoặc).</summary>
    public static string JellyfinTitle(string title)
    {
        var t = JellyBracketRe().Replace(SafeFilename(title), " ");
        return WsRunRe().Replace(t, " ").Trim();
    }

    /// <summary><c>Tên phim (Năm) [tmdbid-123]</c> / <c>[tvdbid-123]</c> — tên thư mục và tên file phim lẻ.</summary>
    public static string MovieBaseName(string title, object year, string db, object dbId) =>
        $"{JellyfinTitle(title)} ({Py.Str(year)}) [{db.ToLowerInvariant()}id-{Py.Str(dbId)}]";

    /// <summary>Thư mục series: <c>Tên series (Năm) [tmdbid-123]</c> (không có năm thì bỏ phần năm).</summary>
    public static string ShowFolderName(string name, object? year, string db, object dbId)
    {
        var nm = JellyfinTitle(name);
        if (Py.Truthy(year))
            return $"{nm} ({Py.Str(year)}) [{db.ToLowerInvariant()}id-{Py.Str(dbId)}]";
        return $"{nm} [{db.ToLowerInvariant()}id-{Py.Str(dbId)}]";
    }

    /// <summary><c>Season 01</c>. Không bao giờ <c>Season 00</c> (Jellyfin coi là tập đặc biệt).</summary>
    public static string SeasonFolderName(int season) =>
        "Season " + Math.Max(1, season).ToString("00", CultureInfo.InvariantCulture);

    /// <summary><c>S01E05</c> hoặc <c>S01E05-E06</c> (tập đôi). Season ≤ 0 → 1.</summary>
    public static string EpisodeCode(int season, int episode, int? episodeEnd = null)
    {
        var s = Math.Max(1, season);
        var code = string.Create(CultureInfo.InvariantCulture, $"S{s:00}E{episode:00}");
        if (episodeEnd is { } e && e != 0)
            code += string.Create(CultureInfo.InvariantCulture, $"-E{e:00}");
        return code;
    }

    /// <summary><c>Tên series (Năm) - S01E05 - Tên tập</c>.</summary>
    public static string EpisodeBaseName(string showName, object? year, string code, string? episodeTitle = null)
    {
        var prefix = Py.Truthy(year) ? $"{JellyfinTitle(showName)} ({Py.Str(year)})" : JellyfinTitle(showName);
        var b = $"{prefix} - {code}";
        if (!string.IsNullOrEmpty(episodeTitle))
        {
            var et = SquareCurlyRe().Replace(SafeFilename(episodeTitle), " ");
            et = WsRunRe().Replace(et, " ").Trim();
            if (et.Length > 0)
                b += $" - {et}";
        }
        return SafeFilename(b);
    }

    // ==================================================================
    //  ĐẶT TÊN THEO DATABASE (bước ③) — phần không cần client
    // ==================================================================

    /// <summary>Năm từ chuỗi ngày <c>"2019-05-01"</c>; không có → null.</summary>
    public static int? YearOf(object? date)
    {
        var s = Py.Truthy(date) ? Py.Str(date) : ""; // str(date or "")
        return s.Length >= 4 && Py.IsDigit(s[..4]) ? Py.Int(s[..4]) : null;
    }

    [GeneratedRegex(@"[._\- ]+")]
    private static partial Regex SuffixSepRe();

    /// <summary>
    /// Ngôn ngữ trong phần đuôi tên phụ đề ngoài sau tên phim (<c>.vi</c>, <c>_vietnamese</c>, <c>.en.sdh</c>, <c>-ja</c>…).
    /// Từ đầu tiên nhận ra được thắng; không nhận ra → ''. (TĐ-25)
    /// </summary>
    public static string LangOfSuffix(string? suffix)
    {
        foreach (var tok in SuffixSepRe().Split(suffix ?? ""))
        {
            var c = LangFromWord(tok);
            if (c.Length > 0)
                return c;
        }
        return "";
    }
}
