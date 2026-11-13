using MuxStudio.Core.Tests.Support;
using static MuxStudio.Core.Tests.Support.PyData;
using JDict = System.Collections.Generic.OrderedDictionary<string, object?>;

namespace MuxStudio.Core.Tests;

/// <summary>OpenSubtitles (giả lập) — User-Agent, VIP base_url + token, 401 đăng nhập lại, quota. Port <c>tests/test_opensubs.py</c>.</summary>
public class OpenSubsTests
{
    private static JDict LoginVip => D(("token", "tok-1"), ("base_url", "vip-api.opensubtitles.com"),
                                       ("user", D(("allowed_downloads", 1000L), ("vip", true), ("level", "VIP Member"))));

    private static (OpenSubtitlesClient C, Sleeper S, Job J) Client(FakeHttp http, string user = "nguoidung", string pw = "matkhau", string? tmp = null)
    {
        var sl = new Sleeper();
        var job = new Job();
        return (new OpenSubtitlesClient("demo-os-key", user, pw, "MyApp v1.0.0", 1100, 50, 5, http.Client, sl.Sleep, job, tmp), sl, job);
    }

    private static FakeResponse Ok(object data) => FakeResponse.Json(200, data);

    private static FakeHttp BaseHttp()
    {
        var http = new FakeHttp();
        http.Add("POST", @"api\.opensubtitles\.com/api/v1/login$", Ok(LoginVip));
        http.Add("GET", @"vip-api\.opensubtitles\.com/api/v1/infos/user$", Ok(D(("data", D(("remaining_downloads", 987L), ("allowed_downloads", 1000L))))));
        return http;
    }

    private static JDict Item(long fileId) => D(("attributes", D(("files", L(D(("file_id", fileId)))))));

    [Fact]
    public void Login_vip_switches_host_and_sends_token_everywhere()
    {
        var http = BaseHttp();
        http.Add("GET", @"vip-api\.opensubtitles\.com/api/v1/subtitles", Ok(D(("data", L(D(("id", "1")))))));
        var (c, _, job) = Client(http);
        Assert.True(c.Login());
        Assert.Equal("vip-api.opensubtitles.com", c.Host);
        Assert.True(c.Vip);
        Assert.Equal(987, c.Remaining);
        c.Search("tt0133093", null, null, "vi");
        foreach (var call in http.Calls.Skip(1))
        {
            Assert.StartsWith("https://vip-api.opensubtitles.com/", call.Url, StringComparison.Ordinal);
            Assert.Equal("Bearer tok-1", call.Headers["Authorization"]);
        }
        foreach (var call in http.Calls)
        {
            Assert.Equal("demo-os-key", call.Headers["Api-Key"]);
            Assert.Equal("MyApp v1.0.0", call.Headers["User-Agent"]);
        }
        Assert.Equal("0133093", http.CallsTo("/subtitles")[0].Params["imdb_id"]); // bo 'tt'
        Assert.Contains(job.Lines, l => l.Message.Contains("VIP", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_text()
    {
        var (c, _, _) = Client(BaseHttp());
        var st = c.Check();
        Assert.True(st.Ok);
        Assert.Equal("OpenSubtitles: đăng nhập VIP, máy chủ vip-api, còn 987/1000 lượt tải", st.Text);
    }

    [Fact]
    public void Status_401_relogin_at_most_twice_then_disable()
    {
        var http = BaseHttp();
        http.Add("GET", "/subtitles", FakeResponse.Text(401, "invalid token"));
        var (c, _, _) = Client(http);
        c.Login(quiet: true);
        Assert.Empty(c.Search("tt1", null, null, "en"));
        Assert.Equal(3, http.CallsTo("/login", "POST").Count); // 1 + 2 lan dang nhap lai
        Assert.True(c.Disabled);
        Assert.Equal(2, c.Relogins);
    }

    [Fact]
    public void Status_401_relogin_succeeds_and_retries()
    {
        var http = BaseHttp();
        http.Add("GET", "/subtitles", FakeResponse.Status_(401), Ok(D(("data", L(D(("id", "x")))))));
        var (c, _, _) = Client(http);
        c.Login(quiet: true);
        Assert.True(Py.Equal(L(D(("id", "x"))), c.Search("tt1", null, null, "en").Cast<object?>().ToList()));
        Assert.False(c.Disabled);
        Assert.Equal(1, c.Relogins);
    }

    [Fact]
    public void Status_403_user_agent_block_disables_source()
    {
        var http = new FakeHttp().Add("POST", "/login", FakeResponse.Text(403, """{"message":"kong-user-agent-block: User-Agent not allowed"}"""));
        var (c, _, job) = Client(http);
        Assert.False(c.Login());
        Assert.True(c.Disabled);
        Assert.Contains(job.Lines, l => l.Message.Contains("User-Agent", StringComparison.Ordinal));
    }

    [Fact]
    public void Status_429_waits_5s()
    {
        var http = BaseHttp();
        http.Add("GET", "/subtitles", FakeResponse.Status_(429), Ok(D(("data", L(D(("id", "y")))))));
        var (c, sl, _) = Client(http);
        c.Login(quiet: true);
        Assert.True(Py.Equal(L(D(("id", "y"))), c.Search("tt1", null, null, "en").Cast<object?>().ToList()));
        Assert.Contains(5.0, sl.Calls);
    }

    [Fact]
    public void No_account_can_search_not_download()
    {
        var http = new FakeHttp().Add("GET", "/subtitles", Ok(D(("data", L(D(("id", "1")))))));
        var (c, _, job) = Client(http, user: "", pw: "");
        Assert.False(c.Login());
        Assert.False(c.Disabled);
        Assert.NotEmpty(c.Search("tt1", null, null, "en"));
        Assert.Null(c.Download(Item(1)));
        Assert.Contains(job.Lines, l => l.Message.Contains("không tải được", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Search_three_attempts_and_params()
    {
        var http = BaseHttp();
        var seen = new List<OrderedDictionary<string, string>>();
        http.Add("GET", "/subtitles", call =>
        {
            seen.Add(call.Params);
            return call.Params.ContainsKey("query") ? Ok(D(("data", L(D(("id", "theo-ten")))))) : Ok(D(("data", L())));
        });
        var (c, _, _) = Client(http);
        c.Login(quiet: true);
        Assert.True(Py.Equal(L(D(("id", "theo-ten"))), c.Search("tt1234", 2, 5, "vi", "Show", 2019).Cast<object?>().ToList()));
        Assert.Equal("download_count", seen[0]["order_by"]);
        Assert.Equal("2", seen[0]["season_number"]);
        Assert.Equal("5", seen[0]["episode_number"]);
        Assert.False(seen[1].ContainsKey("order_by"));
        Assert.Equal("1234", seen[1]["imdb_id"]);
        Assert.False(seen[2].ContainsKey("imdb_id"));
        Assert.Equal("Show", seen[2]["query"]);
        Assert.Equal("2019", seen[2]["year"]);
    }

    [Theory]
    [InlineData(100, true, "ok")]
    [InlineData(50, true, "warn")]
    [InlineData(6, true, "warn")]
    [InlineData(5, false, "stop")]
    [InlineData(0, false, "stop")]
    [InlineData(-1, true, "unknown")]
    public void Quota_thresholds(int remaining, bool ok, string level)
    {
        var (c, _, job) = Client(new FakeHttp());
        c.Remaining = remaining;
        c.Allowed = 1000;
        Assert.Equal(level, c.QuotaLevel);
        Assert.Equal(ok, c.QuotaOk());
        if (level == "warn")
            Assert.Contains(job.Lines, l => l.Message.Contains("Chú ý", StringComparison.Ordinal));
        if (level == "stop")
            Assert.True(c.Disabled);
    }

    [Fact]
    public void Download_flow_updates_quota()
    {
        using var tmp = new TempDir();
        var http = BaseHttp();
        http.Add("POST", "vip-api.*/download$", Ok(D(("link", "https://dl.opensubtitles.com/f/abc"), ("remaining", 49L), ("reset_time", "23 hours"))));
        var body = string.Concat(Enumerable.Repeat("1\r\n00:00:01,000 --> 00:00:02,000\r\nHi\r\n", 5));
        http.Add("GET", @"dl\.opensubtitles\.com/f/abc", FakeResponse.Text(200, body));
        var (c, _, job) = Client(http, tmp: tmp.Path);
        c.Login();
        var p = c.Download(Item(777));
        Assert.NotNull(p);
        Assert.StartsWith("1\r\n", File.ReadAllText(p), StringComparison.Ordinal);
        Assert.True(Py.Equal(D(("file_id", 777L)), http.CallsTo("/download", "POST")[0].Json));
        Assert.Equal(49, c.Remaining);
        Assert.Contains("23 hours", c.ResetMsg, StringComparison.Ordinal);
        Assert.Contains(job.Lines, l => l.Message.Contains("Chú ý", StringComparison.Ordinal)); // <= 50 -> canh bao
    }

    [Fact]
    public void Download_rejects_empty_file()
    {
        using var tmp = new TempDir();
        var http = BaseHttp();
        http.Add("POST", "/download$", Ok(D(("link", "https://dl.x/f"), ("remaining", 900L))));
        http.Add("GET", @"dl\.x/f", FakeResponse.Text(200, "tiny"));
        var (c, _, _) = Client(http, tmp: tmp.Path);
        c.Login(quiet: true);
        Assert.Null(c.Download(Item(1)));
    }

    [Fact]
    public void Download_stops_when_quota_low()
    {
        using var tmp = new TempDir();
        var (c, _, _) = Client(BaseHttp(), tmp: tmp.Path);
        c.Login(quiet: true);
        c.Remaining = 4;
        Assert.Null(c.Download(Item(1)));
        Assert.True(c.Disabled);
    }

    public static TheoryData<string, string, bool> UsableCases => new()
    {
        { """{"files": [1]}""", "", true },
        { """{"files": []}""", "", false },
        { """{"files": [1], "hearing_impaired": true}""", "", false },
        { """{"files": [1], "hearing_impaired": true}""", "exclude_sdh", true },
        { """{"files": [1], "foreign_parts_only": true}""", "", false },
        { """{"files": [1], "machine_translated": true}""", "", false },
        { """{"files": [1], "ai_translated": true}""", "exclude_machine", true },
    };

    [Theory]
    [MemberData(nameof(UsableCases))]
    public void Usable(string attrs, string off, bool ok) =>
        Assert.Equal(ok, OpenSubs.Usable(D(("attributes", J(attrs))), excludeSdh: off != "exclude_sdh", excludeMachine: off != "exclude_machine"));

    // ---------------------------------------------------------------- ket qua do API that (2026-11-09, SPEC 7.E)
    [Fact]
    public void Language_codes_match_real_server()
    {
        Assert.Equal("zh-cn,zh-tw", OpenSubs.OsLanguages("zh"));
        Assert.Equal("pt-br,pt-pt", OpenSubs.OsLanguages("pt"));
        Assert.Equal("vi", OpenSubs.OsLanguages("vi"));
        Assert.Equal("cs", OpenSubs.OsLanguages("CS"));
        var http = BaseHttp();
        http.Add("GET", "/subtitles", Ok(D(("data", L(D(("id", "1")))))));
        var (c, _, _) = Client(http);
        c.Login(quiet: true);
        c.Search("tt0133093", null, null, "zh");
        var call = http.CallsTo("/subtitles")[0];
        Assert.Equal("zh-cn,zh-tw", call.Params["languages"]);
        Assert.Equal(call.Params.Keys.Order(StringComparer.Ordinal), call.Params.Keys); // theo chu cai: khong chuyen huong 301
    }

    [Fact]
    public void Absolute_episode_without_season()
    {
        var http = BaseHttp();
        http.Add("GET", "/subtitles", Ok(D(("data", L()))));
        var (c, _, job) = Client(http);
        c.Login(quiet: true);
        Assert.Empty(c.Search(null, null, 643, "en", "One Piece"));
        Assert.Empty(http.CallsTo("/subtitles"));
        Assert.Contains(job.Lines, l => l.Message.Contains("không rõ season", StringComparison.Ordinal));
        Assert.Empty(c.Search("tt0388629", null, 643, "en", "One Piece"));
        var calls = http.CallsTo("/subtitles");
        Assert.Equal(2, calls.Count);
        Assert.All(calls, x => Assert.True(!x.Params.ContainsKey("query") && x.Params["episode_number"] == "643"));
        // co season (TMDB doi so tuyet doi -> S16E01): du 3 luot nhu .bat
        c.Search("tt0388629", 16, 1, "en", "One Piece");
        Assert.Equal(5, http.CallsTo("/subtitles").Count);
        Assert.True(http.CallsTo("/subtitles")[^1].Params.ContainsKey("query"));
    }
}
