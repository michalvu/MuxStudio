using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace MuxStudio.Core;

/// <summary>Một dòng của bảng kết quả tìm kiếm Titulky (port <c>titulky.SearchRow</c>).</summary>
public sealed class SearchRow(int id, string title, string href, string release = "", string series = "", int? year = null,
                              int downloads = 0, string lang = "")
{
    public int Id { get; } = id;
    public string Title { get; } = title;

    /// <summary><c>&lt;Tên&gt;-&lt;ID&gt;.htm</c>.</summary>
    public string Href { get; } = href;

    public string Release { get; set; } = release;

    /// <summary><c>S01E05</c> ("" = phim lẻ / không ghi).</summary>
    public string Series { get; } = series;

    public int? Year { get; } = year;
    public int Downloads { get; } = downloads;

    /// <summary><c>CZ</c> | <c>SK</c> | "" (không ghi).</summary>
    public string Lang { get; } = lang;

    // điền thêm khi đã xem trang chi tiết
    public string Fps { get; set; } = "";
    public int Cds { get; set; }

    /// <summary><c>srt</c> / <c>sub</c> …</summary>
    public string Kind { get; set; } = "";

    public string Imdb { get; set; } = "";

    public string Url => PyUrl.Join(Titulky.Base, Href);
}

/// <summary>Trang chi tiết đã đọc (<c>parse_detail</c>).</summary>
public sealed record TitulkyDetail(string Fps, int Cds, string Kind, string Release, string Imdb);

/// <summary>Trang <c>idown.php</c>: có CAPTCHA, có link (sau thời gian chờ), hoặc chỉ có thông báo (port <c>titulky.DownloadPage</c>).</summary>
public sealed class DownloadPage
{
    /// <summary>Địa chỉ ảnh CAPTCHA ("" = không có).</summary>
    public string Captcha { get; set; } = "";

    /// <summary>Các ô của form gửi mã CAPTCHA.</summary>
    public OrderedDictionary<string, string> Form { get; } = new(StringComparer.Ordinal);

    /// <summary>Link tải thật ("" = chưa có).</summary>
    public string Link { get; set; } = "";

    /// <summary>Số nhịp CountDown.</summary>
    public int Wait { get; set; }

    /// <summary>Chữ hiển thị của trang (để báo lỗi).</summary>
    public string Message { get; set; } = "";
}

/// <summary>
/// Titulky.com — nguồn phụ đề TIẾNG SÉC cho bước ① (bản 1.3.0, TĐ-40). Trang KHÔNG có API: phải đọc trang web.
/// Port <c>core/titulky.py</c>; xem chú thích đầu file Python về các bước đã kiểm chứng với trang thật (tìm, chi tiết, xem trước,
/// đăng nhập đúng / sai, tải có / không CAPTCHA).
/// <para>Quy tắc: không sửa nội dung phụ đề ở bước ① (chép nguyên byte), không tự vượt CAPTCHA (bạn gõ mã), chờ đủ thời gian trang yêu
/// cầu, nghỉ giữa các lượt gọi.</para>
/// </summary>
public static partial class Titulky
{
    public const string Base = "https://www.titulky.com/";

    /// <summary>Tên trình khách trung thực (trang thật chấp nhận — đã thử 2026-11-11); không giả làm trình duyệt.</summary>
    public const string UserAgent = "Mozilla/5.0 (compatible; MuxStudio; +https://github.com/michalvu/MuxStudio)";

    /// <summary><c>CountDown(t)</c> trong <c>downfnc.js</c> của trang: mỗi nhịp 1,5 giây.</summary>
    public const double CountdownStep = 1.5;

    public const string Lang = "cs";

    /// <summary>Số dòng một trang kết quả (trang thật: <c>ZaznamuStrana=50</c> &amp; <c>ActualRecord=&lt;vị trí&gt;</c>).</summary>
    public const int PageSize = 50;

    private const RegexOptions SI = RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRe();

    [GeneratedRegex(@"\s+")]
    internal static partial Regex WsRe();

    [GeneratedRegex(@"\D")]
    private static partial Regex NonDigitRe();

    /// <summary><c>html.unescape</c> (bộ thực thể HTML 4 của .NET — đủ cho trang Titulky).</summary>
    internal static string Unescape(string s) => WebUtility.HtmlDecode(s);

    /// <summary><c>_text</c>: chữ hiển thị của một đoạn HTML (bỏ thẻ, <c>&amp;nbsp;</c> → khoảng trắng, gộp khoảng trắng).</summary>
    public static string Text(string fragment) =>
        Py.StripWs(WsRe().Replace(Unescape(TagRe().Replace(fragment, " ")).Replace(' ', ' '), " "));

    /// <summary><c>_int</c>: giữ chữ số rồi <c>int()</c>; không có chữ số → <c>null</c>.</summary>
    private static int? IntOf(string? s)
    {
        var d = NonDigitRe().Replace(s ?? "", "");
        return d.Length > 0 ? Py.Int(d) : null;
    }

    /// <summary>Tiêu đề cột của bảng <c>soupis</c> → tên trường (cột có thể thêm / đổi chỗ khi đăng nhập ⇒ dò theo tiêu đề).</summary>
    private static readonly Dictionary<string, string> Headers = new(StringComparer.Ordinal)
    {
        ["název"] = "title", ["nazev"] = "title", ["v."] = "release", ["seriál"] = "series", ["serial"] = "series", ["rok"] = "year",
        ["staž."] = "downloads", ["staz."] = "downloads", ["jazyk"] = "lang", ["uložil"] = "uploader", ["ulozil"] = "uploader",
    };

    private static Dictionary<string, int> DefaultCols() => new(StringComparer.Ordinal)
    {
        ["title"] = 0, ["release"] = 1, ["series"] = 2, ["year"] = 3, ["downloads"] = 4, ["lang"] = 5,
    };

    [GeneratedRegex("""<table[^>]*class="[^"]*\bsoupis\b[^"]*"[^>]*>(.*?)</table>""", SI)]
    private static partial Regex SoupisRe();

    [GeneratedRegex("<tr[^>]*>(.*?)</tr>", SI)]
    private static partial Regex TrRe();

    [GeneratedRegex("<td[^>]*>(.*?)</td>", SI)]
    private static partial Regex TdRe();

    [GeneratedRegex("""<tr[^>]*class="r1?"[^>]*>(.*?)</tr>""", SI)]
    private static partial Regex RowRe();

    [GeneratedRegex("""<a[^>]*href="([^"]*?-(\d+)\.htm)"[^>]*>(.*?)</a>""", SI)]
    private static partial Regex LinkRe();

    [GeneratedRegex(@"title=""([^""]*)""")]
    private static partial Regex TitleAttrRe();

    [GeneratedRegex(@"alt=""([A-Za-z]{2})""")]
    private static partial Regex AltRe();

    /// <summary><c>parse_search</c>: bảng kết quả <c>class="soupis"</c> → danh sách dòng (mọi ngôn ngữ; lọc <c>CZ</c> là việc của nơi gọi).</summary>
    public static List<SearchRow> ParseSearch(string? page)
    {
        var m = SoupisRe().Match(page ?? "");
        if (!m.Success)
            return [];
        var body = m.Groups[1].Value;
        var cols = DefaultCols();
        var head = TrRe().Match(body);
        if (head.Success && head.Groups[1].Value.Contains("detailh", StringComparison.Ordinal))
        {
            var found = new Dictionary<string, int>(StringComparer.Ordinal);
            var i = 0;
            foreach (Match cell in TdRe().Matches(head.Groups[1].Value))
            {
                if (Headers.TryGetValue(Text(cell.Groups[1].Value).ToLowerInvariant(), out var name))
                    found[name] = i;
                i++;
            }
            if (found.ContainsKey("title"))
                cols = found;
        }
        var outp = new List<SearchRow>();
        foreach (Match tr in RowRe().Matches(body))
        {
            var cells = TdRe().Matches(tr.Groups[1].Value).Select(x => x.Groups[1].Value).ToList();
            string Cell(string name) => cols.TryGetValue(name, out var ci) && ci >= 0 && ci < cells.Count ? cells[ci] : "";
            var link = LinkRe().Match(Cell("title"));
            if (!link.Success)
                continue;
            var rel = TitleAttrRe().Match(Cell("release"));
            var lang = AltRe().Match(Cell("lang"));
            var year = IntOf(Text(Cell("year")));
            outp.Add(new SearchRow(Py.Int(link.Groups[2].Value), Text(link.Groups[3].Value), Unescape(link.Groups[1].Value).TrimStart('/'),
                                   rel.Success ? Py.StripWs(Unescape(rel.Groups[1].Value)) : "", Text(Cell("series")).ToUpperInvariant(),
                                   year is >= 1880 and <= 2100 ? year : null, IntOf(Text(Cell("downloads"))) ?? 0,
                                   lang.Success ? lang.Groups[1].Value.ToUpperInvariant() : ""));
        }
        return outp;
    }

    [GeneratedRegex(@"FPS:\s*</span>\s*([\d][\d,\.]*)", SI)]
    private static partial Regex FpsRe();

    [GeneratedRegex(@"Počet CD:\s*</span>\s*(\d+)", SI)]
    private static partial Regex CdRe();

    [GeneratedRegex(@"typ titulků:\s*</span>\s*([A-Za-z0-9]+)", SI)]
    private static partial Regex KindRe();

    [GeneratedRegex("""itemprop=['"]alternateName['"][^>]*>([^<]*)<""", SI)]
    private static partial Regex AltNameRe();

    [GeneratedRegex(@"imdb\.com/title/(tt\d+)", SI)]
    private static partial Regex ImdbRe();

    /// <summary><c>parse_detail</c>: trang chi tiết → FPS ('23.976' / ''), số CD (0 = không ghi), loại file ('srt'…), bản release, IMDb.</summary>
    public static TitulkyDetail ParseDetail(string? page)
    {
        page ??= "";
        string Grab(Regex rx) => rx.Match(page) is { Success: true } m ? Text(m.Groups[1].Value) : "";
        var fps = Grab(FpsRe()).Replace(",", ".", StringComparison.Ordinal);
        return new TitulkyDetail(fps, IntOf(Grab(CdRe())) ?? 0, Grab(KindRe()).ToLowerInvariant(), Grab(AltNameRe()), Grab(ImdbRe()));
    }

    [GeneratedRegex("""<div[^>]*id="nahled_zdroj"[^>]*>(.*?)</div>""", SI)]
    private static partial Regex PreviewRe();

    [GeneratedRegex(@"<br\s*/?>", I)]
    private static partial Regex BrRe();

    /// <summary><c>parse_preview</c>: trang xem trước (<c>nahled.php</c>) → chữ SRT (để đọc mốc thời gian bằng <see cref="Srt.PreviewTimes"/>).</summary>
    public static string ParsePreview(string? page)
    {
        var m = PreviewRe().Match(page ?? "");
        if (!m.Success)
            return "";
        return Unescape(TagRe().Replace(BrRe().Replace(m.Groups[1].Value, "\n"), ""));
    }

    [GeneratedRegex("<body[^>]*>(.*)", SI)]
    private static partial Regex BodyRe();

    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", SI)]
    private static partial Regex ScriptRe();

    [GeneratedRegex(@"<img[^>]*src=""([^""]*captcha[^""]*)""", I)]
    private static partial Regex CaptchaRe();

    [GeneratedRegex("""<form[^>]*name="downform"[^>]*>(.*?)</form>""", SI)]
    private static partial Regex FormRe();

    [GeneratedRegex("<input[^>]*>", I)]
    private static partial Regex InputRe();

    [GeneratedRegex(@"name=""([^""]*)""")]
    private static partial Regex NameAttrRe();

    [GeneratedRegex(@"value=""([^""]*)""")]
    private static partial Regex ValueAttrRe();

    [GeneratedRegex(@"<a[^>]*id=""downlink""[^>]*href=""([^""]+)""", I)]
    private static partial Regex DownlinkRe();

    [GeneratedRegex(@"<a[^>]*href=""([^""]+)""[^>]*id=""downlink""", I)]
    private static partial Regex DownlinkRe2();

    [GeneratedRegex(@"CountDown\(\s*(\d+)\s*\)")]
    private static partial Regex CountDownRe();

    /// <summary><c>parse_download_page</c>.</summary>
    public static DownloadPage ParseDownloadPage(string? page)
    {
        page ??= "";
        var outp = new DownloadPage();
        var body = BodyRe().Match(page);
        outp.Message = Text(ScriptRe().Replace(body.Success ? body.Groups[1].Value : page, " "));
        var cap = CaptchaRe().Match(page);
        if (cap.Success)
            outp.Captcha = Unescape(cap.Groups[1].Value);
        var form = FormRe().Match(page);
        if (form.Success)
        {
            foreach (Match tag in InputRe().Matches(form.Groups[1].Value))
            {
                var name = NameAttrRe().Match(tag.Value);
                if (!name.Success)
                    continue;
                var value = ValueAttrRe().Match(tag.Value);
                outp.Form[name.Groups[1].Value] = value.Success ? Unescape(value.Groups[1].Value) : "";
            }
        }
        var link = DownlinkRe().Match(page);
        if (!link.Success)
            link = DownlinkRe2().Match(page);
        if (link.Success)
            outp.Link = Unescape(link.Groups[1].Value);
        var wait = CountDownRe().Match(page);
        if (wait.Success)
            outp.Wait = Py.Int(wait.Groups[1].Value);
        return outp;
    }

    [GeneratedRegex(@"id=""log_login""|name=""Login""")]
    private static partial Regex LoginFormRe();

    /// <summary><c>has_login_form</c>: trang còn ô đăng nhập = CHƯA đăng nhập.</summary>
    public static bool HasLoginForm(string? page) => LoginFormRe().IsMatch(page ?? "");

    /// <summary><c>looks_like_html</c>: trả lời là TRANG WEB (không phải file phụ đề / file nén)? Trang thật bắt đầu bằng <c>&lt;!--</c>
    /// hoặc <c>&lt;!DOCTYPE</c>; file phụ đề không bao giờ bắt đầu bằng <c>&lt;</c>.</summary>
    public static bool LooksLikeHtml(ReadOnlySpan<byte> data)
    {
        var i = 0;
        while (i < data.Length && data[i] is 0xEF or 0xBB or 0xBF or (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            i++;
        return i < data.Length && data[i] == (byte)'<';
    }

    /// <summary><c>search_query</c>: phim bộ → <c>Tên S01E05</c> (trang ghi tập đúng kiểu này).</summary>
    public static string SearchQuery(string? title, int? season = null, int? episode = null)
    {
        var t = Py.StripWs(WsRe().Replace(title ?? "", " "));
        if (season is { } s && episode is { } e)
            return string.Create(CultureInfo.InvariantCulture, $"{t} S{s:00}E{e:00}");
        return t;
    }

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonAlnumRe();

    /// <summary>
    /// <c>title_fragments</c>: các MẢNH của tên phim nên thử khi tìm nguyên tên không ra. Ô tìm của Titulky so khớp NGUYÊN VĂN; tên rút
    /// từ tên file mất dấu câu. Cắt đôi tên (rồi cắt tiếp mỗi nửa); mảnh dài thử trước; bỏ mảnh quá chung chung (dưới 6 chữ cái).
    /// </summary>
    public static List<string> TitleFragments(string? title, int limit = 4)
    {
        var words = Py.StripWs(WsRe().Replace(title ?? "", " ")).Split(' ');
        if (words.Length < 2)
            return [];
        static List<string[]> Halves(string[] ws)
        {
            var mid = (ws.Length + 1) / 2;
            return ws.Length >= 2 ? [ws[..mid], ws[mid..]] : [];
        }
        var level1 = Halves(words);
        var level2 = level1.SelectMany(Halves).ToList();
        var outp = new List<string>();
        foreach (var group in new[] { level1, level2 })
        {
            foreach (var ws in group.OrderBy(x => -string.Join(" ", x).Length))
            {
                var frag = string.Join(" ", ws);
                if (NonAlnumRe().Replace(frag, "").Length >= 6 && !outp.Any(x => x.ToLowerInvariant() == frag.ToLowerInvariant()))
                    outp.Add(frag);
            }
        }
        return outp.Take(Math.Max(0, limit)).ToList();
    }
}

/// <summary>Client Titulky.com (port <c>titulky.TitulkyClient</c>). Mọi hàm chặn (chạy ở luồng phụ).</summary>
public sealed partial class TitulkyClient
{
    private double? _lastCall;
    private readonly Dictionary<string, (int Seen, List<SearchRow> Rows)> _searchCache = new(StringComparer.Ordinal);
    private readonly HashSet<int> _detailDone = [];
    private readonly Dictionary<int, string> _previewCache = [];
    private readonly Dictionary<int, string?> _downCache = [];
    private readonly Func<double> _clock;
    private readonly Func<double> _now;

    public TitulkyClient(string? username = "", string? password = "", int pauseMs = 1100, Http? http = null, Action<double>? sleep = null,
                         Job? job = null, string? tempDir = null, Func<double>? clock = null, Func<double>? now = null,
                         string? userAgent = Titulky.UserAgent)
    {
        Username = Py.StripWs(username ?? "");
        Password = password ?? "";
        Pause = Math.Max(0, pauseMs) / 1000.0;
        HttpClient = http ?? Http.Shared;
        Job = job ?? Job.Null;
        Sleep = sleep ?? (s => Http.Sleep(s, Job.Token));
        TempDir = string.IsNullOrEmpty(tempDir) ? null : tempDir;
        _clock = clock ?? (() => Environment.TickCount64 / 1000.0);
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
        UserAgent = string.IsNullOrEmpty(userAgent) ? Titulky.UserAgent : userAgent;
    }

    public string Username { get; }
    public string Password { get; }
    public double Pause { get; }
    public Http HttpClient { get; }
    public Job Job { get; set; }

    /// <summary>Hàm chờ (= <c>self.sleep</c>; test thay được giữa chừng).</summary>
    public Action<double> Sleep { get; set; }

    public string? TempDir { get; private set; }
    public string UserAgent { get; }

    /// <summary><c>null</c> = không dùng tài khoản.</summary>
    public bool? LoggedIn { get; private set; }

    /// <summary>"" | <c>rejected</c> | <c>unconfirmed</c> | <c>network</c> (xem <see cref="Login"/>).</summary>
    public string LoginNote { get; private set; } = "";

    public bool Disabled { get; set; }

    /// <summary>Bạn đã bỏ qua CAPTCHA một lần → không hỏi nữa trong lượt này.</summary>
    public bool CaptchaDeclined { get; private set; }

    public int LastStatus { get; private set; }

    public bool HasAccount => Username.Length > 0 && Password.Length > 0;

    // ------------------------------------------------------------------ goi trang
    private void Pace()
    {
        if (_lastCall is { } last && Pause > 0)
        {
            var wait = Pause - (_clock() - last);
            if (wait > 0)
                Sleep(wait);
        }
        _lastCall = _clock();
    }

    /// <summary><c>_request</c>: một lượt gọi (nghỉ <see cref="Pause"/> giữa các lượt; lỗi mạng thử 3 lần). <c>null</c> = không lấy được.</summary>
    private HttpResult? Request(string method, string url, IEnumerable<KeyValuePair<string, string?>>? parameters = null,
                                IEnumerable<KeyValuePair<string, string>>? data = null, string? referer = null, int timeout = 30)
    {
        if (Disabled)
            return null;
        var headers = new List<KeyValuePair<string, string>>
        {
            new("User-Agent", UserAgent), new("Referer", string.IsNullOrEmpty(referer) ? Titulky.Base : referer), new("Accept-Language", "cs,en;q=0.7"),
        };
        var ps = parameters?.ToList();
        var form = data?.ToList();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Job.Check();
            Pace();
            HttpResult r;
            try
            {
                r = HttpClient.Request(method, url, ps, headers, form: form, timeout: timeout, ct: Job.Token);
            }
            catch (HttpNetworkException e)
            {
                if (attempt < 3)
                {
                    Sleep(2 * attempt);
                    continue;
                }
                Job.Error($"    [!] Titulky lỗi mạng: {e.Message}");
                return null;
            }
            LastStatus = r.StatusCode;
            if (r.StatusCode == 200)
                return r;
            if (r.StatusCode is 429 or 502 or 503 && attempt < 3)
            {
                Job.Dim(string.Create(CultureInfo.InvariantCulture, $"    [i] Titulky đang bận (HTTP {r.StatusCode}), chờ {5 * attempt} giây..."));
                Sleep(5 * attempt);
                continue;
            }
            Job.Warn(string.Create(CultureInfo.InvariantCulture, $"    [!] Titulky trả về HTTP {r.StatusCode}."));
            return null;
        }
        return null;
    }

    private static KeyValuePair<string, string?> P(string k, object? v) => new(k, Py.Str(v));

    // ------------------------------------------------------------------ dang nhap

    [GeneratedRegex("LoginError|BadLogin")]
    private static partial Regex LoginErrorRe();

    /// <summary>
    /// <c>login</c>: đăng nhập (tuỳ chọn). Không có tài khoản → <c>false</c> và vẫn dùng được kiểu khách. <see cref="LoginNote"/> sau khi
    /// gọi: "" (được / không dùng tài khoản), <c>rejected</c> (trang báo <c>LoginError</c>), <c>unconfirmed</c> (gửi xong nhưng trang
    /// chủ vẫn hiện ô đăng nhập), <c>network</c>.
    /// </summary>
    public bool Login(bool quiet = false)
    {
        LoginNote = "";
        if (!HasAccount)
        {
            LoggedIn = null;
            return false;
        }
        var data = new List<KeyValuePair<string, string>>
        {
            new("Login", Username), new("Password", Password), new("foreverlog", "1"), new("Detail2", ""), new("prihlasit", "Přihlásit"),
        };
        var r = Request("POST", Titulky.Base, data: data);
        var ok = false;
        if (r is null)
        {
            LoginNote = "network";
        }
        else if (LoginErrorRe().IsMatch(r.Text + " " + r.Url))
        {
            LoginNote = "rejected"; // trang that: chuyen huong toi /?ShowInfo=LoginError&BadId=&BadLogin=<ten>
        }
        else
        {
            // khong bao loi -> xem trang chu: da dang nhap thi khong con o dang nhap
            var r2 = Request("GET", Titulky.Base);
            ok = r2 is not null && !Titulky.HasLoginForm(r2.Text);
            if (!ok)
                LoginNote = r2 is not null ? "unconfirmed" : "network";
        }
        LoggedIn = ok;
        if (ok && !quiet)
            Job.Info("[i] Titulky: đã đăng nhập.");
        else if (!ok && !quiet)
            Job.Warn($"[!] {LoginProblem()} Vẫn tìm và tải được kiểu khách (hay phải gõ CAPTCHA).");
        return ok;
    }

    /// <summary><c>login_problem</c>: câu giải thích lần đăng nhập vừa hỏng (theo <see cref="LoginNote"/>).</summary>
    public string LoginProblem() => LoginNote switch
    {
        "rejected" => "Titulky từ chối tên đăng nhập / mật khẩu (trang báo LoginError) — thử đăng nhập trên titulky.com bằng "
                      + "trình duyệt với đúng tên và mật khẩu đó để kiểm tra.",
        "unconfirmed" => "Đã gửi đăng nhập Titulky nhưng trang chủ vẫn hiện ô đăng nhập — chưa đăng nhập được.",
        _ => "Không gửi được đăng nhập tới Titulky (lỗi mạng).",
    };

    // ------------------------------------------------------------------ tim

    /// <summary><c>search</c>: tìm theo chữ (so khớp nguyên văn). Mỗi trang 50 dòng; <paramref name="pages"/> = số trang tối đa được xem
    /// (trang sau chỉ gọi khi trang trước đầy). Có nhớ đệm trong lượt chạy.</summary>
    public List<SearchRow> Search(string? query, int pages = 1)
    {
        var q = Py.StripWs(Titulky.WsRe().Replace(query ?? "", " "));
        if (q.Length == 0)
            return [];
        var key = q.ToLowerInvariant();
        pages = Math.Max(1, pages);
        var (seen, rows) = _searchCache.TryGetValue(key, out var hit) ? hit : (0, []);
        if (seen >= pages || (seen > 0 && rows.Count < seen * Titulky.PageSize))
            return rows;
        rows = [.. rows];
        var ids = rows.Select(r => r.Id).ToHashSet();
        while (seen < pages)
        {
            var ps = new List<KeyValuePair<string, string?>> { P("Fulltext", q) };
            if (seen > 0)
            {
                ps.Add(P("ZaznamuStrana", Titulky.PageSize));
                ps.Add(P("ActualRecord", seen * Titulky.PageSize));
            }
            var r = Request("GET", Titulky.Base, ps);
            if (r is null)
                return rows;
            var got = Titulky.ParseSearch(r.Text);
            seen++;
            rows.AddRange(got.Where(x => !ids.Contains(x.Id)));
            ids.UnionWith(got.Select(x => x.Id));
            _searchCache[key] = (seen, rows);
            if (got.Count < Titulky.PageSize)
                break;
        }
        return rows;
    }

    /// <summary><c>fill_detail</c>: xem trang chi tiết MỘT lần cho dòng này: FPS, số CD, loại file, bản release (nếu bảng không ghi), IMDb.</summary>
    public SearchRow FillDetail(SearchRow row)
    {
        if (_detailDone.Contains(row.Id))
            return row;
        var r = Request("GET", row.Url);
        if (r is null)
            return row;
        _detailDone.Add(row.Id);
        var d = Titulky.ParseDetail(r.Text);
        (row.Fps, row.Cds, row.Kind, row.Imdb) = (d.Fps, d.Cds, d.Kind, d.Imdb);
        if (d.Release.Length > 0 && row.Release.Length == 0)
            row.Release = d.Release;
        return row;
    }

    /// <summary><c>preview</c>: chữ SRT của bản xem trước ("" nếu không có). Không tốn lượt tải.</summary>
    public string Preview(SearchRow row)
    {
        if (_previewCache.TryGetValue(row.Id, out var hit))
            return hit;
        var r = Request("GET", PyUrl.Join(Titulky.Base, "nahled.php"), [P("Titulky", row.Id), P("histstamp", "")], referer: row.Url);
        if (r is null)
            return "";
        var txt = Titulky.ParsePreview(r.Text);
        _previewCache[row.Id] = txt;
        return txt;
    }

    // ------------------------------------------------------------------ tai
    private string WorkDir()
    {
        TempDir ??= Directory.CreateTempSubdirectory("muxstudio_titulky_").FullName;
        Directory.CreateDirectory(TempDir);
        return TempDir;
    }

    /// <summary><c>_wait_countdown</c>: chờ đủ thời gian trang yêu cầu (<c>CountDown(n)</c>: n nhịp × 1,5 s). Bấm "Dừng ngay" là thoát.</summary>
    private void WaitCountdown(int ticks)
    {
        var total = Math.Max(0.0, ticks * Titulky.CountdownStep) + (ticks > 0 ? 0.5 : 0.0);
        if (total <= 0)
            return;
        Job.Dim(string.Create(CultureInfo.InvariantCulture, $"      (Titulky yêu cầu chờ {Math.Round(total)} giây trước khi tải)"));
        var left = total;
        while (left > 0)
        {
            Job.Check();
            var step = Math.Min(0.5, left);
            Sleep(step);
            left -= step;
        }
    }

    [GeneratedRegex("""filename\*?=(?:UTF-8'')?"?([^";]+)"?""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FilenameRe();

    [GeneratedRegex(@"\.(srt|ass|ssa|sub|vtt|txt)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SubNameRe();

    /// <summary><c>_store</c>: lưu file trả về vào thư mục tạm riêng của <paramref name="subId"/> (ZIP → giải nén; file trần → giữ NGUYÊN byte).</summary>
    private string? Store(int subId, HttpResult r)
    {
        var data = r.Content;
        var id = subId.ToString(CultureInfo.InvariantCulture);
        var outDir = Py.PathJoin(WorkDir(), id);
        if (data.Length >= 2 && data[0] == (byte)'P' && data[1] == (byte)'K')
        {
            var zpath = Py.PathJoin(WorkDir(), id + ".zip");
            try
            {
                File.WriteAllBytes(zpath, data);
                SubSource.SafeExtract(zpath, outDir);
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                Job.Error($"      [!] Giải nén lỗi: {e.Message}");
                return null;
            }
            return outDir;
        }
        if ((data.Length >= 4 && data.AsSpan(0, 4).SequenceEqual("Rar!"u8)) || (data.Length >= 2 && data[0] == (byte)'7' && data[1] == (byte)'z'))
        {
            Job.Warn("      [!] Titulky trả về file nén RAR / 7z — cần 7-Zip, bỏ qua.");
            return null;
        }
        if (data.Length < 16)
        {
            Job.Warn("      [!] File Titulky tải về rỗng.");
            return null;
        }
        var name = "";
        var m = FilenameRe().Match(r.Header("Content-Disposition"));
        if (m.Success)
            name = Py.Basename(Py.StripWs(m.Groups[1].Value));
        if (!SubNameRe().IsMatch(name))
        {
            var head = data.AsSpan(0, Math.Min(400, data.Length));
            name = id + (head.IndexOf("[Script Info]"u8) >= 0 ? ".ass" : ".srt");
        }
        Directory.CreateDirectory(outDir);
        File.WriteAllBytes(Py.Join(outDir, name), data);
        return outDir;
    }

    /// <summary>
    /// <c>download</c>: tải MỘT phụ đề → thư mục tạm chứa file (ZIP đã giải nén). <c>null</c> = không tải được (đã ghi lý do).
    /// Gặp CAPTCHA thì gọi <paramref name="captcha"/>(ảnh, ghi chú) để BẠN gõ mã (tối đa 3 lần gõ sai); bạn bỏ qua một lần thì các bản
    /// cần CAPTCHA sau đó trong lượt này không hỏi nữa.
    /// </summary>
    public string? Download(SearchRow row, Func<byte[], string, string?>? captcha = null)
    {
        if (_downCache.TryGetValue(row.Id, out var hit))
            return hit;
        var got = DownloadCore(row, captcha);
        _downCache[row.Id] = got;
        return got;
    }

    private string? DownloadCore(SearchRow row, Func<byte[], string, string?>? captcha)
    {
        var sid = row.Id;
        var url = PyUrl.Join(Titulky.Base, "idown.php");
        var r = Request("GET", url, [P("R", (long)_now()), P("titulky", sid), P("zip", "z"), P("histstamp", "")], referer: row.Url);
        var asked = 0;
        while (r is not null)
        {
            if (!Titulky.LooksLikeHtml(r.Content))
                return Store(sid, r);
            var page = Titulky.ParseDownloadPage(r.Text);
            if (page.Link.Length > 0)
            {
                WaitCountdown(page.Wait);
                var f = Request("GET", PyUrl.Join(url, page.Link), referer: url, timeout: 90);
                if (f is null)
                    return null;
                if (!Titulky.LooksLikeHtml(f.Content))
                    return Store(sid, f);
                var msg = Titulky.ParseDownloadPage(f.Text).Message;
                Job.Warn($"      [!] Titulky không trả file: {Py.Slice(msg, 0, 200)}");
                return null;
            }
            if (page.Captcha.Length > 0)
            {
                if (CaptchaDeclined)
                {
                    Job.Warn("      [!] Titulky đòi CAPTCHA (bạn đã bỏ qua trong lượt này) → bỏ bản này.");
                    return null;
                }
                if (captcha is null)
                {
                    Job.Warn("      [!] Titulky đòi CAPTCHA nhưng không có cửa sổ để nhập → bỏ bản này.");
                    return null;
                }
                if (asked >= 3)
                {
                    Job.Warn("      [!] Mã CAPTCHA sai 3 lần → bỏ bản này.");
                    return null;
                }
                var img = Request("GET", PyUrl.Join(url, page.Captcha), referer: url);
                if (img is null || img.Content.Length == 0)
                {
                    Job.Warn("      [!] Không tải được ảnh CAPTCHA của Titulky.");
                    return null;
                }
                asked++;
                var note = row.Release.Length > 0 ? row.Release : row.Title;
                if (asked > 1)
                    note += "\n(mã vừa gõ không đúng — thử lại)";
                var code = captcha(img.Content, note);
                if (string.IsNullOrEmpty(code) || Py.StripWs(code).Length == 0)
                {
                    CaptchaDeclined = true;
                    Job.Warn("      [!] Bạn bỏ qua CAPTCHA → các bản Titulky cần CAPTCHA sẽ bị bỏ qua trong lượt này.");
                    return null;
                }
                var form = page.Form.Count > 0
                    ? new OrderedDictionary<string, string>(page.Form, StringComparer.Ordinal)
                    : new OrderedDictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["securedown"] = "2", ["zip"] = "z", ["T"] = "", ["titulky"] = sid.ToString(CultureInfo.InvariantCulture), ["histstamp"] = "",
                    };
                form["downkod"] = Py.StripWs(code);
                r = Request("POST", url, data: form, referer: url);
                continue;
            }
            var m = Py.Slice(page.Message, 0, 240);
            Job.Warn($"      [!] Titulky không cho tải: {(m.Length > 0 ? m : "trang không có link tải")}");
            return null;
        }
        return null;
    }

    /// <summary><c>subtitle_files</c>: file phụ đề dùng được trong thư mục đã tải (<c>.srt</c> / <c>.ass</c> / <c>.ssa</c>).</summary>
    public List<string> SubtitleFiles(string? d) => string.IsNullOrEmpty(d) ? [] : SubSource.PackFiles(d);

    // ------------------------------------------------------------------ kiem tra ket noi

    /// <summary><c>check</c>.</summary>
    public ConnStatus Check()
    {
        var r = Request("GET", Titulky.Base);
        if (r is null)
            return new ConnStatus(false, $"Titulky: không kết nối được ({(LastStatus != 0 ? LastStatus.ToString(CultureInfo.InvariantCulture) : "lỗi mạng")})");
        if (!HasAccount)
            return new ConnStatus(true, "Titulky: dùng kiểu khách (không tài khoản) — tìm được; tải hay phải gõ CAPTCHA");
        if (Login(quiet: true))
            return new ConnStatus(true, "Titulky: đăng nhập được");
        return new ConnStatus(false, "Titulky: đăng nhập lỗi — " + LoginProblem());
    }
}
