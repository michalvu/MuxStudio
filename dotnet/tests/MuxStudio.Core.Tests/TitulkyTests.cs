using System.Text;
using System.Text.RegularExpressions;
using MuxStudio.Core.Tests.Support;

namespace MuxStudio.Core.Tests;

/// <summary>Titulky.com (bản 1.3.0, TĐ-40): bộ đọc HTML chạy trên MẪU THẬT của trang (<c>tests/data/titulky/</c> của repo); luồng
/// đăng nhập / tải / CAPTCHA / chờ chạy với máy chủ giả. Không gọi mạng thật. Port <c>tests/test_titulky.py</c>.</summary>
public class TitulkyTests
{
    public static string DataDir => RepoPaths.Data("titulky");

    public static string Page(string name) => File.ReadAllText(Path.Combine(DataDir, name), Encoding.UTF8);

    private static (TitulkyClient C, Job J, Sleeper S) Client(FakeHttp http, string tmp, string username = "", string password = "")
    {
        var sl = new Sleeper();
        var job = new Job();
        var c = new TitulkyClient(username, password, 1100, http.Client, sl.Sleep, job, Path.Combine(tmp, "tt"), sl.Clock, () => 1794415365);
        return (c, job, sl);
    }

    /// <summary>Trang chờ — dựng theo downfnc.js thật: CountDown(n) + id=downlink.</summary>
    public static string CountdownPage(int n = 6, string link = "idown.php?id=abc123") =>
        $"<!DOCTYPE html><html><body><span id=\"count_down\">{n}</span>"
        + $"<div id=\"downdiv\" style=\"display:none\"><a id=\"downlink\" href=\"{link}\">stáhnout</a></div>"
        + $"<script>CountDown({n})</script></body></html>";

    private static bool Has(Job job, string s) => job.Lines.Any(l => l.Message.Contains(s, StringComparison.Ordinal));

    private static string AllText(Job job) => string.Join("\n", job.Lines.Select(l => l.Message));

    // ---------------------------------------------------------------- doc HTML that

    [Fact]
    public void Parse_search_real_series_page()
    {
        var rows = Titulky.ParseSearch(Page("search_breaking_bad_s01e05.html"));
        Assert.Equal(4, rows.Count);
        var r = rows[2];
        Assert.Equal((232758, "Breaking Bad S01E05", "S01E05", (int?)2008, "CZ"), (r.Id, r.Title, r.Series, r.Year, r.Lang));
        Assert.Equal("Breaking.Bad.S01E05.Gray.Matter.720p.BRRip.x264.AC3", r.Release);
        Assert.True(r.Downloads > 1000);
        Assert.Equal("Breaking-Bad-S01E05-232758.htm", r.Href);
        Assert.Equal("https://www.titulky.com/Breaking-Bad-S01E05-232758.htm", r.Url);
        Assert.All(rows, x => Assert.True(x.Lang == "CZ" && x.Series == "S01E05"));
    }

    [Fact]
    public void Parse_search_real_movie_page_languages_years_and_missing_cells()
    {
        var html = Page("search_dune.html");
        var rows = Titulky.ParseSearch(html);
        Assert.Equal(13, rows.Count);
        Assert.Equal(("Duna", (int?)1984), (rows[0].Title, rows[0].Year)); // ten tieng Sec cua phim
        Assert.Equal(new HashSet<string> { "CZ", "SK", "" }, rows.Select(r => r.Lang).ToHashSet()); // dong cu khong ghi ngon ngu
        Assert.Equal([2021], rows.Where(r => r.Lang == "SK").Select(r => r.Year));
        Assert.Contains(rows, r => r.Release == "");
        Assert.Contains(rows, r => r.Year is null);
        Assert.All(rows, r => Assert.Equal("", r.Series)); // phim le: cot Seriál la &nbsp;
        Assert.Equal(13, rows.Select(r => r.Id).Distinct().Count());
        Assert.Equal("Dune.2021.BRRip.x264-ION10", rows[1].Release);
        Assert.Equal(rows[1].Release, rows[2].Release);
        Assert.Empty(Titulky.ParseSearch("<html>không có bảng</html>"));
        Assert.Empty(Titulky.ParseSearch(""));
        Assert.Contains("class=\"soupis\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("table.find", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_search_logged_in_layout_has_extra_saved_column()
    {
        // Da dang nhap (nguoi dung do 2026-11-12): bang co them cot "Uloženo" giua Rok va Staž.
        var orig = Page("search_breaking_bad_s01e05.html");
        var headCells = Regex.Matches(orig, "<td[^>]*class=\"detailh[^>]*>.*?</td>", RegexOptions.Singleline).Select(m => m.Value).ToList();
        Assert.Equal(7, headCells.Count);
        var idx = orig.IndexOf(headCells[3], StringComparison.Ordinal);
        var html = orig[..idx] + headCells[3] + "<td class=\"detailh ucase\">Uloženo</td>" + orig[(idx + headCells[3].Length)..];
        html = Regex.Replace(html, "<tr class=\"r1?\">.*?</tr>", m =>
        {
            var cells = Regex.Matches(m.Value, "<td[^>]*>.*?</td>", RegexOptions.Singleline).Select(x => x.Value).ToList();
            var i = m.Value.IndexOf(cells[3], StringComparison.Ordinal);
            return m.Value[..i] + cells[3] + "<td>12.11.2026</td>" + m.Value[(i + cells[3].Length)..];
        }, RegexOptions.Singleline);
        Assert.Equal(["Název", "V.", "Seriál", "Rok", "Uloženo", "Staž.", "Jazyk", "Uložil"],
            Regex.Matches(html, "<td[^>]*class=\"detailh[^>]*>(.*?)</td>", RegexOptions.Singleline).Select(m => Titulky.Text(m.Groups[1].Value)));
        var rows = Titulky.ParseSearch(html);
        var plain = Titulky.ParseSearch(orig);
        Assert.Equal(4, rows.Count);
        Assert.Equal(plain.Select(r => (r.Id, r.Title, r.Release, r.Series, r.Year, r.Downloads, r.Lang)),
                     rows.Select(r => (r.Id, r.Title, r.Release, r.Series, r.Year, r.Downloads, r.Lang)));
        Assert.All(rows, r => Assert.True(r.Lang == "CZ" && r.Downloads > 500));
    }

    [Fact]
    public void Parse_search_follows_header_when_columns_move()
    {
        var html = "<table class=\"soupis\"><tr><td class=\"detailh ucase\">Jazyk</td><td class=\"detailh\">Nový</td>"
                   + "<td class=\"detailh ucase\">Název</td><td class=\"detailh\" title=\"Verze/Release\">V.</td>"
                   + "<td class=\"detailh\">Rok</td></tr>"
                   + "<tr class=\"r\"><td><img alt=\"CZ\" src=\"x.gif\" /></td><td>x</td><td><a href=\"/Film-A-77.htm\">Film &amp; A</a></td>"
                   + "<td><a class=\"listTip\" title=\"Film.A.2020.WEB&amp;DL\"><img/></a></td><td>2020</td></tr></table>";
        var r = Assert.Single(Titulky.ParseSearch(html));
        Assert.Equal((77, "Film & A", "Film.A.2020.WEB&DL", (int?)2020, "CZ", "Film-A-77.htm"), (r.Id, r.Title, r.Release, r.Year, r.Lang, r.Href));
    }

    [Fact]
    public void Parse_detail_preview_download_and_login_real_pages()
    {
        Assert.Equal(new TitulkyDetail("23.976", 1, "srt", "Breaking.Bad.S01E05.Gray.Matter.720p.BRRip.x264.AC3", "tt0903747"),
                     Titulky.ParseDetail(Page("detail_breaking_bad_s01e05.html")));
        Assert.Equal(new TitulkyDetail("", 0, "", "", ""), Titulky.ParseDetail(""));
        // trang tai that (vuot gioi han theo IP): co CAPTCHA + form, chua co link
        var p = Titulky.ParseDownloadPage(Page("idown_captcha.html"));
        Assert.Equal(("./captcha/captcha.php", "", 0), (p.Captcha, p.Link, p.Wait));
        Assert.Equal([("downkod", ""), ("securedown", "2"), ("zip", ""), ("T", ""), ("titulky", "115664"), ("histstamp", "")],
                     p.Form.Select(kv => (kv.Key, kv.Value)));
        Assert.Contains("denní limit", p.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowLoader", p.Message, StringComparison.Ordinal);
        // trang cho (dung theo downfnc.js that)
        var w = Titulky.ParseDownloadPage(CountdownPage(6, "idown.php?id=9&amp;x=1"));
        Assert.Equal(("idown.php?id=9&x=1", 6, ""), (w.Link, w.Wait, w.Captcha));
        var js = Page("downfnc.js");
        Assert.Contains("15e2", js, StringComparison.Ordinal);
        Assert.Contains("getElementById('downlink')", js, StringComparison.Ordinal);
        Assert.Equal(1.5, Titulky.CountdownStep);
        // form dang nhap that: ten o dung nhu client gui
        var form = Page("home_login_form.html");
        Assert.True(Titulky.HasLoginForm(form));
        Assert.False(Titulky.HasLoginForm("<html><a href='?logoff=1'>Odhlásit</a></html>"));
        foreach (var name in new[] { "Login", "Password", "foreverlog", "Detail2", "prihlasit" })
            Assert.Contains($"name=\"{name}\"", form, StringComparison.Ordinal);
        // xem truoc: <br /> -> xuong dong, doc duoc moc thoi gian
        var prev = "<html><body><div id=\"nahled_zdroj\" style=\"x\">1<br />00:00:07,432 --&gt; 00:00:09,434<br />A &amp; B<br /><br />"
                   + "2<br />00:02:47,862 --&gt; 00:02:49,280<br />C<br /></div></body></html>";
        var txt = Titulky.ParsePreview(prev);
        Assert.Contains("00:00:07,432 --> 00:00:09,434\nA & B", txt, StringComparison.Ordinal);
        Assert.Equal([7.432, 167.862], Srt.PreviewTimes(txt).Select(x => Math.Round(x, 6)));
        Assert.Equal("", Titulky.ParsePreview("<html></html>"));
    }

    [Fact]
    public void Search_query_and_html_sniffing()
    {
        Assert.Equal("Breaking Bad S01E05", Titulky.SearchQuery("Breaking  Bad", 1, 5));
        Assert.Equal("Dune", Titulky.SearchQuery(" Dune "));
        Assert.True(Titulky.LooksLikeHtml([0xEF, 0xBB, 0xBF, (byte)'\n', (byte)' ', .. "<!DOCTYPE html><html>"u8]));
        Assert.True(Titulky.LooksLikeHtml("<html>"u8));
        Assert.True(Titulky.LooksLikeHtml("<!--\r\n** trang that cua Titulky bat dau bang chu thich -->"u8));
        Assert.True(Titulky.LooksLikeHtml("\t\t<!DOCTYPE html>"u8));
        Assert.False(Titulky.LooksLikeHtml("{1}{25}MicroDVD"u8));
        Assert.False(Titulky.LooksLikeHtml(""u8));
        Assert.False(Titulky.LooksLikeHtml("PK\x03\x04"u8));
        Assert.False(Titulky.LooksLikeHtml("1\r\n00:00:01,000 --> 00:00:02,000"u8));
        Assert.False(Titulky.LooksLikeHtml("[Script Info]"u8));
        Assert.False(Titulky.LooksLikeHtml([0xEF, 0xBB, 0xBF, (byte)'1', (byte)'\n']));
    }

    [Fact]
    public void Title_fragments_for_names_that_lost_punctuation()
    {
        Assert.Equal(["The Return of the King", "The Lord of the Rings", "The Return of", "The Lord of"],
                     Titulky.TitleFragments("The Lord of the Rings The Return of the King"));
        var f = Titulky.TitleFragments("Star Wars Episode IV A New Hope");
        Assert.Equal(["Star Wars Episode IV", "A New Hope"], f.Take(2));
        Assert.Contains("Episode IV", f);
        Assert.Equal(["Schindlers"], Titulky.TitleFragments("Schindlers List")); // "List" qua chung chung -> bo
        Assert.Empty(Titulky.TitleFragments("Dune"));
        Assert.Empty(Titulky.TitleFragments(""));
        Assert.Empty(Titulky.TitleFragments("Up It"));
        Assert.Equal(["The Return of the King"], Titulky.TitleFragments("The Lord of the Rings The Return of the King", limit: 1));
        Assert.True(Titulky.TitleFragments("a b c d e f g h i j k l m n o p q r s t").Count <= 4);
    }

    [Fact]
    public void Real_fragment_results_page_of_the_return_of_the_king()
    {
        var rows = Titulky.ParseSearch(Page("search_the_return_of_the_king.html"));
        var titles = rows.Select(r => r.Title).ToList();
        Assert.Equal(3, titles.Count(t => t == "The Lord of the Rings: The Return of the King"));
        Assert.Contains("King of Boys: The Return of the King S01E01", titles);
        Assert.Contains("The Lord of the Rings: The Return of the King [EXTENDED]", titles);
        Assert.Superset(new HashSet<int?> { 2003, 1980, 2021 }, rows.Select(r => r.Year).ToHashSet());
    }

    private static string RowsHtml(int start, int n, string title = "Film X")
    {
        var head = "<tr><td class=\"detailh ucase\">Název</td><td class=\"detailh ucase\">V.</td><td class=\"detailh ucase\">Seriál</td>"
                   + "<td class=\"detailh ucase\">Rok</td><td class=\"detailh ucase\">Staž.</td><td class=\"detailh ucase\">Jazyk</td>"
                   + "<td class=\"detailh ucase\">Uložil</td></tr>";
        var rows = string.Concat(Enumerable.Range(start, n).Select(i =>
            $"<tr class=\"r\"><td><a href=\"Film-X-{i}.htm\" >{title}</a></td><td></td><td>&nbsp;</td><td>2020</td>"
            + $"<td align=\"right\">{i}</td><td><img alt=\"CZ\" src=\"x.gif\" /></td><td>u</td></tr>"));
        return $"<html><table class=\"soupis\">{head}{rows}</table></html>";
    }

    private static int Rec(FakeCall c) => c.Params.TryGetValue("ActualRecord", out var v) ? int.Parse(v, System.Globalization.CultureInfo.InvariantCulture) : 0;

    [Fact]
    public void Search_pages_use_real_paging_parameters_and_cache()
    {
        using var tmp = new TempDir();
        var http = new FakeHttp();
        http.Add("GET", @"titulky\.com/$", call => FakeResponse.Text(200, RowsHtml(1 + Rec(call), Rec(call) switch { 0 => 50, 50 => 50, 100 => 20, _ => 0 })));
        var (c, _, _) = Client(http, tmp.Path);
        Assert.Equal(50, c.Search("Film").Count);
        Assert.Single(http.Calls); // mac dinh chi mot trang
        var rows = c.Search("Film", pages: 4);
        Assert.Equal(120, rows.Count);
        Assert.Equal(120, rows.Select(r => r.Id).Distinct().Count()); // trang 3 chua day -> dung, khong goi trang 4
        Assert.Equal(["Fulltext=Film", "Fulltext=Film&ZaznamuStrana=50&ActualRecord=50", "Fulltext=Film&ZaznamuStrana=50&ActualRecord=100"],
                     http.Calls.Select(x => string.Join("&", x.Params.Select(kv => $"{kv.Key}={kv.Value}"))));
        c.Search("film", pages: 4);
        Assert.Equal(3, http.Calls.Count);
        Assert.Equal(120, c.Search("FILM").Count); // nho dem
        var http2 = new FakeHttp();
        http2.Add("GET", @"titulky\.com/$", call => FakeResponse.Text(200, RowsHtml(1 + Rec(call), 50)));
        var (c3, _, _) = Client(http2, tmp.Path);
        Assert.Equal(100, c3.Search("Nhieu", pages: 2).Count);
        Assert.Equal(2, http2.Calls.Count); // ton trong gioi han so trang
    }

    // ---------------------------------------------------------------- client voi may chu gia

    [Fact]
    public void Search_detail_preview_use_real_urls_cache_and_pause()
    {
        using var tmp = new TempDir();
        var http = new FakeHttp();
        http.Add("GET", @"titulky\.com/nahled\.php", FakeResponse.Text(200, "<div id=\"nahled_zdroj\">1<br />00:00:05,000 --&gt; 00:00:06,000<br />x</div>"));
        http.Add("GET", @"titulky\.com/Breaking-Bad-S01E05-232758\.htm", FakeResponse.Text(200, Page("detail_breaking_bad_s01e05.html")));
        http.Add("GET", @"titulky\.com/$", FakeResponse.Text(200, Page("search_breaking_bad_s01e05.html")));
        var (c, _, sl) = Client(http, tmp.Path);
        var rows = c.Search("Breaking Bad S01E05");
        Assert.Equal(4, rows.Count);
        Assert.Same(rows, c.Search("breaking bad  s01e05")); // nho dem, khong goi lai
        var call = http.Calls[0];
        Assert.Equal("https://www.titulky.com/", call.Url);
        Assert.Equal([new("Fulltext", "Breaking Bad S01E05")], call.Params);
        Assert.Contains("MuxStudio", call.Headers["User-Agent"], StringComparison.Ordinal);
        Assert.DoesNotContain("Chrome", call.Headers["User-Agent"], StringComparison.Ordinal);
        var row = rows.First(r => r.Id == 232758);
        c.FillDetail(row);
        c.FillDetail(row);
        Assert.Equal(("23.976", 1, "srt", "tt0903747"), (row.Fps, row.Cds, row.Kind, row.Imdb));
        Assert.Equal([5.0], Srt.PreviewTimes(c.Preview(row)));
        Assert.NotEmpty(c.Preview(row));
        Assert.Equal(3, http.Calls.Count); // 1 tim + 1 chi tiet + 1 xem truoc
        Assert.Equal([new("Titulky", "232758"), new("histstamp", "")], http.Calls[2].Params);
        Assert.Equal(row.Url, http.Calls[2].Headers["Referer"]);
        Assert.Equal([1.1, 1.1], sl.Calls); // nghi giua cac luot goi
        Assert.Empty(c.Search(""));
    }

    [Fact]
    public void Download_with_captcha_wrong_then_right_waits_full_countdown_and_unzips()
    {
        using var tmp = new TempDir();
        TextEncoding.EnsureCodePages();
        var sub = Encoding.GetEncoding(1250).GetBytes("1\r\n00:00:01,000 --> 00:00:02,000\r\nPříliš žluťoučký\r\n"); // bang ma cu
        var http = new FakeHttp();
        http.Add("GET", @"idown\.php\?id=abc123", FakeResponse.Bytes(200, ZipData.Of(("Breaking.Bad.S01E05.srt", sub), ("info.nfo", "x"u8.ToArray()))));
        http.Add("GET", @"captcha/captcha\.php", FakeResponse.Bytes(200, [0xFF, 0xD8, 0xFF, 0xE0, (byte)'J', (byte)'P', (byte)'E', (byte)'G']));
        http.Add("POST", @"titulky\.com/idown\.php$", FakeResponse.Text(200, Page("idown_captcha.html")), FakeResponse.Text(200, CountdownPage(6)));
        http.Add("GET", @"titulky\.com/idown\.php$", FakeResponse.Text(200, Page("idown_captcha.html")));
        var (c, job, sl) = Client(http, tmp.Path);
        var asked = new List<(byte[] Img, string Note)>();
        string? Captcha(byte[] img, string note)
        {
            asked.Add((img, note));
            return asked.Count == 1 ? " 7KQ2 " : "x9y8";
        }
        var row = new SearchRow(232758, "Breaking Bad S01E05", "Breaking-Bad-S01E05-232758.htm", "Breaking.Bad.S01E05.720p");
        var d = c.Download(row, Captcha);
        var files = c.SubtitleFiles(d);
        Assert.Equal(["Breaking.Bad.S01E05.srt"], files.Select(Path.GetFileName));
        Assert.Equal(sub, File.ReadAllBytes(files[0])); // NGUYEN byte: buoc ① khong doi bang ma
        var first = http.CallsTo(@"idown\.php$", "GET")[0];
        Assert.Equal([new("R", "1794415365"), new("titulky", "232758"), new("zip", "z"), new("histstamp", "")], first.Params);
        Assert.Equal(row.Url, first.Headers["Referer"]);
        var posts = http.CallsTo(@"idown\.php$", "POST");
        Assert.Equal(["7KQ2", "x9y8"], posts.Select(p => p.Data!["downkod"]));
        Assert.Equal([("downkod", "7KQ2"), ("securedown", "2"), ("zip", ""), ("T", ""), ("titulky", "115664"), ("histstamp", "")],
                     posts[0].Data!.Select(kv => (kv.Key, kv.Value)));
        Assert.Equal(2, asked.Count);
        Assert.Equal([0xFF, 0xD8, 0xFF, 0xE0, (byte)'J', (byte)'P', (byte)'E', (byte)'G'], asked[0].Img);
        Assert.Equal("Breaking.Bad.S01E05.720p", asked[0].Note);
        Assert.Contains("không đúng", asked[1].Note, StringComparison.Ordinal);
        // cho du: 6 nhip x 1,5 s (+0,5) = 9,5 s, chia tung 0,5 s
        Assert.Equal(19, sl.Calls.Count(x => x == 0.5));
        Assert.True(Has(job, "chờ 10 giây") || Has(job, "chờ 9 giây"));
        Assert.Equal("https://www.titulky.com/idown.php?id=abc123", http.Calls[^1].Url);
        Assert.EndsWith("idown.php", http.Calls[^1].Headers["Referer"], StringComparison.Ordinal);
        var n = http.Calls.Count;
        Assert.Equal(d, c.Download(row, Captcha));
        Assert.Equal(n, http.Calls.Count); // nho dem: khong tai lai, khong hoi lai
    }

    [Fact]
    public void Download_without_captcha_plain_file_and_vip_like_direct_zip()
    {
        using var tmp = new TempDir();
        var http = new FakeHttp();
        http.Add("GET", @"idown\.php\?id=abc123", FakeResponse.Text(200, "[Script Info]\nTitle: x\n[Events]\nDialogue: 0,0:00:01.00,0:00:02.00,D,,0,0,0,,a\n",
                                                                   ("Content-Disposition", "attachment; filename=\"Film.A.2020.ass\"")));
        http.Add("GET", @"titulky\.com/idown\.php$", call => call.Params["titulky"] == "5"
            ? FakeResponse.Text(200, CountdownPage(2))
            : FakeResponse.Bytes(200, ZipData.Of(("a/Film.B.srt", "1\n00:00:01,000 --> 00:00:02,000\nx\n"))));
        var (c, _, _) = Client(http, tmp.Path);
        string? Never(byte[] img, string note) => throw new InvalidOperationException("không được hỏi CAPTCHA");
        var d5 = c.Download(new SearchRow(5, "Film A", "Film-A-5.htm"), Never);
        Assert.Equal(["Film.A.2020.ass"], c.SubtitleFiles(d5).Select(Path.GetFileName));
        var d6 = c.Download(new SearchRow(6, "Film B", "Film-B-6.htm"), Never); // may chu tra thang ZIP
        Assert.Equal(["Film.B.srt"], c.SubtitleFiles(d6).Select(Path.GetFileName));
        Assert.NotEqual(d5, d6);
        Assert.Equal(Path.Combine(tmp.Path, "tt"), Path.GetDirectoryName(d5));
    }

    [Fact]
    public void Download_captcha_skipped_once_is_not_asked_again_and_other_failures()
    {
        using var tmp = new TempDir();
        var http = new FakeHttp();
        http.Add("GET", @"captcha/captcha\.php", FakeResponse.Bytes(200, [0xFF, 0xD8, (byte)'J']));
        http.Add("GET", @"titulky\.com/idown\.php$", FakeResponse.Text(200, Page("idown_captcha.html")));
        var (c, job, _) = Client(http, tmp.Path);
        var asked = new List<string>();
        string? Skip(byte[] img, string note)
        {
            asked.Add(note);
            return null;
        }
        Assert.Null(c.Download(new SearchRow(1, "A", "A-1.htm"), Skip));
        Assert.True(c.CaptchaDeclined);
        Assert.Null(c.Download(new SearchRow(2, "B", "B-2.htm"), Skip));
        Assert.Single(asked); // ban bo qua mot lan -> khong hoi nua trong luot nay
        Assert.Equal(1, job.Lines.Count(l => l.Message.Contains("bỏ qua CAPTCHA", StringComparison.Ordinal)));
        Assert.True(Has(job, "đã bỏ qua trong lượt này"));
        // khong co cua so nhap ma
        var (c2, job2, _) = Client(http, tmp.Path);
        Assert.Null(c2.Download(new SearchRow(3, "C", "C-3.htm"), null));
        Assert.True(Has(job2, "không có cửa sổ để nhập"));
        // sai 3 lan lien tiep
        http.Add("POST", @"titulky\.com/idown\.php$", FakeResponse.Text(200, Page("idown_captcha.html")));
        var (c3, job3, _) = Client(http, tmp.Path);
        Assert.Null(c3.Download(new SearchRow(4, "D", "D-4.htm"), (_, _) => "sai"));
        Assert.Equal(3, http.CallsTo(@"idown\.php$", "POST").Count);
        Assert.True(Has(job3, "sai 3 lần"));
        // trang khong co link, khong co CAPTCHA (vd het luot) -> bao nguyen van loi; RAR -> can 7-Zip
        var h2 = new FakeHttp();
        h2.Add("GET", @"titulky\.com/idown\.php$", call => call.Params["titulky"] == "7"
            ? FakeResponse.Text(200, "<html><body><h3>Limit vyčerpán</h3></body></html>")
            : FakeResponse.Bytes(200, [.. "Rar!\x1a\x07\x00"u8, .. Enumerable.Repeat((byte)'0', 40)]));
        var (c4, job4, _) = Client(h2, tmp.Path);
        Assert.Null(c4.Download(new SearchRow(7, "E", "E-7.htm"), Skip));
        Assert.Null(c4.Download(new SearchRow(8, "F", "F-8.htm"), Skip));
        var text = AllText(job4);
        Assert.Contains("Titulky không cho tải: Limit vyčerpán", text, StringComparison.Ordinal);
        Assert.Contains("RAR", text, StringComparison.Ordinal);
    }

    private static string LoginErrorPage(string name)
    {
        var url = $"/?ShowInfo=LoginError&BadId=&BadLogin={name}";
        return $"<meta http-equiv=\"Refresh\" content=\"0.1;URL={url}\">\t\t\t\t\t<script>window.location.assign(\"{url}\")</script>";
    }

    private static FakeHttp SiteWithLogin((string, string)? valid)
    {
        var form = Page("home_login_form.html");
        const string homeIn = "<html><body><a href='/?logoff=1'>Odhlásit</a></body></html>";
        var http = new FakeHttp();
        var loggedIn = false;
        http.Add("POST", @"titulky\.com/$", call =>
        {
            if (valid is { } v && (call.Data!["Login"], call.Data["Password"]) == v)
            {
                loggedIn = true;
                return FakeResponse.Text(200, "<meta http-equiv=\"Refresh\" content=\"0.1;URL=/?welcome=\">\t\t\t\t\t"
                                              + "<script>window.location.assign(\"/?welcome=\")</script>");
            }
            return FakeResponse.Text(200, LoginErrorPage(call.Data!["Login"]));
        });
        http.Add("GET", @"titulky\.com/$", _ => FakeResponse.Text(200, loggedIn ? homeIn : form));
        return http;
    }

    [Fact]
    public void Login_real_field_names_and_connection_check()
    {
        using var tmp = new TempDir();
        var http = SiteWithLogin(("anh", "demo-pass"));
        var (c, _, _) = Client(http, tmp.Path, " anh ", "demo-pass");
        Assert.True(c.Login());
        Assert.True(c.LoggedIn);
        Assert.Equal("", c.LoginNote);
        var post = http.CallsTo(@"titulky\.com/$", "POST")[0];
        Assert.Equal([("Login", "anh"), ("Password", "demo-pass"), ("foreverlog", "1"), ("Detail2", ""), ("prihlasit", "Přihlásit")],
                     post.Data!.Select(kv => (kv.Key, kv.Value)));
        Assert.Equal("GET", http.Calls[^1].Method); // sau khi gui: xem lai trang chu
        Assert.True(c.Check().Ok);
        Assert.Equal("Titulky: đăng nhập được", c.Check().Text);
        // sai mat khau
        var bad = SiteWithLogin(("anh", "demo-pass"));
        var (c2, job2, _) = Client(bad, tmp.Path, "anh", "demo-sai");
        Assert.False(c2.Login());
        Assert.False(c2.LoggedIn);
        Assert.Equal("rejected", c2.LoginNote);
        Assert.Single(bad.Calls);
        var msg = AllText(job2);
        Assert.Contains("Titulky từ chối tên đăng nhập / mật khẩu (trang báo LoginError)", msg, StringComparison.Ordinal);
        Assert.Contains("kiểu khách", msg, StringComparison.Ordinal);
        var st = c2.Check();
        Assert.False(st.Ok);
        Assert.StartsWith("Titulky: đăng nhập lỗi — Titulky từ chối tên đăng nhập / mật khẩu", st.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("demo-sai", msg, StringComparison.Ordinal);
        Assert.DoesNotContain("demo-sai", st.Text, StringComparison.Ordinal);
        // trang khong bao loi nhung trang chu van hien o dang nhap
        var odd = new FakeHttp();
        odd.Add("POST", @"titulky\.com/$", FakeResponse.Text(200, "<html>ok?</html>"));
        odd.Add("GET", @"titulky\.com/$", FakeResponse.Text(200, Page("home_login_form.html")));
        var (c5, job5, _) = Client(odd, tmp.Path, "anh", "demo-pass");
        Assert.False(c5.Login());
        Assert.Equal("unconfirmed", c5.LoginNote);
        Assert.True(Has(job5, "trang chủ vẫn hiện ô đăng nhập"));
        // khong tai khoan: kieu khach, khong gui form dang nhap
        var (c3, _, _) = Client(bad, tmp.Path);
        var n = bad.CallsTo(@"titulky\.com/$", "POST").Count;
        st = c3.Check();
        Assert.True(st.Ok);
        Assert.Contains("kiểu khách", st.Text, StringComparison.Ordinal);
        Assert.Null(c3.LoggedIn);
        Assert.False(c3.Login());
        Assert.Equal(n, bad.CallsTo(@"titulky\.com/$", "POST").Count);
        // may chu loi
        var down = new FakeHttp().Add("GET", @"titulky\.com/$", FakeResponse.Text(500, "x"));
        var (c4, _, _) = Client(down, tmp.Path);
        Assert.False(c4.Check().Ok);
        Assert.Contains("500", c4.Check().Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Download_replays_the_real_run_of_2026_11_12()
    {
        using var tmp = new TempDir();
        TextEncoding.EnsureCodePages();
        var srtCp1250 = Encoding.GetEncoding(1250).GetBytes("1\r\n00:00:01,000 --> 00:00:02,000\r\nPříliš žluťoučký kůň\r\n");
        var wrong = Page("idown_captcha.html").Replace("<h3>Zabezpečené", "<b>CHYBA - Byl špatně zadaný bezpečnostní kód</b><h3>Zabezpečené", StringComparison.Ordinal);
        const string ready = "<!DOCTYPE html><html><body><div id=\"imgLoader\"></div><div id=\"razenidiv\"></div><span id=\"count_down\">10</span>"
                             + "<div id=\"downdiv\" style=\"display:none\"><a id=\"downlink\" href=\"/idown.php?id=100196299\">STÁHNOUT TITULKY</a></div>"
                             + "<div id=\"afterdown\"></div><script>CountDown(10)</script></body></html>";
        var http = new FakeHttp();
        http.Add("GET", @"titulky\.com/idown\.php\?id=100196299$", FakeResponse.Bytes(200, ZipData.Of(("Breaking Bad S01E05.srt", srtCp1250)),
            ("Content-Type", "application/octet-stream"), ("Content-Disposition", "attachment; filename=Breaking-Bad-S01E05(0000126732).zip")));
        http.Add("GET", @"captcha/captcha\.php", FakeResponse.Bytes(200, [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F'],
                                                                    ("Content-Type", "image/jpeg")));
        http.Add("POST", @"titulky\.com/idown\.php$", FakeResponse.Text(200, wrong), FakeResponse.Text(200, ready));
        http.Add("GET", @"titulky\.com/idown\.php$", FakeResponse.Text(200, Page("idown_captcha.html")));
        var (c, job, sl) = Client(http, tmp.Path);
        var notes = new List<string>();
        var row = new SearchRow(126732, "Breaking Bad S01E05", "Breaking-Bad-S01E05-126732.htm", "Breaking.Bad.S01E05.DVDRip.XviD-ORPHEUS");
        var d = c.Download(row, (_, note) =>
        {
            notes.Add(note);
            return $"ma{notes.Count}";
        });
        var f = Assert.Single(c.SubtitleFiles(d));
        Assert.Equal("Breaking Bad S01E05.srt", Path.GetFileName(f));
        Assert.Equal(srtCp1250, File.ReadAllBytes(f)); // nguyen byte (cp1250)
        Assert.Equal(2, notes.Count);
        Assert.Contains("không đúng", notes[1], StringComparison.Ordinal);
        Assert.Equal("https://www.titulky.com/idown.php?id=100196299", http.Calls[^1].Url);
        Assert.Equal(31, sl.Calls.Count(x => x == 0.5)); // CountDown(10): 10 x 1,5 + 0,5 = 15,5 s
        Assert.True(Has(job, "chờ 16 giây"));
        Assert.Equal("legacy", TextEncoding.DetectKindBytes(File.ReadAllBytes(f)));
    }

    [Fact]
    public void Stop_now_interrupts_the_countdown()
    {
        using var tmp = new TempDir();
        var http = new FakeHttp().Add("GET", @"titulky\.com/idown\.php$", FakeResponse.Text(200, CountdownPage(40)));
        var (c, job, sl) = Client(http, tmp.Path);
        c.Sleep = s =>
        {
            sl.Calls.Add(s);
            if (sl.Calls.Count == 5)
                job.CancelNow();
        };
        Assert.ThrowsAny<OperationCanceledException>(() => c.Download(new SearchRow(9, "G", "G-9.htm"), null));
        Assert.Equal(5, sl.Calls.Count); // dung sau 5 nhip 0,5 s, khong cho het 60 s
        Assert.Matches(@"idown\.php$", http.Calls[^1].Url); // chua goi link tai
    }
}
