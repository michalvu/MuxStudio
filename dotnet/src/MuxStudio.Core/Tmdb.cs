using System.Globalization;
using JDict = System.Collections.Generic.OrderedDictionary<string, object?>;

namespace MuxStudio.Core;

/// <summary>Kết quả "Kiểm tra kết nối" của một nguồn (port <c>tmdb.ConnStatus</c>). <see cref="Ok"/> = <c>null</c>: chưa có key.</summary>
public sealed record ConnStatus(bool? Ok, string Text);

/// <summary>
/// Database đặt tên cho bước ③ (giao diện "vịt" của <c>naming.py</c>): <c>db_name</c>, <c>usable</c>, <c>search_movie</c>,
/// <c>search_series</c>, <c>pick_show</c>, <c>resolve_episode</c>. Kết quả tìm phim lẻ: <c>{id, title, original_title,
/// release_date}</c>; phim bộ: <c>{id, name, original_name, first_air_date}</c>.
/// </summary>
public interface INameDb
{
    string DbName { get; }

    bool Usable { get; }

    List<JDict> SearchMovie(string title, int? year);

    List<JDict> SearchSeries(string title, int? year);

    /// <summary>Trả (series, chắc_chắn).</summary>
    (JDict? Show, bool Sure) PickShow(List<JDict> res, EpisodeInfo ep);

    EpisodeHit ResolveEpisode(int showId, int season, int episode, bool wantTitle = true);
}

/// <summary>
/// TMDB (themoviedb.org) — port <c>core/tmdb.py</c> (<c>TmdbClient</c>).
/// <list type="bullet">
/// <item>Bước ①: IMDb ID (<c>Get-ImdbId</c>/<c>Get-ImdbIdCached</c> của SubFetch) và tên chính thức.</item>
/// <item>Bước ③: <c>Invoke-TmdbApi</c>, <c>Find-TmdbMovie</c>, <c>Find-TmdbTv</c>, <c>Get-TmdbTvDetails</c>, <c>Select-TmdbTvShow</c>,
/// <c>Get-TmdbEpisodeTitle</c> + <c>Resolve-TmdbEpisode</c>.</item>
/// </list>
/// Key 32 ký tự → <c>?api_key=</c>; token dài bắt đầu <c>eyJ</c> → <c>Authorization: Bearer</c> (§6.10). 401 → tắt TMDB cả lượt
/// chạy, báo MỘT lần. 404 khi dò tập là bình thường → im lặng. Lỗi mạng → thử lại <c>TmdbRetry</c> lần, chờ 2, 4… giây.
/// </summary>
public sealed class TmdbClient : INameDb
{
    public const string Base = "https://api.themoviedb.org/3";

    private readonly Action<double> _sleep;
    private readonly Dictionary<(string Kind, string Title, int? Year), List<JDict>> _searchCache = [];
    private readonly Dictionary<int, object?> _tvCache = [];
    private readonly Dictionary<(int Tv, int Season, int Episode), object?> _epCache = [];
    private readonly Dictionary<(string Title, int? Year, bool IsTv, int? Season, int? Episode), string?> _imdbCache = [];

    /// <param name="sleep">(cho test) thay <c>time.sleep</c>; bỏ trống = chờ thật (bấm "Dừng ngay" thì thôi chờ).</param>
    public TmdbClient(string? apiKey, string lang = "en-US", int retry = 3, Http? http = null, Action<double>? sleep = null,
                      Job? job = null)
    {
        ApiKey = Py.StripWs(apiKey ?? "");
        Lang = lang;
        Retry = Math.Max(1, retry);
        HttpClient = http ?? Http.Shared;
        Job = job ?? Job.Null;
        _sleep = sleep ?? (s => Http.Sleep(s, Job.Token));
    }

    public string DbName => "tmdb";

    public string ApiKey { get; }

    public string Lang { get; }

    public int Retry { get; }

    public Http HttpClient { get; }

    public Job Job { get; set; }

    public bool AuthFailed { get; private set; }

    public bool Usable => ApiKey.Length > 0 && !AuthFailed;

    // ------------------------------------------------------------------ goi API

    /// <summary><c>get</c> = <c>Invoke-TmdbApi</c>. Tham số rỗng / <c>null</c> bị bỏ.</summary>
    public object? Get(string path, IEnumerable<KeyValuePair<string, object?>>? parameters = null, bool quiet404 = false)
    {
        if (!Usable)
            return null;
        var q = new List<KeyValuePair<string, string?>>();
        foreach (var (k, v) in parameters ?? [])
        {
            if (v is null || v is string { Length: 0 })
                continue;
            q.Add(new(k, Py.Str(v)));
        }
        var headers = new List<KeyValuePair<string, string>> { new("Accept", "application/json") };
        if (ApiKey.StartsWith("eyJ", StringComparison.Ordinal))
            headers.Add(new("Authorization", $"Bearer {ApiKey}"));
        else
            q.Add(new("api_key", ApiKey));
        for (var i = 1; i <= Retry; i++)
        {
            var code = 0;
            var msg = "";
            try
            {
                var r = HttpClient.Request("GET", Base + path, q, headers, timeout: 20, ct: Job.Token);
                code = r.StatusCode;
                if (code == 200)
                    return r.JsonOrNull();
                msg = Py.Slice(r.Text, 0, 200);
            }
            catch (HttpNetworkException e)
            {
                msg = e.Message;
            }
            if (code == 404)
            {
                if (!quiet404)
                    Job.Warn($"  [!] TMDB không có dữ liệu (404): {path}");
                return null;
            }
            if (code == 401)
            {
                Job.Error("  [!] TMDB từ chối API key (401) → tắt TMDB cho lần chạy này.");
                Job.Warn("      Kiểm tra TMDB key: dán 'API Key' (32 ký tự) hoặc 'API Read Access Token' (bắt đầu eyJ).");
                AuthFailed = true;
                return null;
            }
            if (i < Retry)
            {
                var wait = 1 << i;
                Job.Warn(string.Create(CultureInfo.InvariantCulture, $"  [!] TMDB lỗi mạng (lần {i}/{Retry}), thử lại sau {wait} giây..."));
                _sleep(wait);
            }
            else
            {
                Job.Error(string.Create(CultureInfo.InvariantCulture, $"  [!] TMDB thất bại sau {Retry} lần: {msg}"));
            }
        }
        return null;
    }

    private static KeyValuePair<string, object?> P(string k, object? v) => new(k, v);

    // ------------------------------------------------------------------ tim kiem

    /// <summary><c>search</c> = <c>Find-TmdbMovie</c>/<c>Find-TmdbTv</c>: có năm mà không ra → bỏ năm tìm lại. Có bộ nhớ đệm.</summary>
    public List<JDict> Search(string kind, string title, int? year)
    {
        if (string.IsNullOrEmpty(title))
            return [];
        var key = (kind, title, year);
        if (_searchCache.TryGetValue(key, out var hit))
            return hit;
        List<JDict> Do()
        {
            var ps = new List<KeyValuePair<string, object?>> { P("language", Lang), P("include_adult", "false"), P("query", title) };
            if (year is { } y && y != 0)
                ps.Add(P(kind == "tv" ? "first_air_date_year" : "year", y));
            var r = Get($"/search/{kind}", ps);
            var res = PyDict.AsDict(r).GetList("results").OfType<JDict>().ToList();
            if (res.Count == 0 && year is { } y2 && y2 != 0 && r is not null)
                return Search(kind, title, null);
            return res;
        }
        var v = Do();
        _searchCache[key] = v;
        return v;
    }

    /// <summary><c>tv_details</c> = <c>Get-TmdbTvDetails</c> (có cache).</summary>
    public JDict? TvDetails(int tvId)
    {
        if (!_tvCache.TryGetValue(tvId, out var v))
        {
            v = Get($"/tv/{tvId.ToString(CultureInfo.InvariantCulture)}", [P("language", Lang)], quiet404: true);
            _tvCache[tvId] = v;
        }
        return v as JDict;
    }

    /// <summary><c>pick_exact</c>: phần không hỏi của <c>Select-TmdbCandidate</c> — trùng tên chuẩn hoá và năm ±1.</summary>
    public static JDict? PickExact(List<JDict> res, string wantTitle, int? wantYear, string nameProp, string origProp, string dateProp) =>
        Naming.PickMatch(res, wantTitle, wantYear, nameProp, origProp, dateProp);

    // ------------------------------------------------------------------ dat ten (buoc 3)

    /// <summary><c>search_movie</c> = <c>Find-TmdbMovie</c>.</summary>
    public List<JDict> SearchMovie(string title, int? year) => Search("movie", title, year);

    /// <summary><c>search_series</c> = <c>Find-TmdbTv</c>.</summary>
    public List<JDict> SearchSeries(string title, int? year) => Search("tv", title, year);

    /// <summary><c>pick_show</c> = <c>Select-TmdbTvShow</c> cho <c>naming.tv_name</c>.</summary>
    public (JDict? Show, bool Sure) PickShow(List<JDict> res, EpisodeInfo ep) => SelectShow(res, ep.Series, ep.Year, ep.Season, ep.Episode);

    /// <summary><c>episode_title</c> = <c>Get-TmdbEpisodeTitle</c> (404 im lặng, có cache).</summary>
    public string? EpisodeTitle(int tvId, int season, int episode)
    {
        var key = (tvId, season, episode);
        if (!_epCache.TryGetValue(key, out var r))
        {
            r = Get(string.Create(CultureInfo.InvariantCulture, $"/tv/{tvId}/season/{season}/episode/{episode}"), [P("language", Lang)],
                    quiet404: true);
            _epCache[key] = r;
        }
        var v = PyDict.AsDict(r).Get("name");
        return Py.Truthy(v) ? Py.Str(v) : null;
    }

    private static List<JDict> RealSeasons(JDict? d) =>
        d.GetList("seasons").OfType<JDict>().Where(s => s.GetInt("season_number") >= 1)
         .OrderBy(s => Py.ToInt(s.Get("season_number"))).ToList();

    /// <summary><c>resolve_episode</c> = <c>Resolve-TmdbEpisode</c>: tra thẳng → nếu ngoài phạm vi season: cộng dồn
    /// <c>episode_count</c> thử số tương đối, rồi số tuyệt đối ngay trong season đó (một số anime TMDB giữ số tuyệt đối).</summary>
    public EpisodeHit ResolveEpisode(int showId, int season, int episode, bool wantTitle = true)
    {
        if (season >= 1)
        {
            var t0 = EpisodeTitle(showId, season, episode);
            if (!string.IsNullOrEmpty(t0))
                return new EpisodeHit(season, episode, wantTitle ? t0 : null, false, true);
        }
        var d = TvDetails(showId);
        var ss = RealSeasons(d);
        var miss = new EpisodeHit(season, episode);
        if (ss.Count == 0)
            return miss;
        var cur = ss.FirstOrDefault(s => Py.ToInt(s.Get("season_number")) == season);
        if (cur is not null && episode <= cur.GetInt("episode_count"))
            return miss; // trong pham vi season -> khong phai so tuyet doi, chi la TMDB chua co ten
        if (AbsoluteToSeason(showId, episode) is not { } m)
            return miss;
        var (s_, rel) = m;
        var t = EpisodeTitle(showId, s_, rel);
        if (!string.IsNullOrEmpty(t))
            return new EpisodeHit(s_, rel, wantTitle ? t : null, true, true);
        t = EpisodeTitle(showId, s_, episode);
        if (!string.IsNullOrEmpty(t))
            return new EpisodeHit(s_, episode, wantTitle ? t : null, s_ != season, true);
        return miss;
    }

    /// <summary><c>select_show</c> = <c>Select-TmdbTvShow</c>: nhiều series TRÙNG TÊN (One Piece 1999 / 2023), tên file không có
    /// năm → chọn series thực sự CÓ season/tập đó. Trả (series, chắc_chắn).</summary>
    public (JDict? Show, bool Sure) SelectShow(List<JDict> res, string series, int? year, int? season, int? episode)
    {
        if (res.Count == 0)
            return (null, false);
        var want = Naming.NormTitle(series);
        var exact = res.Where(r => Naming.NormTitle(r.Get("name") as string) == want || Naming.NormTitle(r.Get("original_name") as string) == want)
                       .ToList();
        if ((year is { } y && y != 0) || exact.Count <= 1 || episode is null)
        {
            var p = PickExact(res, series, year, "name", "original_name", "first_air_date");
            return p is not null ? (p, true) : (res[0], false);
        }
        Job.Warn(string.Create(CultureInfo.InvariantCulture, $"  [?] Có {exact.Count} series cùng tên '{series}' — kiểm tra series nào có tập này..."));
        JDict? best = null;
        var bestScore = -1;
        foreach (var c in exact.Take(6))
        {
            var d = TvDetails(Py.ToInt(c.Get("id")));
            var ss = d.GetList("seasons").OfType<JDict>().Where(s => s.GetInt("season_number") >= 1).ToList();
            var hasSeason = season is not null && ss.Any(s => Py.ToInt(s.Get("season_number")) == season);
            var total = ss.Sum(s => s.GetInt("episode_count"));
            var score = (hasSeason ? 2 : 0) + (total >= episode ? 1 : 0);
            var yy = Naming.YearOf(c.Get("first_air_date")) is { } yv && yv != 0 ? yv.ToString(CultureInfo.InvariantCulture) : "????";
            Job.Dim(string.Create(CultureInfo.InvariantCulture,
                $"      - {Py.Str(c.Get("name"))} ({yy})  tmdbid-{Py.Str(c.Get("id"))}: {ss.Count} season, {total} tập"));
            if (score > bestScore)
            {
                best = c;
                bestScore = score;
            }
        }
        if (bestScore <= 0)
        {
            Job.Warn("  [!] Không series nào có tập này.");
            var p = PickExact(res, series, year, "name", "original_name", "first_air_date");
            return p is not null ? (p, true) : (res[0], false);
        }
        var by = Naming.YearOf(best!.Get("first_air_date")) is { } bv && bv != 0 ? bv.ToString(CultureInfo.InvariantCulture) : "????";
        Job.Ok($"  [i] → Chọn: {Py.Str(best.Get("name"))} ({by})");
        return (best, true);
    }

    /// <summary><c>absolute_to_season</c>: số tập tuyệt đối → (season, tập trong season) bằng cách cộng dồn <c>episode_count</c> (§6.10).</summary>
    public (int Season, int Episode)? AbsoluteToSeason(int tvId, int absEp)
    {
        var acc = 0;
        foreach (var s in RealSeasons(TvDetails(tvId)))
        {
            var n = s.GetInt("episode_count");
            if (absEp <= acc + n)
                return (Py.ToInt(s.Get("season_number")), absEp - acc);
            acc += n;
        }
        return null;
    }

    // ------------------------------------------------------------------ IMDb ID cho buoc 1

    /// <summary><c>find_show</c>.</summary>
    public JDict? FindShow(string series, int? year, int? season, int? episode)
    {
        var res = Search("tv", series, year);
        return SelectShow(res, series, year, season, episode).Show;
    }

    private JDict? PickMovieOrTv(string title, int? year, bool isTv, int? season, int? episode)
    {
        if (isTv)
            return FindShow(title, year, season, episode);
        var res = Search("movie", title, year);
        return PickExact(res, title, year, "title", "original_title", "release_date") ?? (res.Count > 0 ? res[0] : null);
    }

    /// <summary><c>imdb_id</c>: IMDb ID (<c>tt…</c>) qua <c>/search/{movie|tv}</c> → <c>/{kind}/{id}/external_ids</c>. Có cache.</summary>
    public string? ImdbId(string title, int? year, bool isTv, int? season = null, int? episode = null)
    {
        if (!Usable || string.IsNullOrEmpty(title))
            return null;
        var key = (title, year, isTv, season, episode);
        if (_imdbCache.TryGetValue(key, out var hit))
            return hit;
        string? Do()
        {
            var kind = isTv ? "tv" : "movie";
            var pick = PickMovieOrTv(title, year, isTv, season, episode);
            if (pick is null || pick.Count == 0)
                return null;
            var e = Get($"/{kind}/{Py.Str(pick.Get("id"))}/external_ids");
            var v = PyDict.AsDict(e).Get("imdb_id");
            return Py.Truthy(v) ? Py.Str(v) : null;
        }
        var outp = Do();
        _imdbCache[key] = outp;
        return outp;
    }

    /// <summary><c>official_title</c>: tên CHÍNH THỨC của phim / series (đủ dấu câu: <c>The Lord of the Rings: The Return of the
    /// King</c>). Dùng cùng kết quả tìm (đã nhớ đệm) với <see cref="ImdbId"/> nên không tốn thêm lượt gọi. <c>null</c> = không có
    /// key / không tìm thấy.</summary>
    public string? OfficialTitle(string title, int? year, bool isTv, int? season = null, int? episode = null)
    {
        if (!Usable || string.IsNullOrEmpty(title))
            return null;
        var pick = PickMovieOrTv(title, year, isTv, season, episode);
        var name = pick.Get(isTv ? "name" : "title");
        if (!Py.Truthy(name))
            return null;
        var s = Py.StripWs(Py.Str(name));
        return s.Length > 0 ? s : null;
    }

    // ------------------------------------------------------------------ kiem tra ket noi

    /// <summary><c>check</c>.</summary>
    public ConnStatus Check()
    {
        if (ApiKey.Length == 0)
            return new ConnStatus(null, "TMDB: chưa điền key");
        var r = Get("/configuration", quiet404: true);
        if (AuthFailed)
            return new ConnStatus(false, "TMDB: key bị từ chối (401)");
        if (r is null)
            return new ConnStatus(false, "TMDB: không kết nối được");
        var kind = ApiKey.StartsWith("eyJ", StringComparison.Ordinal) ? "token (Bearer)" : "API key";
        return new ConnStatus(true, $"TMDB: sẵn sàng ({kind} hợp lệ)");
    }
}
