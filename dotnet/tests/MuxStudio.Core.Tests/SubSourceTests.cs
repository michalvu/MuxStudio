using MuxStudio.Core.Tests.Support;
using static MuxStudio.Core.Tests.Support.PyData;
using JDict = System.Collections.Generic.OrderedDictionary<string, object?>;

namespace MuxStudio.Core.Tests;

/// <summary>SubSource (giả lập) — §6.11. Port <c>tests/test_subsource.py</c>.</summary>
[Collection(GlobalStateCollection.Name)] // bang ngon ngu (Naming.GetLang)
public class SubSourceTests
{
    private const string Base = "https://api.subsource.net/api/v1";

    private static (SubSourceClient C, Sleeper S, Job J) Client(FakeHttp http, int pauseMs = 1100, int maxPages = 10, string? cacheDir = null)
    {
        var sl = new Sleeper();
        var job = new Job();
        return (new SubSourceClient("demo-ss-key", Base, pauseMs, maxPages, http.Client, sl.Sleep, sl.Clock, job, cacheDir), sl, job);
    }

    private static FakeResponse Ok(object data, params (string, string)[] headers) => FakeResponse.Json(200, data, headers);

    private static KeyValuePair<string, object?> P(string k, object? v) => new(k, v);

    [Fact]
    public void Header_and_pacing_between_calls()
    {
        var http = new FakeHttp().Add("GET", "/movies/search", Ok(D(("data", L()))));
        var (c, sl, _) = Client(http);
        c.Get("/movies/search", [P("searchType", "text"), P("q", "A"), P("empty", "")]);
        c.Get("/movies/search", [P("searchType", "text"), P("q", "B")]);
        Assert.Equal("demo-ss-key", http.Calls[0].Headers["X-API-Key"]);
        Assert.False(http.Calls[0].Params.ContainsKey("empty")); // bo tham so rong nhu .bat
        Assert.Equal(1.1, Assert.Single(sl.Calls), 6); // nghi ~1,1 s giua 2 luot, luot dau khong nghi
    }

    [Fact]
    public void Status_429_waits_20s_then_retries_and_4xx_stops()
    {
        var http = new FakeHttp().Add("GET", "/x", FakeResponse.Status_(429), Ok(D(("data", L(1L)))));
        var (c, sl, _) = Client(http);
        Assert.True(Py.Equal(D(("data", L(1L))), c.Get("/x")));
        Assert.Contains(20.0, sl.Calls);
        var http2 = new FakeHttp().Add("GET", "/y", FakeResponse.Text(403, "forbidden"));
        var (c2, _, job) = Client(http2);
        Assert.Null(c2.Get("/y"));
        Assert.Single(http2.Calls);
        Assert.Contains(job.Lines, l => l.Message.Contains("403", StringComparison.Ordinal));
    }

    [Fact]
    public void Find_entry_imdb_first_then_text_and_season_filter()
    {
        var http = new FakeHttp().Add("GET", "/movies/search", call => call.Params["searchType"] == "imdb"
            ? Ok(D(("data", L())))
            : Ok(D(("data", L(D(("movieId", 11L), ("title", "Show"), ("season", 1L)), D(("movieId", 12L), ("title", "Show"), ("season", 2L)))))));
        var (c, _, job) = Client(http);
        Assert.Equal(12L, c.FindEntry("Show", null, 2, "tt123")!["movieId"]);
        Assert.Equal(["imdb", "text"], http.Calls.Select(x => x.Params["searchType"]));
        Assert.Null(c.FindEntry("Show", null, 5, "tt123"));
        Assert.Contains(job.Lines, l => l.Message.Contains("Không có season 5", StringComparison.Ordinal) && l.Message.Contains("1, 2", StringComparison.Ordinal));
    }

    [Fact]
    public void Find_entry_movie_year_and_title()
    {
        var data = L(D(("movieId", 1L), ("title", "Dune"), ("releaseYear", 1984L)), D(("movieId", 2L), ("title", "Dune"), ("releaseYear", 2021L)),
                     D(("movieId", 3L), ("title", "Dune Part Two"), ("releaseYear", 2024L)));
        var http = new FakeHttp().Add("GET", "/movies/search", Ok(D(("data", data))));
        var (c, _, _) = Client(http);
        Assert.Equal(2L, c.FindEntry("Dune", 2021, null)!["movieId"]);
        Assert.Equal(3L, c.FindEntry("Dune Part Two", null, null)!["movieId"]);
    }

    [Fact]
    public void List_all_no_server_side_filter_limit_fallback_and_pagination()
    {
        var http = new FakeHttp().Add("GET", "/subtitles$", call =>
        {
            if (call.Params["limit"] == "100")
                return FakeResponse.Text(400, "limit too large");
            var page = long.Parse(call.Params["page"], System.Globalization.CultureInfo.InvariantCulture);
            return Ok(D(("data", L(D(("subtitleId", page * 10), ("language", "vietnamese")), D(("subtitleId", page * 10 + 1), ("language", "vietnamese")))),
                        ("pagination", D(("pages", 3L)))));
        });
        var (c, _, _) = Client(http);
        Assert.Equal([10L, 11L, 20L, 21L, 30L, 31L], c.ListAll(99).Select(s => (long)s["subtitleId"]!));
        foreach (var call in http.Calls)
            Assert.Subset(new HashSet<string> { "movieId", "page", "limit" }, call.Params.Keys.ToHashSet()); // KHONG loc phia may chu
        Assert.Equal(["100", "20", "20", "20"], http.Calls.Select(x => x.Params["limit"]));
        var n = http.Calls.Count;
        c.ListAll(99);
        Assert.Equal(n, http.Calls.Count); // bo nho dem
    }

    [Fact]
    public void List_all_respects_max_pages()
    {
        var http = new FakeHttp().Add("GET", "/subtitles$", call => Ok(D(("data", L(D(("subtitleId", call.Params["page"])))), ("pagination", D(("pages", 50L))))));
        var (c, _, _) = Client(http, maxPages: 4);
        Assert.Equal(4, c.ListAll(1).Count);
    }

    [Theory]
    [InlineData("vietnamese", "vi", true)]
    [InlineData("Vietnamese ", "vi", true)]
    [InlineData("viet", "vi", true)]
    [InlineData("vi-vn", "vi", true)]
    [InlineData("vi_VN", "vi", true)]
    [InlineData("czech", "cs", true)]
    [InlineData("cze", "cs", true)]
    [InlineData("cs-cz", "cs", true)]
    [InlineData("english", "en", true)]
    [InlineData("en-us", "en", true)]
    [InlineData("chinese_simplified", "zh", true)]
    [InlineData("indonesian", "id", true)]
    [InlineData("", "vi", false)]
    [InlineData(null, "vi", false)]
    [InlineData("vietnamese", "en", false)]
    [InlineData("english", "cs", false)]
    public void Lang_matches(string? lang, string code, bool ok) => Assert.Equal(ok, SubSource.LangMatches(D(("language", lang)), code));

    [Fact]
    public void Lang_summary()
    {
        var s = Enumerable.Repeat(D(("language", "english")), 3).Append(D(("language", "vietnamese"))).Concat(Enumerable.Repeat(D(("language", "")), 2));
        Assert.Equal(["english(3)", "(trống)(2)", "vietnamese(1)"], SubSource.LangSummary(s));
    }

    [Fact]
    public void Download_pack_zip_cache_and_zip_slip()
    {
        using var tmp = new TempDir();
        var z = ZipData.Of(("Show.S01E01.srt", "a"), ("sub/Show.S01E02.srt", "b"), ("../../evil.srt", "x"), ("..\\evil2.srt", "y"));
        var http = new FakeHttp().Add("GET", "/subtitles/5/download", FakeResponse.Bytes(200, z));
        var (c, _, _) = Client(http, cacheDir: tmp["packs"]);
        var d = c.DownloadPack(5)!;
        var names = SubSource.PackFiles(d).Select(p => Path.GetRelativePath(d, p)).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "Show.S01E01.srt", Path.Combine("sub", "Show.S01E02.srt"), "evil.srt", "evil2.srt" }.Order(StringComparer.Ordinal), names);
        Assert.False(File.Exists(tmp["evil.srt"])); // khong thoat ra ngoai
        Assert.Equal(d, c.DownloadPack(5));
        Assert.Single(http.Calls); // tai MOT lan
        Assert.True(c.IsDownloaded(5));
    }

    [Fact]
    public void Download_pack_rejects_rar()
    {
        using var tmp = new TempDir();
        var http = new FakeHttp().Add("GET", "/download", FakeResponse.Bytes(200, "Rar!\x1a\x07\x00rest"u8.ToArray()));
        var (c, _, job) = Client(http, cacheDir: tmp.Path);
        Assert.Null(c.DownloadPack(6));
        Assert.Contains(job.Lines, l => l.Message.Contains("RAR", StringComparison.Ordinal));
    }

    private static string MakePack(TempDir tmp, params string[] names)
    {
        var d = tmp["[pack] goi"];
        Directory.CreateDirectory(d);
        foreach (var n in names)
            File.WriteAllText(Path.Combine(d, n), "1\n00:00:01,000 --> 00:00:02,000\nx\n", Py.Utf8NoBom);
        return d;
    }

    [Fact]
    public void Select_from_pack_right_episode_and_exclusions()
    {
        using var tmp = new TempDir();
        var d = MakePack(tmp, "Show.S01E01.srt", "Show.S01E02.srt", "Show.S01E02.forced.srt", "Show.S01E02.SDH.srt", "Show.S01E02.HI.srt", "Show.S01E03.ass");
        var vt = Naming.TitleTokens("Show.S01E02.1080p");
        Assert.Equal("Show.S01E02.srt", Path.GetFileName(SubSource.SelectFromPack(d, Episodes.ParseEpisode("Show.S01E02"), vt)));
        Assert.Equal("Show.S01E03.ass", Path.GetFileName(SubSource.SelectFromPack(d, Episodes.ParseEpisode("Show.S01E03"), vt)));
        Assert.Null(SubSource.SelectFromPack(d, Episodes.ParseEpisode("Show.S01E09"), vt)); // goi khong co tap nay
    }

    [Fact]
    public void Select_from_pack_anime_absolute_numbers()
    {
        using var tmp = new TempDir();
        var d = MakePack(tmp, Enumerable.Range(641, 5).Select(i => $"[SubGroup] One Piece - {i:0000}.srt").ToArray());
        var vt = Naming.TitleTokens("[Judas] One Piece - 0643 [1080p]");
        Assert.Equal("[SubGroup] One Piece - 0643.srt", Path.GetFileName(SubSource.SelectFromPack(d, Episodes.ParseEpisode("[Judas] One Piece - 0643 [1080p]"), vt)));
        Assert.Equal("[SubGroup] One Piece - 0643.srt", Path.GetFileName(SubSource.SelectFromPack(d, Episodes.ParseEpisode("One.Piece.S16E643"), vt)));
    }

    [Fact]
    public void Select_from_pack_only_forced_returns_none_and_movie_by_name()
    {
        using var tmp = new TempDir();
        var d = MakePack(tmp, "Movie.forced.srt", "Movie.SDH.srt");
        Assert.Null(SubSource.SelectFromPack(d, null, ["movie"]));
        var d2 = tmp["m"];
        Directory.CreateDirectory(d2);
        foreach (var n in new[] { "Other.Film.srt", "Dune.Part.Two.2024.srt" })
            File.WriteAllText(Path.Combine(d2, n), "x", Py.Utf8NoBom);
        Assert.Equal("Dune.Part.Two.2024.srt", Path.GetFileName(SubSource.SelectFromPack(d2, null, Naming.TitleTokens("Dune.Part.Two.2024.2160p"))));
    }

    [Fact]
    public void Check_reports_rate_headers()
    {
        var http = new FakeHttp().Add("GET", "/movies/search", Ok(D(("data", L())), ("X-RateLimit-Limit", "60"), ("X-RateLimit-Remaining", "58"),
                                                               ("Content-Type", "application/json")));
        var (c, _, _) = Client(http);
        var st = c.Check();
        Assert.True(st.Ok);
        Assert.Contains("58/60", st.Text, StringComparison.Ordinal);
        var http2 = new FakeHttp().Add("GET", ".", FakeResponse.Text(401, "bad key"));
        var (c2, _, _) = Client(http2);
        Assert.False(c2.Check().Ok);
        Assert.Null(new SubSourceClient("").Check().Ok);
    }

    // ---------------------------------------------------------------- ket qua do API that (2026-11-09, SPEC 7.E)
    [Fact]
    public void Rate_text_real_headers()
    {
        var h = new Dictionary<string, string>
        {
            ["Content-Type"] = "application/json", ["X-RateLimit-Limit"] = "60", ["X-RateLimit-Remaining"] = "58",
            ["X-RateLimit-Reset"] = "2026-11-09T13:04:07.358Z", ["X-RateLimit-Window"] = "60", ["X-RateLimit-Limit-Hour"] = "1800",
            ["X-RateLimit-Remaining-Hour"] = "1794", ["X-RateLimit-Limit-Day"] = "7200", ["X-RateLimit-Remaining-Day"] = "7194",
        };
        Assert.Equal("còn 58/60 lượt/phút, 1794/1800 lượt/giờ, 7194/7200 lượt/ngày", SubSource.RateText(h));
        Assert.Equal("còn 4/5 lượt", SubSource.RateText(new Dictionary<string, string> { ["RateLimit-Remaining"] = "4", ["RateLimit-Limit"] = "5" }));
        Assert.Equal("", SubSource.RateText(new Dictionary<string, string>()));
    }

    [Fact]
    public void Real_language_names()
    {
        foreach (var v in new[] { "chinese_bg_code", "big_5_code", "chinese" })
            Assert.True(SubSource.LangMatches(D(("language", v)), "zh"), v);
        foreach (var v in new[] { "portuguese", "brazilian_portuguese" })
            Assert.True(SubSource.LangMatches(D(("language", v)), "pt"), v);
        Assert.False(SubSource.LangMatches(D(("language", "chinese_bg_code")), "ja"));
        Assert.False(SubSource.LangMatches(D(("language", "farsi_persian")), "pt"));
        foreach (var (code, v) in new[] { ("vi", "vietnamese"), ("cs", "czech"), ("en", "english"), ("ko", "korean"), ("th", "thai"),
                                          ("id", "indonesian"), ("ar", "arabic"), ("tr", "turkish"), ("nl", "dutch"), ("ru", "russian"),
                                          ("it", "italian"), ("pl", "polish"), ("fr", "french"), ("de", "german"), ("es", "spanish"),
                                          ("ja", "japanese") })
            Assert.True(SubSource.LangMatches(D(("language", v)), code), $"{code} {v}");
    }

    [Fact]
    public void Zip_names_without_utf8_flag_read_as_cp437_like_python()
    {
        // ZIP tu tao: ten 'é' (0x82 trong cp437) KHONG bat co UTF-8 -> zipfile doc la cp437
        using var tmp = new TempDir();
        TextEncoding.EnsureCodePages();
        var zpath = tmp["a.zip"];
        using (var fs = File.Create(zpath))
        using (var z = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create, false,
                                                            System.Text.Encoding.GetEncoding(437)))
        {
            using var s = z.CreateEntry("café.srt").Open();
            s.Write("x"u8);
        }
        SubSource.SafeExtract(zpath, tmp["out"]);
        Assert.Equal(["café.srt"], Directory.GetFiles(tmp["out"]).Select(Path.GetFileName));
    }
}
