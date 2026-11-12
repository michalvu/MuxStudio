using MuxStudio.Core.Tests.Support;

namespace MuxStudio.Core.Tests;

/// <summary>Tên phim, ngôn ngữ, tên file an toàn, tên chuẩn Jellyfin (§6.10 phần không cần mạng). Port <c>tests/test_naming.py</c>.</summary>
[Collection(GlobalStateCollection.Name)]
public class NamingTests
{
    // ---------------- tách tên + năm ----------------
    [Theory]
    [InlineData("Avengers.Endgame.2019.2160p.BluRay.x265-GRP", "Avengers Endgame", 2019)]
    [InlineData("28.Years.Later.2025.1080p.AMZN.WEB-DL.DDP5.1", "28 Years Later", 2025)]
    [InlineData("28 Years Later (2025) NF WEB", "28 Years Later", 2025)]
    [InlineData("[YTS.MX] Dune Part Two (2024) [2160p]", "Dune Part Two", 2024)]
    [InlineData("1917 - 2019", "1917", 2019)]
    [InlineData("1917.2019.1080p", "1917", 2019)]
    [InlineData("Blade Runner 2049", "Blade Runner 2049", null)] // TĐ-05 / Q4
    [InlineData("Blade.Runner.2049.2017.1080p.BluRay", "Blade Runner 2049", 2017)]
    [InlineData("2001 A Space Odyssey", "2001 A Space Odyssey", null)] // TĐ-05 / Q4
    [InlineData("2001.A.Space.Odyssey.1968.1080p", "2001 A Space Odyssey", 1968)]
    [InlineData("Uncut.Gems.2019.1080p", "Uncut Gems", 2019)]
    [InlineData("Charlotte's.Web.2006.720p", "Charlotte's Web", 2006)]
    [InlineData("Movie - 1080p", "Movie", null)]
    [InlineData("Mission Impossible.EXTENDED.1080p", "Mission Impossible", null)]
    [InlineData("Some.Movie.2019.EXTENDED.1080p.WEB-DL", "Some Movie", 2019)]
    public void Split_release_name(string name, string title, int? year)
    {
        var r = Naming.SplitReleaseName(name, currentYear: 2026);
        Assert.Equal((title, year), (r.Title, r.Year));
    }

    [Fact]
    public void Year_rule_uses_current_year()
    {
        Assert.Equal(2027, Naming.SplitReleaseName("Future.Film.2027.1080p", currentYear: 2026).Year); // năm nay + 1
        Assert.Null(Naming.SplitReleaseName("Future.Film.2028.1080p", currentYear: 2026).Year);
    }

    // ---------------- từ khoá ----------------
    [Fact]
    public void Title_tokens_subprep_noise_and_order()
    {
        Assert.Equal(["lord", "rings", "grp"], Naming.TitleTokens("The.Lord.of.the.Rings.2001.1080p.BluRay.x264-GRP[YTS] (Extended)"));
        Assert.Equal(["movie"], Naming.TitleTokens("Action.Movie.Drama.avi")); // thể loại + avi là từ nhiễu (bản SubPrep)
        Assert.Equal(["phim", "hay"], Naming.TitleTokens("Phim Hay 2"));
    }

    [Fact]
    public void Title_score()
    {
        var a = Naming.TitleTokens("Dune.Part.Two.2024.2160p");
        var b = Naming.TitleTokens("Dune Part Two (2024) [YTS]");
        Assert.Equal(1.0, Naming.TitleScore(a, b));
        Assert.Equal(0.0, Naming.TitleScore(a, []));
        Approx.Equal(1.0 / 3, Naming.TitleScore(["a1", "b1"], ["a1", "c1", "d1"]));
    }

    [Fact]
    public void Title_key_and_norm()
    {
        Assert.Equal("28yearslater", Naming.TitleKey("28.Years.Later.2025.1080p.AMZN.WEB-DL"));
        Assert.Equal("28yearslater", Naming.TitleKey("28 Years Later (2025) NF WEB"));
        Assert.Equal("spidermannowayhome", Naming.NormTitle("Spider-Man: No Way Home"));
    }

    // ---------------- ngôn ngữ ----------------
    [Theory]
    [InlineData("vi", "vi")]
    [InlineData("VIE", "vi")]
    [InlineData("vietnamese", "vi")]
    [InlineData("tiengviet", "vi")]
    [InlineData("vn", "vi")]
    [InlineData("cz", "cs")]
    [InlineData("ces", "cs")]
    [InlineData("sec", "cs")]
    [InlineData("anh", "en")]
    [InlineData("eng", "en")]
    [InlineData("ger", "de")]
    [InlineData("deu", "de")]
    [InlineData("jp", "ja")]
    [InlineData("chi", "zh")]
    [InlineData("ind", "id")]
    [InlineData("ara", "ar")]
    [InlineData("xx", "")]
    [InlineData("", "")]
    public void Lang_from_word(string w, string c) => Assert.Equal(c, Naming.LangFromWord(w));

    [Theory]
    [InlineData("cze", "cs", "cs")]
    [InlineData("ger", "de", "de")]
    [InlineData("eng", "en", "en")]
    [InlineData("und", "", "und")]
    [InlineData("", null, "und")]
    [InlineData("spa", "es-419", "es")]
    [InlineData("und", "pt-BR", "pt")]
    [InlineData("hun", "hu", "hun")]
    [InlineData("jpn", "", "ja")]
    [InlineData("chi", "zh-Hans", "zh")]
    public void Lang_from_track(string lang, string? ietf, string c) => Assert.Equal(c, Naming.LangFromTrack(lang, ietf));

    [Theory]
    [InlineData("cze cs", "cs", true)]
    [InlineData("cze ", "cs", true)]
    [InlineData("ger", "de", true)] // TĐ-04: .bat ra False
    [InlineData("jpn", "ja", true)]
    [InlineData("chi", "zh", true)]
    [InlineData("spa", "es", true)]
    [InlineData("eng en", "en", true)]
    [InlineData("vie", "vi", true)]
    [InlineData("eng", "cs", false)]
    [InlineData("und", "en", false)]
    [InlineData("", "en", false)]
    public void Lang_match_fixed(string tag, string want, bool ok) => Assert.Equal(ok, Naming.LangMatch(tag, want));

    [Fact]
    public void Lang_name_and_for_mux()
    {
        Assert.Equal("Vietnamese", Naming.LangName("vi"));
        Assert.Equal("", Naming.LangName("und"));
        Assert.Equal("HUN", Naming.LangName("hun"));
        Assert.Equal("cs", Naming.LangForMux("cs", 50));
        Assert.Equal("cze", Naming.LangForMux("cs", 49));
        Assert.Equal("ger", Naming.LangForMux("de", 40));
        Assert.Equal("hun", Naming.LangForMux("hun", 40));
    }

    [Theory]
    [InlineData("Phim.S01E01.vi", "Phim.S01E01", "vi")]
    [InlineData("Phim.S01E01.cz", "Phim.S01E01", "cs")] // TĐ-15
    [InlineData("Phim.S01E01.eng", "Phim.S01E01", "en")]
    [InlineData("Phim.S01E01", "Phim.S01E01", "")]
    [InlineData("Phim.Two", "Phim.Two", "")]
    public void Split_lang_tag(string b, string outBase, string outLang) =>
        Assert.Equal((outBase, outLang), Naming.SplitLangTag(b));

    [Fact]
    public void Lang_tag_of_file()
    {
        using var tmp = new TempDir();
        var root = tmp["srt_can_sync"];
        string P(params string[] parts) => Path.Combine([root, .. parts]);
        Assert.Equal("vi", Naming.LangTagOfFile(P("vi", "[Judas] Show - 01.srt"), root));
        Assert.Equal("cs", Naming.LangTagOfFile(P("Show.S01E01.cs.srt"), root));
        Assert.Equal("vi", Naming.LangTagOfFile(P("Show.S01E01_vietnamese.srt"), root));
        Assert.Equal("en", Naming.LangTagOfFile(P("Show.S01E01-en.srt"), root));
        Assert.Equal("", Naming.LangTagOfFile(P("Show.S01E01[en].srt"), root)); // như .bat: dấu ] ở cuối -> không nhận
        Assert.Equal("", Naming.LangTagOfFile(P("Show.S01E01.srt"), root));
        // chỉ xét thư mục con CẤP 1: thư mục phim tên 'en' sâu hơn không tính
        Assert.Equal("", Naming.LangTagOfFile(P("Phim", "en", "x.srt"), root));
        Assert.Equal("id", Naming.LangTagOfFile(P("id", "x.srt"), root)); // thêm ngôn ngữ ngoài 10 cũ
    }

    [Fact]
    public void Register_extra_language()
    {
        try
        {
            Naming.RegisterLanguage("hu");
            Assert.Equal("hu", Naming.LangFromWord("hu"));
            Assert.Equal(("Film", "hu"), Naming.SplitLangTag("Film.hu"));
        }
        finally
        {
            Naming.ResetLanguages();
        }
    }

    // ---------------- tên file + Jellyfin ----------------
    [Theory]
    [InlineData("Mission: Impossible", "Mission - Impossible")]
    [InlineData("What? <Why> \"No\" | a/b\\c *", "What Why No abc")]
    [InlineData("Movie.  ", "Movie")]
    [InlineData("  A   B ", "A B")]
    public void Safe_filename(string n, string o) => Assert.Equal(o, Naming.SafeFilename(n));

    [Fact]
    public void Jellyfin_names()
    {
        Assert.Equal("Spider-Man - Across the Spider-Verse Part One (2023) [tmdbid-569094]",
            Naming.MovieBaseName("Spider-Man: Across the Spider-Verse (Part One)", 2023, "tmdb", 569094));
        Assert.Equal("Dune (2021) [tvdbid-123]", Naming.MovieBaseName("Dune", 2021, "TVDB", 123));
        Assert.Equal("One Piece (1999) [tmdbid-37854]", Naming.ShowFolderName("One Piece", 1999, "tmdb", 37854));
        Assert.Equal("One Piece [tvdbid-81797]", Naming.ShowFolderName("One Piece", null, "tvdb", 81797));
        Assert.Equal("Season 01", Naming.SeasonFolderName(0)); // không bao giờ Season 00
        Assert.Equal("Season 16", Naming.SeasonFolderName(16));
        Assert.Equal("S01E643", Naming.EpisodeCode(0, 643));
        Assert.Equal("S01E05-E06", Naming.EpisodeCode(1, 5, 6));
        Assert.Equal("One Piece (1999) - S21E1085 - The Straw Hats' Promise",
            Naming.EpisodeBaseName("One Piece", 1999, "S21E1085", "The Straw Hats' [Promise]"));
        Assert.Equal("Show - S01E01", Naming.EpisodeBaseName("Show", null, "S01E01"));
    }

    [Fact]
    public void Jellyfin_full_paths_tv_and_movie()
    {
        var show = Naming.ShowFolderName("Show", 2019, "tmdb", 1);
        var p = Path.Combine(show, Naming.SeasonFolderName(1),
            Naming.EpisodeBaseName("Show", 2019, Naming.EpisodeCode(1, 5), "Pilot") + ".mkv");
        Assert.Equal("Show (2019) [tmdbid-1]/Season 01/Show (2019) - S01E05 - Pilot.mkv", p.Replace('\\', '/'));
    }

    // ---------------- thêm cho bản C#: hàm phụ không có test riêng ở Python ----------------
    [Theory]
    [InlineData(".vi", "vi")]
    [InlineData("_vietnamese", "vi")]
    [InlineData(".en.sdh", "en")]
    [InlineData("-ja", "ja")]
    [InlineData(".forced", "")]
    [InlineData(null, "")]
    public void Lang_of_suffix(string? suffix, string c) => Assert.Equal(c, Naming.LangOfSuffix(suffix));

    [Fact]
    public void Year_of_dates()
    {
        Assert.Equal(2019, Naming.YearOf("2019-05-01"));
        Assert.Null(Naming.YearOf(""));
        Assert.Null(Naming.YearOf(null));
        Assert.Null(Naming.YearOf("abc"));
        Assert.Equal(2019, Naming.YearOf(2019));
    }
}
