using System.Globalization;
using System.Text.Json;
using JDict = System.Collections.Generic.OrderedDictionary<string, object?>;

namespace MuxStudio.Core;

/// <summary>
/// TheTVDB v4 (thetvdb.com) — đặt tên phim lẻ / phim bộ chuẩn Jellyfin <c>[tvdbid-…]</c>. Port <c>core/tvdb.py</c>
/// (<c>TvdbClient</c>): thay <c>Invoke-TvdbApi</c>, <c>Connect-Tvdb</c>, <c>Get-TvdbLocalName</c>, <c>Find-TvdbMovie</c>,
/// <c>Find-TvdbSeries</c>, <c>Get-TvdbEpisodes</c>, <c>Get-TvdbEpisodeName</c>, <c>Resolve-TvdbEpisode</c>, <c>Select-TvdbShow</c>.
/// <list type="bullet">
/// <item><c>POST /login {apikey, pin?}</c> → <c>data.token</c> (Bearer). 401 → đăng nhập lại MỘT lần.</item>
/// <item>Đọc phản hồi bằng UTF-8 (TVDB không luôn ghi charset).</item>
/// <item>404 im lặng khi được yêu cầu; lỗi 4xx khác → báo và thôi; lỗi mạng → thử lại, chờ 2ⁱ giây.</item>
/// </list>
/// </summary>
public sealed class TvdbClient : INameDb
{
    public const string Base = "https://api4.thetvdb.com/v4";

    private readonly Action<double> _sleep;
    private readonly Dictionary<(string Kind, string Title, int? Year), List<JDict>> _searchCache = [];
    private readonly Dictionary<(int Series, string SeasonType), List<JDict>> _epsCache = [];

    public TvdbClient(string? apiKey, string? pin = "", string? lang = "eng", string? seasonType = "default", int retry = 3,
                      Http? http = null, Action<double>? sleep = null, Job? job = null)
    {
        ApiKey = Py.StripWs(apiKey ?? "");
        Pin = Py.StripWs(pin ?? "");
        Lang = Py.StripWs(string.IsNullOrEmpty(lang) ? "eng" : lang);
        SeasonType = string.IsNullOrEmpty(seasonType) ? "default" : seasonType;
        Retry = Math.Max(1, retry);
        HttpClient = http ?? Http.Shared;
        Job = job ?? Job.Null;
        _sleep = sleep ?? (s => Http.Sleep(s, Job.Token));
    }

    public string DbName => "tvdb";

    public string ApiKey { get; }

    public string Pin { get; }

    public string Lang { get; }

    public string SeasonType { get; }

    public int Retry { get; }

    public Http HttpClient { get; }

    public Job Job { get; set; }

    public string? Token { get; private set; }

    public bool LoginFailed { get; private set; }

    public bool Usable => ApiKey.Length > 0 && !LoginFailed;

    // ------------------------------------------------------------------ goi API

    /// <summary><c>request</c> = <c>Invoke-TvdbApi</c>.</summary>
    public object? Request(string path, bool quiet404 = false, bool noAuth = false, JDict? body = null)
    {
        if (!noAuth && Token is null && !Login())
            return null;
        var url = Base + path;
        var tries = Math.Max(2, Retry);
        var relogged = false;
        var i = 0;
        while (i < tries)
        {
            i++;
            var h = new List<KeyValuePair<string, string>> { new("Accept", "application/json") };
            if (!noAuth)
                h.Add(new("Authorization", $"Bearer {Token}"));
            var code = 0;
            var msg = "";
            try
            {
                var r = body is not null
                    ? HttpClient.Request("POST", url, headers: h, json: body, timeout: 25, ct: Job.Token)
                    : HttpClient.Request("GET", url, headers: h, timeout: 25, ct: Job.Token);
                code = r.StatusCode;
                if (code == 200)
                {
                    try
                    {
                        return PyJson.Loads(Py.Utf8Replace.GetString(r.Content));
                    }
                    catch (JsonException)
                    {
                        return null;
                    }
                }
                msg = Py.Utf8Replace.GetString(r.Content, 0, Math.Min(200, r.Content.Length));
            }
            catch (HttpNetworkException e)
            {
                msg = e.Message;
            }
            if (code == 404)
            {
                if (!quiet404)
                    Job.Warn($"  [!] TVDB không có: {path}");
                return null;
            }
            if (code == 401 && !noAuth && !relogged)
            {
                relogged = true; // token het han -> dang nhap lai MOT lan
                Token = null;
                if (Login())
                {
                    i--;
                    continue;
                }
                return null;
            }
            if (code is >= 400 and < 500)
            {
                Job.Error(string.Create(CultureInfo.InvariantCulture, $"  [!] TVDB từ chối yêu cầu ({code}): {msg}"));
                return null;
            }
            if (i < Retry)
            {
                var wait = 1 << i;
                Job.Warn(string.Create(CultureInfo.InvariantCulture, $"  [!] TVDB lỗi mạng (lần {i}/{Retry}), thử lại sau {wait} giây..."));
                _sleep(wait);
            }
            else
            {
                Job.Error($"  [!] TVDB thất bại: {msg}");
                return null;
            }
        }
        return null;
    }

    /// <summary><c>login</c> = <c>Connect-Tvdb</c>.</summary>
    public bool Login()
    {
        if (Token is not null)
            return true;
        if (LoginFailed || ApiKey.Length == 0)
            return false;
        var b = new JDict(StringComparer.Ordinal) { ["apikey"] = ApiKey };
        if (Pin.Length > 0)
            b["pin"] = Pin;
        var r = Request("/login", noAuth: true, body: b);
        var tok = PyDict.AsDict(r).GetDict("data").Get("token");
        if (Py.Truthy(tok))
        {
            Token = Py.Str(tok);
            return true;
        }
        LoginFailed = true;
        Job.Error("[!] Đăng nhập TVDB thất bại — kiểm tra TVDB API key (và PIN nếu key của bạn cần PIN).");
        return false;
    }

    // ------------------------------------------------------------------ tim kiem

    /// <summary><c>local_name</c> = <c>Get-TvdbLocalName</c>: <c>TvdbLang</c> → <c>eng</c> → tên gốc.</summary>
    public string LocalName(JDict obj)
    {
        if (obj.Get("translations") is JDict tr)
        {
            foreach (var lg in new[] { Lang, "eng" })
            {
                var v = tr.Get(lg);
                if (Py.Truthy(v))
                    return Py.Str(v);
            }
        }
        return obj.GetStr("name");
    }

    private List<JDict> SearchCore(string kind, string title, int? year)
    {
        if (string.IsNullOrEmpty(title))
            return [];
        var key = (kind, title, year);
        if (_searchCache.TryGetValue(key, out var hit))
            return hit;
        List<JDict> Do()
        {
            var hasYear = year is { } y && y != 0;
            var q = $"/search?query={PyUrl.Quote(title, safe: "")}&type={kind}"
                    + (hasYear ? "&year=" + year!.Value.ToString(CultureInfo.InvariantCulture) : "");
            var r = Request(q);
            var data = PyDict.AsDict(r).GetList("data").ToList();
            if (data.Count == 0 && hasYear)
                return SearchCore(kind, title, null);
            var outp = new List<JDict>();
            foreach (var x in data.OfType<JDict>())
            {
                if (!Py.Truthy(x.Get("tvdb_id")))
                    continue;
                var yy = x.GetStr("year");
                var fa = x.GetStr("first_air_time");
                if (yy.Length == 0 && fa.Length >= 4)
                    yy = fa[..4];
                var date = yy.Length > 0 ? $"{yy}-01-01" : "";
                long tid = Py.ToInt(x.Get("tvdb_id"));
                if (kind == "movie")
                {
                    outp.Add(new JDict(StringComparer.Ordinal)
                    {
                        ["id"] = tid, ["title"] = LocalName(x), ["original_title"] = x.GetStr("name"), ["release_date"] = date,
                    });
                }
                else
                {
                    outp.Add(new JDict(StringComparer.Ordinal)
                    {
                        ["id"] = tid, ["name"] = LocalName(x), ["original_name"] = x.GetStr("name"), ["first_air_date"] = date,
                    });
                }
            }
            return outp;
        }
        var v = Do();
        _searchCache[key] = v;
        return v;
    }

    /// <summary><c>search_movie</c> = <c>Find-TvdbMovie</c> → cùng dạng kết quả TMDB (title / original_title / release_date).</summary>
    public List<JDict> SearchMovie(string title, int? year) => SearchCore("movie", title, year);

    /// <summary><c>search_series</c> = <c>Find-TvdbSeries</c> → cùng dạng TMDB (name / original_name / first_air_date).</summary>
    public List<JDict> SearchSeries(string title, int? year) => SearchCore("series", title, year);

    // ------------------------------------------------------------------ tap

    /// <summary><c>episodes</c> = <c>Get-TvdbEpisodes</c>: <c>/series/{id}/episodes/{TvdbSeasonType}?page=N</c>, theo
    /// <c>links.next</c>, ≤ 30 trang.</summary>
    public List<JDict> Episodes(int seriesId)
    {
        var key = (seriesId, SeasonType);
        if (_epsCache.TryGetValue(key, out var hit))
            return hit;
        var outp = new List<JDict>();
        for (var p = 0; p < 30; p++)
        {
            var r = PyDict.AsDict(Request(string.Create(CultureInfo.InvariantCulture,
                $"/series/{seriesId}/episodes/{SeasonType}?page={p}"), quiet404: true));
            var data = r.Get("data");
            if (!Py.Truthy(data))
                break;
            outp.AddRange(PyDict.AsDict(data).GetList("episodes").OfType<JDict>());
            if (!Py.Truthy(r.GetDict("links").Get("next")))
                break;
        }
        _epsCache[key] = outp;
        return outp;
    }

    /// <summary><c>episode_name</c> = <c>Get-TvdbEpisodeName</c>: <c>/episodes/{id}/translations/{lang}</c> (ngôn ngữ cấu hình,
    /// rồi <c>eng</c>).</summary>
    public string? EpisodeName(JDict e)
    {
        var have = e.GetList("nameTranslations").ToList();
        foreach (var lg in new[] { Lang, "eng" }.Distinct(StringComparer.Ordinal))
        {
            if (have.Count > 0 && !have.Any(x => Py.Equal(x, lg)))
                continue;
            var t = Request($"/episodes/{Py.Str(e.Get("id"))}/translations/{lg}", quiet404: true);
            var nm = PyDict.AsDict(t).GetDict("data").Get("name");
            if (Py.Truthy(nm))
                return Py.Str(nm);
        }
        return null;
    }

    /// <summary><c>resolve_episode</c> = <c>Resolve-TvdbEpisode</c>: khớp <c>seasonNumber</c>+<c>number</c>; không có thì coi là
    /// số tuyệt đối (<c>absoluteNumber</c>).</summary>
    public EpisodeHit ResolveEpisode(int showId, int season, int episode, bool wantTitle = true)
    {
        var lst = Episodes(showId);
        if (lst.Count == 0)
            return new EpisodeHit(season, episode);
        JDict? hit = null;
        var mapped = false;
        if (season >= 1)
            hit = lst.FirstOrDefault(x => x.GetInt("seasonNumber") == season && x.GetInt("number") == episode);
        if (hit is null)
        {
            hit = lst.FirstOrDefault(x => x.GetInt("seasonNumber") >= 1 && x.GetInt("absoluteNumber") == episode);
            mapped = hit is not null;
        }
        if (hit is null)
            return new EpisodeHit(season, episode);
        var title = wantTitle ? EpisodeName(hit) : null;
        return new EpisodeHit(hit.GetInt("seasonNumber"), hit.GetInt("number"), title, mapped, true);
    }

    /// <summary><c>pick_show</c> = <c>Select-TvdbShow</c>: nhiều series trùng tên, tên file không có năm → series thực sự có tập này.</summary>
    public (JDict? Show, bool Sure) PickShow(List<JDict> res, EpisodeInfo ep)
    {
        if (res.Count == 0)
            return (null, false);
        var want = Naming.NormTitle(ep.Series);
        var exact = res.Where(r => Naming.NormTitle(r.Get("name") as string) == want || Naming.NormTitle(r.Get("original_name") as string) == want)
                       .ToList();
        if ((ep.Year is { } y && y != 0) || exact.Count <= 1)
        {
            var p = Naming.PickMatch(res, ep.Series, ep.Year, "name", "original_name", "first_air_date");
            return p is not null ? (p, true) : (res[0], false);
        }
        Job.Warn(string.Create(CultureInfo.InvariantCulture,
            $"  [?] Có {exact.Count} series cùng tên '{ep.Series}' — kiểm tra series nào có {ep.RefLabel}..."));
        foreach (var c in exact.Take(6))
        {
            var id = Py.ToInt(c["id"]);
            var n = Episodes(id).Count;
            var rt = ResolveEpisode(id, ep.Season, ep.Episode, false);
            var yy = Naming.YearOf(c.Get("first_air_date")) is { } yv && yv != 0 ? yv.ToString(CultureInfo.InvariantCulture) : "????";
            Job.Dim(string.Create(CultureInfo.InvariantCulture,
                $"      - {Py.Str(c.Get("name"))} ({yy})  tvdbid-{Py.Str(c["id"])}: {n} tập") + (rt.Found ? "  ← có tập này" : ""));
            if (rt.Found)
            {
                Job.Ok($"  [i] → Chọn: {Py.Str(c.Get("name"))} ({yy})");
                return (c, true);
            }
        }
        Job.Warn($"  [!] Không series nào có {ep.RefLabel}.");
        var p2 = Naming.PickMatch(res, ep.Series, ep.Year, "name", "original_name", "first_air_date");
        return p2 is not null ? (p2, true) : (res[0], false);
    }

    // ------------------------------------------------------------------ kiem tra ket noi

    /// <summary><c>check</c>.</summary>
    public ConnStatus Check()
    {
        if (ApiKey.Length == 0)
            return new ConnStatus(null, "TVDB: chưa điền key");
        if (Login())
            return new ConnStatus(true, "TVDB: sẵn sàng (đăng nhập được)");
        return new ConnStatus(false, "TVDB: đăng nhập lỗi");
    }
}
