using MuxStudio.Core.Tests.Support;
using static MuxStudio.Core.Tests.Support.PyData;
using JDict = System.Collections.Generic.OrderedDictionary<string, object?>;

namespace MuxStudio.Core.Tests;

/// <summary>Đặt tên chuẩn Jellyfin theo TMDB / TVDB (§6.10) với API giả: phim lẻ, phim bộ, "Cần chọn", One Piece trùng tên,
/// số tập tuyệt đối 643, TMDB 401 tắt cả lượt, TVDB đăng nhập lại khi 401, phân trang, tên tập theo ngôn ngữ.
/// Port <c>tests/test_naming_db.py</c> (trừ bài cuối cần <c>mux</c> — xem <c>MuxTests</c>).</summary>
public class NamingDbTests
{
    public static readonly Config Cfg0 = new();
    public static JDict Dune => D(("id", 693134L), ("title", "Dune: Part Two"), ("original_title", "Dune: Part Two"), ("release_date", "2024-02-27"));
    public static JDict Dune21 => D(("id", 438631L), ("title", "Dune"), ("original_title", "Dune"), ("release_date", "2021-09-15"));
    public static JDict Show => D(("id", 1399L), ("name", "Game of Thrones"), ("original_name", "Game of Thrones"), ("first_air_date", "2011-04-17"));

    public static Config Cfg(params (string K, object? V)[] kv) =>
        new(kv.Select(x => new KeyValuePair<string, object?>(x.K, x.V)));

    private static FakeResponse Ok(object data) => FakeResponse.Json(200, data);

    private static JDict With(JDict d, params (string K, object? V)[] kv)
    {
        var o = new JDict(d, StringComparer.Ordinal);
        foreach (var (k, v) in kv)
            o[k] = v;
        return o;
    }

    public static (TmdbClient C, Job J) Tm(FakeHttp http, string lang = "en-US")
    {
        var job = new Job();
        return (new TmdbClient(TmdbTests.Key, lang, 3, http.Client, new Sleeper().Sleep, job), job);
    }

    public static FakeHttp TmdbHttp()
    {
        var h = new FakeHttp();
        h.Add("GET", "/search/movie$", c => Ok(D(("results", c.Params["query"].Contains("Dune", StringComparison.Ordinal) ? L(Dune, Dune21) : L()))));
        h.Add("GET", "/search/tv$", c => Ok(D(("results", c.Params["query"] == "One Piece" ? L(TmdbTests.Anime, TmdbTests.Live)
            : c.Params["query"].Contains("Thrones", StringComparison.Ordinal) ? L(Show) : L()))));
        h.Add("GET", "/tv/37854$", Ok(With(TmdbTests.Anime, ("seasons", TmdbTests.AnimeSeasons()))));
        h.Add("GET", "/tv/111110$", Ok(With(TmdbTests.Live, ("seasons", L(D(("season_number", 1L), ("episode_count", 8L)))))));
        h.Add("GET", "/tv/1399$", Ok(With(Show, ("seasons", L(D(("season_number", 1L), ("episode_count", 10L)))))));
        h.Add("GET", "/tv/1399/season/1/episode/5$", Ok(D(("name", "The Wolf and the Lion"))));
        // One Piece tap 643 = season 15 tap 65 (tong 14 season dau = 578)
        h.Add("GET", "/tv/37854/season/15/episode/65$", Ok(D(("name", "Một tập: Hody/Jones?"))));
        return h;
    }

    private static Dictionary<string, INameDb?> Dbs(params (string K, INameDb V)[] kv) =>
        kv.ToDictionary(x => x.K, x => (INameDb?)x.V);

    private static Dictionary<string, object?> NewCache() => new(StringComparer.Ordinal);

    [Fact]
    public void Movie_exact_tmdb_name_and_folder()
    {
        var (c, _) = Tm(TmdbHttp());
        var o = Naming.ResolveOutputName("Dune.Part.Two.2024.2160p.WEB-DL", "tmdb", Dbs(("tmdb", c)), Cfg0, NewCache());
        Assert.Equal(("ok", "Dune - Part Two (2024) [tmdbid-693134]", "Dune - Part Two (2024) [tmdbid-693134]", "tmdb"), (o.Status, o.Base, o.RelDir, o.Db));
        var cfg = Cfg(("TmdbCreateFolder", false));
        Assert.Equal("", Naming.ResolveOutputName("Dune.Part.Two.2024.2160p", "tmdb", Dbs(("tmdb", c)), cfg, NewCache()).RelDir);
    }

    [Fact]
    public void Movie_not_exact_needs_choice_then_choose()
    {
        var (c, _) = Tm(TmdbHttp());
        var cache = NewCache();
        var o = Naming.ResolveOutputName("Dune.Part.2.2024.1080p", "tmdb", Dbs(("tmdb", c)), Cfg0, cache);
        Assert.Equal("need_choice", o.Status);
        Assert.Equal([693134L, 438631L], o.Candidates.Select(x => x.Id));
        Assert.Equal("Dune: Part Two (2024)  tmdbid-693134", o.Candidates[0].Describe("tmdb"));
        var o2 = Naming.ChooseCandidate(c, "Dune.Part.2.2024.1080p", 693134, o, Cfg0, cache);
        Assert.Equal("ok", o2.Status);
        Assert.Equal("Dune - Part Two (2024) [tmdbid-693134]", o2.Base);
        // TmdbConfirm tat -> lay ket qua dau
        var o3 = Naming.ResolveOutputName("Dune.Part.2.2024.1080p", "tmdb", Dbs(("tmdb", c)), Cfg(("TmdbConfirm", false)), NewCache());
        Assert.Equal("ok", o3.Status);
        Assert.Contains("693134", o3.Base);
    }

    [Fact]
    public void Movie_not_found_keeps_name()
    {
        var (c, _) = Tm(TmdbHttp());
        var o = Naming.ResolveOutputName("Khong.Co.Phim.Nay.2020", "tmdb", Dbs(("tmdb", c)), Cfg0, NewCache());
        Assert.Equal(("keep", "Khong.Co.Phim.Nay.2020"), (o.Status, o.Base));
        Assert.Contains("không tìm thấy", o.Note);
    }

    [Fact]
    public void Tv_episode_name_and_season_folder()
    {
        var (c, _) = Tm(TmdbHttp());
        var o = Naming.ResolveOutputName("Game.of.Thrones.S01E05.1080p", "tmdb", Dbs(("tmdb", c)), Cfg0, NewCache());
        Assert.Equal(("ok", "tv"), (o.Status, o.Kind));
        Assert.Equal("Game of Thrones (2011) - S01E05 - The Wolf and the Lion", o.Base);
        Assert.Equal(Path.Combine("Game of Thrones (2011) [tmdbid-1399]", "Season 01"), o.RelDir);
    }

    [Fact]
    public void One_piece_duplicate_names_and_absolute_643()
    {
        var (c, _) = Tm(TmdbHttp());
        var o = Naming.ResolveOutputName("[Judas] One Piece - 0643 [1080p]", "tmdb", Dbs(("tmdb", c)), Cfg0, NewCache());
        Assert.Equal("ok", o.Status);
        Assert.Equal("One Piece (1999) - S15E65 - Một tập - HodyJones", o.Base);
        Assert.EndsWith(Path.Combine("One Piece (1999) [tmdbid-37854]", "Season 15"), o.RelDir, StringComparison.Ordinal);
        Assert.StartsWith("Số tập tuyệt đối: tap #643 → TMDB S15E65", o.EpisodeNote, StringComparison.Ordinal);
        var o2 = Naming.ResolveOutputName("One.Piece.S16E643.1080p", "tmdb", Dbs(("tmdb", c)), Cfg0, NewCache());
        Assert.StartsWith("One Piece (1999) - S15E65", o2.Base, StringComparison.Ordinal);
        var o3 = Naming.ResolveOutputName("One Piece - 0643", "tmdb", Dbs(("tmdb", c)), Cfg(("TvAbsoluteToSeason", false)), NewCache());
        Assert.Contains(" - S01E643 - ", o3.Base, StringComparison.Ordinal);
        Assert.Contains("Season 01", o3.RelDir, StringComparison.Ordinal); // khong bao gio Season 00
    }

    [Fact]
    public void Episode_404_is_quiet_and_keeps_code()
    {
        var (c, job) = Tm(TmdbHttp());
        var o = Naming.ResolveOutputName("Game.of.Thrones.S01E07", "tmdb", Dbs(("tmdb", c)), Cfg0, NewCache());
        Assert.Equal("Game of Thrones (2011) - S01E07", o.Base);
        Assert.Contains("không có S01E07", o.EpisodeNote, StringComparison.Ordinal);
        Assert.DoesNotContain(job.Lines, l => l.Message.Contains("404", StringComparison.Ordinal));
    }

    [Fact]
    public void Tmdb_401_disables_for_run_and_keeps_names()
    {
        var h = new FakeHttp().Add("GET", ".", FakeResponse.Json(401, D(("status_message", "bad"))));
        var (c, job) = Tm(h);
        var cache = NewCache();
        foreach (var n in new[] { "Dune.2021", "Avatar.2009", "Show.S01E01" })
        {
            var o = Naming.ResolveOutputName(n, "tmdb", Dbs(("tmdb", c)), Cfg0, cache);
            Assert.Equal(("keep", n), (o.Status, o.Base));
        }
        Assert.Single(h.Calls);
        Assert.Equal(1, job.Lines.Count(l => l.Message.Contains("401", StringComparison.Ordinal)));
    }

    [Fact]
    public void Series_choice_applies_to_all_episodes()
    {
        var h = TmdbHttp();
        h.Add("GET", "/search/tv$", Ok(D(("results", L(Show, With(Show, ("id", 9L), ("name", "Thrones")))))));
        var (c, _) = Tm(h);
        var cache = NewCache();
        var o1 = Naming.ResolveOutputName("GoT.S01E05", "tmdb", Dbs(("tmdb", c)), Cfg0, cache);
        Assert.Equal("need_choice", o1.Status);
        Naming.ChooseCandidate(c, "GoT.S01E05", 1399, o1, Cfg0, cache);
        var o2 = Naming.ResolveOutputName("GoT.S01E06", "tmdb", Dbs(("tmdb", c)), Cfg0, cache);
        Assert.Equal("ok", o2.Status);
        Assert.StartsWith("Game of Thrones (2011) - S01E06", o2.Base, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- TVDB
    public sealed class TvdbState
    {
        public int Logins { get; set; }
        public bool Expired { get; set; }
    }

    public static (FakeHttp H, TvdbState S) TvdbHttp(bool tokenExpireOnce = false)
    {
        var h = new FakeHttp();
        var state = new TvdbState { Expired = tokenExpireOnce };
        FakeResponse Login(FakeCall call)
        {
            state.Logins++;
            Assert.Equal("tvdb-demo-key", call.JsonDict!.Get("apikey"));
            return Ok(D(("data", D(("token", $"tok{state.Logins}")))));
        }
        FakeResponse Search(FakeCall call)
        {
            if (state.Expired)
            {
                state.Expired = false;
                return FakeResponse.Json(401, D(("status", "failure")));
            }
            if (call.Url.Contains("type=series", StringComparison.Ordinal))
            {
                return Ok(D(("data", L(
                    D(("tvdb_id", "81797"), ("name", "One Piece"), ("year", "1999"), ("translations", D(("eng", "One Piece"), ("vie", "Đảo Hải Tặc")))),
                    D(("tvdb_id", "392276"), ("name", "One Piece"), ("year", "2023"))))));
            }
            return Ok(D(("data", L(D(("tvdb_id", "1234"), ("name", "Dune: Part Two"), ("year", "2024"))))));
        }
        FakeResponse Eps(FakeCall call)
        {
            var sid = call.Url.Split("/series/")[1].Split('/')[0];
            var page = int.Parse(call.Url.Split("page=")[1], System.Globalization.CultureInfo.InvariantCulture);
            if (sid == "392276")
            {
                return Ok(D(("data", D(("episodes", Enumerable.Range(1, 8).Select(i => (object?)D(("id", 1L), ("seasonNumber", 1L), ("number", (long)i),
                    ("absoluteNumber", (long)i))).ToList()))), ("links", D(("next", null)))));
            }
            // anime: 2 trang, tap 643 o trang 2
            var lst = Enumerable.Range(1 + 500 * page, 500).Select(n => (object?)D(("id", 10000L + n), ("seasonNumber", n > 578 ? 15L : 1L),
                ("number", (long)(n > 578 ? n - 578 : n)), ("absoluteNumber", (long)n), ("nameTranslations", L("eng", "jpn")))).ToList();
            return Ok(D(("data", D(("episodes", lst))), ("links", D(("next", page == 0 ? "x" : null)))));
        }
        h.Add("POST", "/login$", Login);
        h.Add("GET", @"/search\?", Search);
        h.Add("GET", @"/series/\d+/episodes/default\?page=\d+$", Eps);
        h.Add("GET", "/episodes/10643/translations/eng$", Ok(D(("data", D(("name", "Tên tập 643"))))));
        return (h, state);
    }

    public static (TvdbClient C, Job J) Tv(FakeHttp h, string lang = "eng")
    {
        var job = new Job();
        return (new TvdbClient("tvdb-demo-key", "", lang, "default", 3, h.Client, new Sleeper().Sleep, job), job);
    }

    [Fact]
    public void Tvdb_absolute_number_pagination_and_episode_name()
    {
        var (h, state) = TvdbHttp();
        var (c, _) = Tv(h);
        var o = Naming.ResolveOutputName("[Judas] One Piece - 0643 [1080p]", "tvdb", Dbs(("tvdb", c)), Cfg0, NewCache());
        Assert.Equal("ok", o.Status);
        Assert.Equal("One Piece (1999) - S15E65 - Tên tập 643", o.Base);
        Assert.Equal(Path.Combine("One Piece (1999) [tvdbid-81797]", "Season 15"), o.RelDir);
        Assert.Equal(1, state.Logins);
        Assert.Equal(2, h.CallsTo("/series/81797/episodes").Count); // theo links.next
        Assert.Empty(h.CallsTo("/episodes/10643/translations/vie")); // 'vie' khong co trong nameTranslations -> bo qua
    }

    [Fact]
    public void Tvdb_local_name_and_relogin_once_on_401()
    {
        var (h, state) = TvdbHttp(tokenExpireOnce: true);
        var (c, _) = Tv(h, "vie");
        var res = c.SearchSeries("One Piece", null);
        Assert.Equal("Đảo Hải Tặc", res[0]["name"]);
        Assert.Equal("One Piece", res[0]["original_name"]);
        Assert.Equal(2, state.Logins); // dang nhap lai MOT lan sau 401
        Assert.True(c.Check().Ok);
        Assert.Contains("query=One%20Piece&type=series", h.CallsTo(@"/search\?")[0].Url, StringComparison.Ordinal);
    }

    [Fact]
    public void Tvdb_login_failure_status()
    {
        var h = new FakeHttp().Add("POST", "/login$", FakeResponse.Json(401, D(("status", "failure"))));
        var (c, _) = Tv(h);
        var st = c.Check();
        Assert.False(st.Ok);
        Assert.Equal("TVDB: đăng nhập lỗi", st.Text);
        Assert.False(c.Usable);
    }

    [Fact]
    public void Name_fallback_other_only_when_enabled()
    {
        var h401 = new FakeHttp().Add("GET", ".", FakeResponse.Json(401, D()));
        var (tmdbC, _) = Tm(h401);
        var (h, _) = TvdbHttp();
        var (tvdbC, _) = Tv(h);
        var dbs = Dbs(("tmdb", tmdbC), ("tvdb", tvdbC));
        var o = Naming.ResolveOutputName("Dune.Part.Two.2024", "tmdb", dbs, Cfg0, NewCache());
        Assert.Equal("keep", o.Status);
        var o2 = Naming.ResolveOutputName("Dune.Part.Two.2024", "tmdb", dbs, Cfg(("NameFallbackOther", true)), NewCache());
        Assert.Equal("ok", o2.Status);
        Assert.Equal("Dune - Part Two (2024) [tvdbid-1234]", o2.Base);
        Assert.Contains("thử TVDB", o2.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void None_keeps_original_name()
    {
        var o = Naming.ResolveOutputName("Bat.Ky.2020", "none", new Dictionary<string, INameDb?>(), Cfg0);
        Assert.Equal(("keep", "Bat.Ky.2020", ""), (o.Status, o.Base, o.RelDir));
    }

    // ---------------------------------------------------------------- "Tim lai voi ten..." (SPEC muc 6 #20, GD5)
    [Fact]
    public void Search_candidates_movie_then_choose()
    {
        var (c, _) = Tm(TmdbHttp());
        var cache = NewCache();
        var o = Naming.ResolveOutputName("Kho.Doan.2021.1080p", "tmdb", Dbs(("tmdb", c)), Cfg0, cache);
        Assert.Equal("keep", o.Status);
        Assert.Empty(o.Candidates);
        var s = Naming.SearchCandidates(c, "Kho.Doan.2021.1080p", o, "Dune", 2021);
        Assert.Equal(("need_choice", "Kho.Doan.2021.1080p", o.QueryKey), (s.Status, s.Base, s.QueryKey));
        Assert.Equal([693134L, 438631L], s.Candidates.Select(x => x.Id));
        Assert.Equal("TMDB: kết quả tìm 'Dune' (2021) — chọn một kết quả.", s.Note);
        var o2 = Naming.ChooseCandidate(c, "Kho.Doan.2021.1080p", 438631, s, Cfg0, cache);
        Assert.Equal(("ok", "Dune (2021) [tmdbid-438631]"), (o2.Status, o2.Base));
        // file khac cung khoa dung luon lua chon
        Assert.Equal("Dune (2021) [tmdbid-438631]", Naming.ResolveOutputName("Kho.Doan.2021.720p", "tmdb", Dbs(("tmdb", c)), Cfg0, cache).Base);
        var none = Naming.SearchCandidates(c, "x", o, "Không Có Gì", null);
        Assert.Equal("need_choice", none.Status);
        Assert.Empty(none.Candidates);
        Assert.Contains("không tìm thấy 'Không Có Gì'", none.Note, StringComparison.Ordinal);
    }
}
