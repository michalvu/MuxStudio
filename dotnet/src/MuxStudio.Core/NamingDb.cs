using System.Globalization;
using JDict = System.Collections.Generic.OrderedDictionary<string, object?>;

namespace MuxStudio.Core;

/// <summary>
/// Tên + thư mục con (trong <c>Output</c>) của file kết quả, chưa gắn thẻ codec. KHÔNG tạo thư mục nào (TĐ-21).
/// Port <c>naming.OutputName</c> (lớp có thể sửa — bản Python sửa thẳng đối tượng, kể cả đối tượng trong bộ nhớ đệm).
/// </summary>
public sealed class OutputName
{
    public OutputName(string @base, string relDir = "", string db = "", string status = "keep", string note = "", string kind = "movie",
                      List<NameCandidate>? candidates = null, string episodeNote = "", string queryKey = "", List<JDict>? raw = null)
    {
        Base = @base;
        RelDir = relDir;
        Db = db;
        Status = status;
        Note = note;
        Kind = kind;
        Candidates = candidates ?? [];
        EpisodeNote = episodeNote;
        QueryKey = queryKey;
        Raw = raw ?? [];
    }

    public string Base { get; set; }

    /// <summary>"" = ngay trong <c>Output</c>.</summary>
    public string RelDir { get; set; }

    /// <summary>Database đã dùng ("" = giữ tên gốc).</summary>
    public string Db { get; set; }

    /// <summary><c>ok</c> | <c>need_choice</c> (Cần chọn) | <c>keep</c> (giữ tên gốc).</summary>
    public string Status { get; set; }

    public string Note { get; set; }

    /// <summary><c>movie</c> | <c>tv</c>.</summary>
    public string Kind { get; set; }

    public List<NameCandidate> Candidates { get; set; }

    public string EpisodeNote { get; set; }

    /// <summary>Cùng khoá = cùng lựa chọn (vd mọi tập của một series). "" = không có khoá (<c>()</c> của Python).
    /// Khoá dựng bằng <see cref="Naming.QueryKey"/>.</summary>
    public string QueryKey { get; set; }

    public List<JDict> Raw { get; set; }

    public bool IsEpisode => Kind == "tv";

    /// <summary><c>OutputName(**o.__dict__)</c>: bản sao nông.</summary>
    public OutputName Copy() => new(Base, RelDir, Db, Status, Note, Kind, Candidates, EpisodeNote, QueryKey, Raw);
}

/// <summary>Phần đặt tên theo TMDB / TVDB của <c>core/naming.py</c> (bước ③).</summary>
public static partial class Naming
{
    /// <summary>Khoá lựa chọn (tuple của Python: <c>("movie", db, title, year)</c>, <c>("tv", db, norm_title, year)</c>,
    /// <c>("movie", db)</c>) dưới dạng chuỗi — <c>None</c> ghi là "None" như <c>str()</c>.</summary>
    public static string QueryKey(params object?[] parts) => string.Join("\u001f", parts.Select(Py.Str));

    /// <summary><c>pick_match</c>: phần không hỏi của <c>Select-TmdbCandidate</c> — tên chuẩn hoá trùng (tên hoặc tên gốc) và năm ±1.</summary>
    public static JDict? PickMatch(List<JDict> res, string wantTitle, int? wantYear, string nameProp, string origProp, string dateProp)
    {
        var want = NormTitle(wantTitle);
        foreach (var r in res)
        {
            if (NormTitle(r.Get(nameProp) as string) != want && NormTitle(r.Get(origProp) as string) != want)
                continue;
            var y = YearOf(r.Get(dateProp));
            if (wantYear is { } wy && wy != 0 && (y is null || Math.Abs(y.Value - wy) > 1))
                continue;
            return r;
        }
        return null;
    }

    /// <summary><c>keep_name</c>.</summary>
    public static OutputName KeepName(string origBase, string note, string kind = "movie", string queryKey = "") =>
        new(origBase, "", "", "keep", note, kind, queryKey: queryKey);

    private static List<NameCandidate> Cands(List<JDict> res, string nameProp, string origProp, string dateProp) =>
        res.Take(5).Select(r => new NameCandidate(r.GetInt("id"), r.GetStr(nameProp), YearOf(r.Get(dateProp)), r.GetStr(origProp))).ToList();

    /// <summary><c>movie_name_from</c>: dựng tên phim lẻ từ kết quả đã chọn: <c>Tên (Năm) [tmdbid-…]</c> (+ thư mục riêng nếu
    /// <c>TmdbCreateFolder</c>).</summary>
    public static OutputName MovieNameFrom(string dbName, JDict pick, int? parsedYear, Config cfg)
    {
        var yr = YearOf(pick.Get("release_date")) is { } y0 && y0 != 0 ? y0 : parsedYear;
        var key = QueryKey("movie", dbName);
        if (yr is null or 0)
            return KeepName("", $"{dbName.ToUpperInvariant()} không có năm phát hành → giữ tên file gốc.", "movie", key);
        var b = MovieBaseName(pick.GetStr("title"), yr.Value, dbName, pick.Get("id"));
        return new OutputName(b, cfg.Bool("TmdbCreateFolder") ? b : "", dbName, "ok", $"Phim lẻ ({dbName.ToUpperInvariant()}): {b}", "movie");
    }

    /// <summary><c>movie_name</c> = <c>Get-MovieFileName</c>. Không khớp chắc chắn + <c>TmdbConfirm</c> → trạng thái "Cần chọn"
    /// (5 kết quả gần nhất).</summary>
    public static OutputName MovieName(INameDb db, string origBase, Config cfg, Dictionary<string, object?>? cache = null)
    {
        var name = db.DbName;
        var rn = SplitReleaseName(origBase);
        var key = QueryKey("movie", name, rn.Title, rn.Year);
        if (rn.Title.Length == 0)
            return KeepName(origBase, "Không tách được tên phim → giữ tên file gốc.", "movie", key);
        if (cache is not null && cache.TryGetValue(key, out var hit) && hit is OutputName ho)
            return FillKeep(ho, origBase);
        var res = db.SearchMovie(rn.Title, rn.Year).ToList();
        OutputName outp;
        if (res.Count == 0)
        {
            outp = KeepName(origBase, $"{name.ToUpperInvariant()} không tìm thấy phim này → giữ tên file gốc.", "movie", key);
        }
        else
        {
            var pick = PickMatch(res, rn.Title, rn.Year, "title", "original_title", "release_date");
            if (pick is null && !cfg.Bool("TmdbConfirm"))
                pick = res[0];
            if (pick is not null)
            {
                outp = MovieNameFrom(name, pick, rn.Year, cfg);
            }
            else
            {
                var yr = rn.Year is { } y && y != 0 ? $" ({y.ToString(CultureInfo.InvariantCulture)})" : "";
                outp = new OutputName(origBase, "", name, "need_choice",
                    $"{name.ToUpperInvariant()}: không khớp chính xác '{rn.Title}'" + yr + " — chọn một kết quả.", "movie",
                    Cands(res, "title", "original_title", "release_date"), raw: [.. res.Take(5)]);
            }
        }
        outp.QueryKey = key;
        if (cache is not null)
            cache[key] = outp;
        return FillKeep(outp, origBase);
    }

    /// <summary><c>_fill_keep</c>: kết quả trong bộ nhớ đệm dùng chung cho nhiều file: tên 'giữ gốc' / 'cần chọn' là tên của TỪNG file.</summary>
    private static OutputName FillKeep(OutputName o, string origBase)
    {
        if (o.Status is "keep" or "need_choice")
        {
            var c = o.Copy();
            c.Base = origBase;
            return c;
        }
        return o;
    }

    /// <summary><c>tv_name_from</c>: dựng tên tập: <c>Series (Năm) [tmdbid-…]\Season 01\Series (Năm) - S01E05 - Tên tập</c>.</summary>
    public static OutputName TvNameFrom(INameDb db, JDict show, EpisodeInfo ep, Config cfg)
    {
        var name = db.DbName;
        var sid = show.GetInt("id");
        var yr = YearOf(show.Get("first_air_date")) is { } y0 && y0 != 0 ? y0 : ep.Year;
        var folder = ShowFolderName(show.GetStr("name"), yr, name, sid);
        int se = ep.Season, en = ep.Episode;
        var end = ep.EpisodeEnd;
        string? title = null;
        var note = "";
        var fetchTitle = cfg.Bool("TvFetchEpisodeTitle");
        if ((fetchTitle || cfg.Bool("TvAbsoluteToSeason")) && end is null or 0)
        {
            var rt = db.ResolveEpisode(sid, se, en, fetchTitle);
            if (rt.Mapped)
            {
                note = string.Create(CultureInfo.InvariantCulture,
                    $"Số tập tuyệt đối: {ep.RefLabel} → {name.ToUpperInvariant()} S{rt.Season:00}E{rt.Episode:00}");
                if (cfg.Bool("TvAbsoluteToSeason"))
                    (se, en) = (rt.Season, rt.Episode);
            }
            else if (!rt.Found)
            {
                note = $"{name.ToUpperInvariant()} không có {ep.RefLabel} → giữ mã tập gốc, không có tên tập.";
            }
            if (fetchTitle)
                title = rt.Title;
        }
        se = Math.Max(1, se); // khong bao gio Season 00
        var b = EpisodeBaseName(show.GetStr("name"), yr, EpisodeCode(se, en, end), title);
        var rel = cfg.Bool("TvSeasonFolder") ? Py.Join(folder, SeasonFolderName(se)) : folder;
        return new OutputName(b, rel, name, "ok", $"TV Show ({name.ToUpperInvariant()}): {b}", "tv", episodeNote: note);
    }

    /// <summary><c>tv_name</c> = <c>Get-TmdbTvName</c> / <c>Get-TvdbTvName</c>: series trùng tên → chọn series thực sự có tập đó.</summary>
    public static OutputName TvName(INameDb db, string origBase, Config cfg, Dictionary<string, object?>? cache = null)
    {
        var name = db.DbName;
        var ep = Episodes.ParseEpisode(origBase);
        if (ep is null || ep.Series.Length == 0)
            return KeepName(origBase, "Không tách được tên series → giữ tên file gốc.", "tv");
        var key = QueryKey("tv", name, NormTitle(ep.Series), ep.Year);
        JDict? show;
        if (cache is not null && cache.TryGetValue(key, out var hit))
        {
            show = hit as JDict;
        }
        else
        {
            var res = db.SearchSeries(ep.Series, ep.Year).ToList();
            show = null;
            if (res.Count > 0)
            {
                var (pick, sure) = db.PickShow(res, ep);
                if (pick is not null && (sure || !cfg.Bool("TmdbConfirm")))
                    show = pick;
                else if (pick is not null)
                    show = new JDict(StringComparer.Ordinal) { ["_need_choice"] = true, ["res"] = res.Take(5).Cast<object?>().ToList() };
            }
            if (cache is not null)
                cache[key] = show;
        }
        if (show is null)
            return KeepName(origBase, $"{name.ToUpperInvariant()} không tìm thấy series '{ep.Series}' → giữ tên file gốc.", "tv", key);
        if (Py.Truthy(show.Get("_need_choice")))
        {
            var res = show.GetList("res").OfType<JDict>().ToList();
            var yr = ep.Year is { } y && y != 0 ? $" ({y.ToString(CultureInfo.InvariantCulture)})" : "";
            return new OutputName(origBase, "", name, "need_choice",
                $"{name.ToUpperInvariant()}: không khớp chính xác series '{ep.Series}'" + yr + " — chọn một kết quả.", "tv",
                Cands(res, "name", "original_name", "first_air_date"), queryKey: key, raw: res);
        }
        var outp = TvNameFrom(db, show, ep, cfg);
        outp.QueryKey = key;
        return outp;
    }

    /// <summary><c>choose_candidate</c>: bạn chọn một kết quả ở dòng "Cần chọn" → tên mới. Ghi vào bộ nhớ đệm để mọi file cùng
    /// khoá (vd các tập khác của series đó) dùng luôn lựa chọn này khi lập lại kế hoạch.</summary>
    public static OutputName ChooseCandidate(INameDb db, string origBase, long candId, OutputName current, Config cfg,
                                             Dictionary<string, object?>? cache = null)
    {
        var pick = current.Raw.FirstOrDefault(r => r.GetInt("id") == candId);
        if (pick is null)
            return current;
        if (current.Kind == "movie")
        {
            var outp = MovieNameFrom(db.DbName, pick, SplitReleaseName(origBase).Year, cfg);
            outp.QueryKey = current.QueryKey;
            if (cache is not null && current.QueryKey.Length > 0)
                cache[current.QueryKey] = outp;
            return FillKeep(outp, origBase);
        }
        if (cache is not null && current.QueryKey.Length > 0)
            cache[current.QueryKey] = pick;
        return cache is not null ? TvName(db, origBase, cfg, cache) : TvNameFrom(db, pick, Episodes.ParseEpisode(origBase)!, cfg);
    }

    /// <summary><c>search_candidates</c>: nút "Tìm lại với tên…" của dòng "Cần chọn" (SPEC mục 6 #20) — tìm lại trên database đã
    /// chọn bằng tên bạn gõ. Kết quả là dòng vẫn "Cần chọn" với tối đa 5 kết quả mới, CÙNG <see cref="OutputName.QueryKey"/>.</summary>
    public static OutputName SearchCandidates(INameDb db, string origBase, OutputName current, string? title, int? year = null)
    {
        var name = db.DbName;
        var t = Py.StripWs(title ?? "");
        List<JDict> res;
        List<NameCandidate> cands;
        if (current.Kind == "tv")
        {
            res = t.Length > 0 ? db.SearchSeries(t, year).ToList() : [];
            cands = Cands(res, "name", "original_name", "first_air_date");
        }
        else
        {
            res = t.Length > 0 ? db.SearchMovie(t, year).ToList() : [];
            cands = Cands(res, "title", "original_title", "release_date");
        }
        var what = $"'{t}'" + (year is { } y && y != 0 ? $" ({y.ToString(CultureInfo.InvariantCulture)})" : "");
        var note = res.Count > 0
            ? $"{name.ToUpperInvariant()}: kết quả tìm {what} — chọn một kết quả."
            : $"{name.ToUpperInvariant()}: không tìm thấy {what} — thử tên khác hoặc giữ tên gốc.";
        return new OutputName(origBase, "", name, "need_choice", note, current.Kind, cands, queryKey: current.QueryKey, raw: [.. res.Take(5)]);
    }

    /// <summary><c>db_usable</c> = <c>Test-DbUsable</c>: có key và key không bị từ chối / đăng nhập không lỗi.</summary>
    public static bool DbUsable(INameDb? db) => db is not null && db.Usable;

    /// <summary>
    /// <c>resolve_output_name</c> = <c>Resolve-OutputName</c>: phim bộ hay phim lẻ, theo database đã chọn (<c>tmdb</c> | <c>tvdb</c> |
    /// <c>none</c>). Chỉ chuyển sang database kia khi <c>NameFallbackOther</c>. KHÔNG tạo thư mục (TĐ-21).
    /// </summary>
    public static OutputName ResolveOutputName(string origBase, string choice, IReadOnlyDictionary<string, INameDb?> dbs, Config cfg,
                                               Dictionary<string, object?>? cache = null)
    {
        var kind = Episodes.ParseEpisode(origBase) is not null ? "tv" : "movie";
        if (choice is not ("tmdb" or "tvdb"))
            return KeepName(origBase, "Không đổi tên (bạn chọn giữ tên gốc).", kind);
        var other = choice == "tmdb" ? "tvdb" : "tmdb";
        Func<INameDb, string, Config, Dictionary<string, object?>?, OutputName> fn = kind == "tv" ? TvName : MovieName;
        var db = dbs.GetValueOrDefault(choice);
        var outp = DbUsable(db)
            ? fn(db!, origBase, cfg, cache)
            : KeepName(origBase, $"{choice.ToUpperInvariant()} chưa dùng được → giữ tên file gốc.", kind);
        if (outp.Status == "keep" && cfg.Bool("NameFallbackOther") && DbUsable(dbs.GetValueOrDefault(other)))
        {
            var alt = fn(dbs[other]!, origBase, cfg, cache);
            if (alt.Status != "keep")
            {
                alt.Note = $"{choice.ToUpperInvariant()} không xác định được → thử {other.ToUpperInvariant()}: {alt.Note}";
                return alt;
            }
        }
        return outp;
    }
}
