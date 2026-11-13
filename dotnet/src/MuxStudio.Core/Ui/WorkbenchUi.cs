using System.Globalization;

namespace MuxStudio.Core.Ui;

/// <summary>Một luồng của file đang chọn ở thẻ ③ (thẻ "VIDEO / AUDIO / SUB" của bản thiết kế). <see cref="State"/>: <c>keep</c> | <c>drop</c>.
/// <see cref="CanToggle"/>: audio đổi được bằng ô "Xoá" (bình luận / thuyết minh thì không).</summary>
public sealed record StreamCard(string Kind, int Id, string Badge, string Title, string Detail, string State, string StateText, bool CanToggle,
                                bool IsDefault);

/// <summary>Thẻ "WORKBENCH STATE": giá trị (chữ tiếng Việt gốc — dịch ở chỗ hiển thị) + loại màu (<c>good need bad dim</c>, rỗng = chữ thường).</summary>
public sealed record WorkbenchState(string Folder, string Langs, string LangsKind, string Ref, string Db, string DbKind, string Videos, string Subs,
                                    string Refs, string RefsKind);

/// <summary>
/// Dữ liệu cho phần trình bày kiểu "Concept 3 Pro Workbench" (nhãn màu, giỏ phụ đề, thẻ luồng, ô thống kê). CHỈ đọc dữ liệu thật của lõi — không
/// đổi kế hoạch, không đổi kết quả. Loại màu: <c>good need bad info accent dim</c> (rỗng = chữ thường).
/// </summary>
public static class WorkbenchUi
{
    // ==========================================================================
    //  Thẻ "WORKBENCH STATE" (cột trái)
    // ==========================================================================

    /// <summary>
    /// Dữ liệu thẻ "WORKBENCH STATE" theo lựa chọn HIỆN TẠI của các thẻ (không phải theo Cài đặt đã lưu): ngôn ngữ đang tick ở ①, mốc đồng bộ ở
    /// ②, database đặt tên ở ③, số file đếm lần gần nhất. <paramref name="counts"/> = <c>null</c>: chưa đếm (hoặc chưa chọn thư mục) → "—".
    /// Mỗi giá trị kèm loại màu; số phụ đề gốc tô đỏ khi mốc = phụ đề gốc mà thư mục phụ đề gốc còn trống.
    /// </summary>
    public static WorkbenchState State(string? folder, IReadOnlyList<string> langs, bool audioMode, string db, PrepCounts? counts)
    {
        var name = folder is null ? "—" : Path.GetFileName(folder.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : folder;
        var lang = langs.Count > 0 ? string.Join(" · ", langs.Select(x => x.ToUpperInvariant())) : "—";
        var dbText = db switch { "tmdb" => "TMDB", "tvdb" => "TVDB", "none" => "Không đổi tên", _ => "chưa chọn" };
        string Num(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "—";
        var refsKind = counts is { Refs: 0, Videos: > 0 } && !audioMode ? "bad" : "";
        return new WorkbenchState(name, lang, langs.Count > 0 ? "need" : "dim", audioMode ? "Audio" : "Phụ đề gốc", dbText,
                                  db.Length > 0 ? "good" : "need", Num(counts?.Videos), Num(counts?.Subs), Num(counts?.Refs), refsKind);
    }

    // ==========================================================================
    //  ① Tải phụ đề
    // ==========================================================================

    /// <summary>Màu các ô của một dòng ứng viên: FPS (khớp / không khai báo / lệch), độ giống tên, độ lệch (đo thật / ước tính / chưa đo), nguồn.</summary>
    public static Dictionary<string, string> CandidateKinds(Candidate c) => new(StringComparer.Ordinal)
    {
        ["~fps"] = c.Tier switch { 1 => "good", 2 => "dim", _ => "bad" },
        ["~score"] = c.Score >= 0.8 ? "good" : c.Score >= 0.5 ? "info" : "dim",
        ["~off"] = c.Off is null ? "dim" : c.Off.Total > 0 ? "need" : "good",
        ["~src"] = c.Src switch { "SubSource" => "info", "Titulky" => "accent", _ => "need" },
    };

    /// <summary>"Giỏ phụ đề" của bước ①: mỗi ngôn ngữ một nhãn <c>VI: 2/3</c> (xong hoặc đã chọn / tổng), kèm số dòng còn CẦN CHỌN.</summary>
    public static List<(string Text, string Kind)> LangBasket(IReadOnlyList<FetchItem> items, IEnumerable<string> languages)
    {
        var outp = new List<(string, string)>();
        foreach (var lang in languages)
        {
            var its = items.Where(i => i.Lang == lang).ToList();
            if (its.Count == 0)
                continue;
            var done = its.Count(it => it.Result is not null || it.Already is not null || it.Existing is not null || it.Chosen is not null);
            var need = its.Count(it => it.NeedsChoice);
            var text = string.Create(CultureInfo.InvariantCulture, $"{lang.ToUpperInvariant()}: {done}/{its.Count}");
            if (need > 0)
                text += string.Create(CultureInfo.InvariantCulture, $" · {need} cần chọn");
            outp.Add((text, need > 0 ? "need" : done == its.Count ? "good" : done > 0 ? "info" : "dim"));
        }
        return outp;
    }

    /// <summary>Trạng thái từng ngôn ngữ của MỘT phim (thẻ "TARGET VIDEO"): <c>vi: đã chọn</c>, <c>cs: CẦN CHỌN</c>…</summary>
    public static List<(string Text, string Kind)> VideoLangs(IEnumerable<FetchItem> items) =>
        [.. items.Select(it =>
        {
            var (_, tag) = FetchUi.ItemStatus(it);
            var word = it.Result is not null ? "đã tải" : it.Already is not null ? "đã có" : it.Existing is not null ? "file có sẵn"
                : it.Skip ? "bỏ qua" : it.Chosen is not null ? "đã chọn" : it.NeedsChoice ? "CẦN CHỌN" : it.Gathered ? "không tìm thấy" : "chưa tìm";
            return ($"{it.Lang}: {word}", tag switch { "ok" => "info", "" => "dim", _ => tag });
        })];

    // ==========================================================================
    //  ② Chuẩn bị phụ đề
    // ==========================================================================

    /// <summary>Màu các ô của một cặp đồng bộ: sức khoẻ, loại mốc, recap đã cắt.</summary>
    public static Dictionary<string, string> PairKinds(SyncPair p, string tag)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["~kind"] = p.Ref is null ? "" : p.Mode == "sub" ? "info" : "accent",
            ["~health"] = p.Result is null ? "" : tag is "good" or "need" or "bad" ? tag : "dim",
            ["~cut"] = p.Result is { Cut: > 0 } ? "need" : "",
        };
        return d;
    }

    /// <summary>Các nhãn đếm phía trên bảng đồng bộ: trước khi chạy = sẽ đồng bộ / bỏ qua; sau khi chạy = TỐT / CẦN XEM / CÓ VẤN ĐỀ / lỗi.</summary>
    public static List<(string Text, string Kind)> SyncChips(IReadOnlyList<SyncPair> pairs)
    {
        if (pairs.Count == 0)
            return [];
        var done = pairs.Where(p => p.Result is not null).ToList();
        if (done.Count == 0)
        {
            var nOk = pairs.Count(p => p.Ok);
            return [(string.Create(CultureInfo.InvariantCulture, $"{nOk} cặp sẽ đồng bộ"), "info"),
                    (string.Create(CultureInfo.InvariantCulture, $"{pairs.Count - nOk} bỏ qua"), "dim")];
        }
        var lv = done.Where(p => p.Result!.Ok).Select(p => p.Result!.Level).ToList();
        return
        [
            (string.Create(CultureInfo.InvariantCulture, $"{lv.Count(x => x == 0)} {Subprep.LevelText[0]}"), "good"),
            (string.Create(CultureInfo.InvariantCulture, $"{lv.Count(x => x == 1)} {Subprep.LevelText[1]}"), "need"),
            (string.Create(CultureInfo.InvariantCulture, $"{lv.Count(x => x == 2)} {Subprep.LevelText[2]}"), "bad"),
            (string.Create(CultureInfo.InvariantCulture, $"{done.Count - lv.Count} lỗi/từ chối"), done.Count > lv.Count ? "bad" : "dim"),
        ];
    }

    // ==========================================================================
    //  ③ Mux
    // ==========================================================================

    /// <summary>Màu các ô của bảng tổng quan: trạng thái (theo nhãn dòng), thẻ codec, audio ghép.</summary>
    public static Dictionary<string, string> OverviewKinds(MuxPlan plan, string tag) => new(StringComparer.Ordinal)
    {
        ["~status"] = tag switch { "" => "info", "ok" => "info", _ => tag },
        ["~tag"] = plan.CodecTag.Length > 0 ? "accent" : "",
        ["~donor"] = plan.Donor.Tracks.Count > 0 ? "need" : "",
    };

    private static string VideoText(OrderedDictionary<string, object?> t)
    {
        var props = t.GetDict("properties");
        var parts = new List<string>();
        if (t.GetStr("codec") is { Length: > 0 } codec)
            parts.Add(codec);
        if (props.GetStr("pixel_dimensions") is { Length: > 0 } dim)
            parts.Add(dim);
        if (props.Get("default_duration") is { } dd && Py.IsNumber(dd) && Py.AsDouble(dd) > 0)
            parts.Add(Py.Fixed(1e9 / Py.AsDouble(dd), 3) + " fps");
        return string.Join(" · ", parts);
    }

    /// <summary>Các luồng của file đang chọn: video → audio (GIỮ / XOÁ) → phụ đề trong MKV → phụ đề ngoài → audio ghép.</summary>
    public static List<StreamCard> Streams(MuxPlan plan)
    {
        var outp = new List<StreamCard>();
        foreach (var t in Probe.Tracks(plan.Json, "video"))
        {
            var id = Py.ToInt(t.Get("id", 0L));
            var lang = plan.VideoNames.TryGetValue(id, out var nm) ? nm : "";
            outp.Add(new StreamCard("video", id, string.Create(CultureInfo.InvariantCulture, $"VIDEO #{id}"), VideoText(t),
                                    lang.Length > 0 ? lang : "Trong file MKV", "keep", "GIỮ", false, false));
        }
        foreach (var a in plan.Audio)
        {
            var title = $"{a.Label}  {Probe.FormatKbps(a.Bitrate)}".TrimEnd() + (a.TrackName.Length > 0 ? $"  [{a.TrackName}]" : "");
            var detail = (a.BitrateSrc.Length > 0 ? $"bitrate: {a.BitrateSrc}" : "");
            outp.Add(new StreamCard("audio", a.Id, string.Create(CultureInfo.InvariantCulture, $"AUDIO #{a.Id}"), title, detail,
                                    a.Keep ? "keep" : "drop", a.Keep ? "GIỮ" : $"XOÁ ({a.Reason})", !MuxUi.FixedAudio.Contains(a.Reason), a.Default));
        }
        foreach (var s in plan.IntSubs)
        {
            var title = (s.Label.Length > 0 ? s.Label : s.Code) + (s.TrackName.Length > 0 ? $"  [{s.TrackName}]" : "");
            outp.Add(new StreamCard("int", s.Id, string.Create(CultureInfo.InvariantCulture, $"SUB #{s.Id}"), title, s.Reason,
                                    s.Keep ? "keep" : "drop", s.Keep ? "GIỮ" : "XOÁ", false, s.Default));
        }
        var (dk, dv) = plan.DefaultSub;
        for (var i = 0; i < plan.ExtSubs.Count; i++)
        {
            var e = plan.ExtSubs[i];
            outp.Add(new StreamCard("ext", -1, "EXT CẠNH PHIM", Subprep.Name(e.Path), $"Phụ đề ngoài: {e.Label}", "keep", "GHÉP VÀO MKV", false,
                                    dk == "ext" && i == dv));
        }
        foreach (var d in plan.Donor.Tracks)
        {
            outp.Add(new StreamCard("donor", d.Tid, "AUDIO GHÉP", $"{Naming.LangName(d.Code)}  {d.Name}".Trim(),
                                    $"{Subprep.Name(d.Source)} — {plan.Donor.Summary}", "keep", "GHÉP VÀO MKV", false, false));
        }
        return outp;
    }
}
