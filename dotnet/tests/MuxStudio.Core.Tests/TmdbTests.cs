using System.Text.RegularExpressions;
using MuxStudio.Core.Tests.Support;
using static MuxStudio.Core.Tests.Support.PyData;
using JDict = System.Collections.Generic.OrderedDictionary<string, object?>;

namespace MuxStudio.Core.Tests;

/// <summary>TMDB (giả lập): key 32 ký tự / token eyJ, 401 tắt cả lượt, 404 im lặng, One Piece trùng tên, tập 643.
/// Port <c>tests/test_tmdb.py</c>.</summary>
public class TmdbTests
{
    public const string Key = "0123456789abcdef0123456789abcdef";
    public const string Token = "eyJhbGciOiJIUzI1NiJ9.ZmFrZQ.ZmFrZQ";

    // One Piece: anime 1999 (nhieu season) va live-action 2023 (1 season, 8 tap)
    public static JDict Anime => D(("id", 37854L), ("name", "One Piece"), ("original_name", "ワンピース"), ("first_air_date", "1999-10-20"));
    public static JDict Live => D(("id", 111110L), ("name", "One Piece"), ("original_name", "One Piece"), ("first_air_date", "2023-08-31"));

    public static List<object?> AnimeSeasons()
    {
        var l = L(D(("season_number", 0L), ("episode_count", 30L)));
        long[] counts = [61, 16, 14, 39, 13, 52, 33, 35, 73, 45, 26, 14, 101, 56, 100, 121]; // tong 16 season dau = 799
        for (var i = 0; i < counts.Length; i++)
            l.Add(D(("season_number", (long)(i + 1)), ("episode_count", counts[i])));
        return l;
    }

    private static (TmdbClient C, Job J) Client(FakeHttp http, string key = Key)
    {
        var job = new Job();
        return (new TmdbClient(key, http: http.Client, sleep: new Sleeper().Sleep, job: job), job);
    }

    private static FakeResponse Ok(object data) => FakeResponse.Json(200, data);

    [Fact]
    public void Api_key_in_query_and_bearer_header()
    {
        var http = new FakeHttp().Add("GET", "/configuration$", Ok(D(("images", D()))));
        var (c, _) = Client(http);
        Assert.True(c.Check().Ok);
        Assert.Equal(Key, http.Calls[0].Params["api_key"]);
        Assert.False(http.Calls[0].Headers.ContainsKey("Authorization"));
        var http2 = new FakeHttp().Add("GET", "/configuration$", Ok(D(("images", D()))));
        var (c2, _) = Client(http2, Token);
        var st = c2.Check();
        Assert.True(st.Ok);
        Assert.Contains("Bearer", st.Text);
        Assert.Equal($"Bearer {Token}", http2.Calls[0].Headers["Authorization"]);
        Assert.False(http2.Calls[0].Params.ContainsKey("api_key"));
    }

    [Fact]
    public void Status_401_disables_tmdb_for_whole_run_and_reports_once()
    {
        var http = new FakeHttp().Add("GET", ".", FakeResponse.Json(401, D(("status_message", "Invalid API key"))));
        var (c, job) = Client(http);
        Assert.Null(c.Get("/search/movie", [new("query", "x")]));
        Assert.Null(c.Get("/search/movie", [new("query", "y")]));
        Assert.Single(http.Calls); // khong goi lai sau 401
        Assert.Equal(1, job.Lines.Count(l => l.Message.Contains("401", StringComparison.Ordinal)));
        Assert.False(c.Check().Ok);
    }

    [Fact]
    public void Status_404_quiet_when_probing_episode()
    {
        var http = new FakeHttp().Add("GET", "/tv/1/season/16/episode/643", FakeResponse.Json(404, D(("status_code", 34L))));
        var (c, job) = Client(http);
        Assert.Null(c.Get("/tv/1/season/16/episode/643", quiet404: true));
        Assert.Empty(job.Lines);
        c.Get("/tv/1/season/16/episode/643");
        Assert.Contains(job.Lines, l => l.Message.Contains("404", StringComparison.Ordinal));
    }

    [Fact]
    public void Network_error_retries_with_backoff()
    {
        var http = new FakeHttp().Add("GET", ".", (Func<FakeCall, FakeResponse>)(_ => throw new HttpRequestException("mat mang")));
        var sl = new Sleeper();
        var c = new TmdbClient(Key, retry: 3, http: http.Client, sleep: sl.Sleep);
        Assert.Null(c.Get("/configuration"));
        Assert.Equal([2.0, 4.0], sl.Calls);
        Assert.Equal(3, http.Calls.Count);
    }

    [Fact]
    public void Search_retries_without_year()
    {
        var http = new FakeHttp().Add("GET", "/search/movie", call =>
            call.Params.ContainsKey("year") ? Ok(D(("results", L()))) : Ok(D(("results", L(D(("id", 1L), ("title", "X")))))));
        var (c, _) = Client(http);
        Assert.True(Py.Equal(L(D(("id", 1L), ("title", "X"))), c.Search("movie", "X", 2001).Cast<object?>().ToList()));
        Assert.Equal([true, false], http.Calls.Select(x => x.Params.ContainsKey("year")));
    }

    public static FakeHttp OnePieceHttp()
    {
        var http = new FakeHttp();
        http.Add("GET", "/search/tv", Ok(D(("results", L(Live, Anime)))));
        http.Add("GET", "/tv/111110$", Ok(D(("id", 111110L), ("seasons", L(D(("season_number", 1L), ("episode_count", 8L)))))));
        http.Add("GET", "/tv/37854$", Ok(D(("id", 37854L), ("seasons", AnimeSeasons()))));
        http.Add("GET", @"/tv/(\d+)/external_ids", call =>
        {
            var id = Regex.Match(call.Url, @"/tv/(\d+)/").Groups[1].Value;
            return Ok(D(("imdb_id", id == "37854" ? "tt0388629" : "tt11737520")));
        });
        return http;
    }

    [Fact]
    public void One_piece_duplicate_names_pick_series_that_has_episode()
    {
        var http = OnePieceHttp();
        var (c, job) = Client(http);
        Assert.Equal(37854L, c.FindShow("One Piece", null, 16, 643)!["id"]);
        Assert.Equal("tt0388629", c.ImdbId("One Piece", null, true, 16, 643));
        // live-action co S01E05 -> ca hai co season 1; ca hai deu >= 5 tap -> diem bang -> giu cai dau
        Assert.Equal(111110L, c.FindShow("One Piece", null, 1, 5)!["id"]);
        Assert.Contains(job.Lines, l => l.Message.Contains("cùng tên", StringComparison.Ordinal));
    }

    [Fact]
    public void Absolute_episode_643_to_season()
    {
        var (c, _) = Client(OnePieceHttp());
        // cong don 14 season dau = 578 -> tap 643 = S15E65
        Assert.Equal((15, 65), c.AbsoluteToSeason(37854, 643));
        Assert.Equal((1, 1), c.AbsoluteToSeason(37854, 1));
        Assert.Null(c.AbsoluteToSeason(37854, 99999));
    }

    [Fact]
    public void Imdb_movie_exact_title_and_year()
    {
        var http = new FakeHttp();
        http.Add("GET", "/search/movie", Ok(D(("results", L(
            D(("id", 1L), ("title", "Dune"), ("original_title", "Dune"), ("release_date", "1984-12-14")),
            D(("id", 2L), ("title", "Dune"), ("original_title", "Dune"), ("release_date", "2021-09-15")))))));
        http.Add("GET", "/movie/2/external_ids", Ok(D(("imdb_id", "tt1160419"))));
        var (c, _) = Client(http);
        Assert.Equal("tt1160419", c.ImdbId("Dune", 2021, false));
        var n = http.Calls.Count;
        Assert.Equal("tt1160419", c.ImdbId("Dune", 2021, false));
        Assert.Equal(n, http.Calls.Count); // bo nho dem trong luot
    }

    [Fact]
    public void Official_title_keeps_punctuation_and_reuses_cached_search()
    {
        var http = new FakeHttp();
        http.Add("GET", "/search/movie", Ok(D(("results", L(
            D(("id", 122L), ("title", "The Lord of the Rings: The Return of the King"),
              ("original_title", "The Lord of the Rings: The Return of the King"), ("release_date", "2003-12-17")),
            D(("id", 123L), ("title", "The Lord of the Rings"), ("original_title", "The Lord of the Rings"), ("release_date", "1978-11-15")))))));
        http.Add("GET", "/movie/122/external_ids", Ok(D(("imdb_id", "tt0167260"))));
        http.Add("GET", "/search/tv", Ok(D(("results", L(
            D(("id", 7L), ("name", "Marvel's Agents of S.H.I.E.L.D."), ("original_name", "Marvel's Agents of S.H.I.E.L.D."),
              ("first_air_date", "2013-09-24")))))));
        http.Add("GET", "/tv/7$", Ok(D(("seasons", L(D(("season_number", 1L), ("episode_count", 22L)))))));
        var (c, _) = Client(http);
        Assert.Equal("tt0167260", c.ImdbId("The Lord of the Rings The Return of the King", 2003, false));
        var n = http.Calls.Count;
        Assert.Equal("The Lord of the Rings: The Return of the King", c.OfficialTitle("The Lord of the Rings The Return of the King", 2003, false));
        Assert.Equal(n, http.Calls.Count); // dung lai ket qua tim da nho dem
        Assert.Equal("Marvel's Agents of S.H.I.E.L.D.", c.OfficialTitle("Marvels Agents of S H I E L D", 2013, true, 1, 5));
        Assert.Null(c.OfficialTitle("", 2003, false));
        var empty = new FakeHttp().Add("GET", "/search/movie", Ok(D(("results", L()))));
        var (c2, _) = Client(empty);
        Assert.Null(c2.OfficialTitle("Phim Khong Co", null, false));
    }

    [Fact]
    public void No_key()
    {
        var c = new TmdbClient("", http: new FakeHttp().Client);
        Assert.Null(c.Check().Ok);
        Assert.Null(c.ImdbId("x", null, false));
        Assert.Null(c.OfficialTitle("x", null, false));
    }

    [Fact]
    public void Query_encoding_like_requests()
    {
        // requests: urlencode (quote_plus) — khoang trang -> '+', dau ':' -> %3A, chu co dau -> UTF-8
        var http = new FakeHttp().Add("GET", "/search/movie", Ok(D(("results", L()))));
        var (c, _) = Client(http);
        c.Search("movie", "Lord: Kỳ ~x", null);
        var call = http.Calls[0];
        Assert.Equal("Lord: Kỳ ~x", call.Params["query"]);
        Assert.Contains("query=Lord%3A+K%E1%BB%B3+~x", call.FullUrl, StringComparison.Ordinal);
        Assert.StartsWith("https://api.themoviedb.org/3/search/movie?language=en-US&include_adult=false&query=", call.FullUrl, StringComparison.Ordinal);
    }
}
