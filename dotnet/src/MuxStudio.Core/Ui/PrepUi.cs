using System.Globalization;
using System.Text;
using Vals = System.Collections.Generic.OrderedDictionary<string, string>;

namespace MuxStudio.Core.Ui;

/// <summary>Phần thuần của thẻ "② Chuẩn bị phụ đề" (port gui/subprep_view.py).</summary>
public static class PrepUi
{
    public const string AutoLang = "Tự nhận diện (thư mục con vi\\ cs\\ en\\ hoặc hậu tố tên file)";
    public const string OtherLang = "Khác…";
    public const string ModeSub = "sub";
    public const string ModeAudio = "audio";
    public const string InnerRename = "Đổi tên";
    public const string InnerSync = "Đồng bộ";
    public const string ResetText = "↺ Làm lại từ đầu";
    public const string RefMark = "◄ mốc";

    public static readonly IReadOnlyDictionary<int, string> LevelTag = new Dictionary<int, string> { [0] = "good", [1] = "need", [2] = "bad" };

    /// <summary><c>lang_options</c>: các lựa chọn của ô "Ngôn ngữ" (mặc định = tự nhận diện).</summary>
    public static List<string> LangOptions(IEnumerable<string>? extra = null)
    {
        var outp = new List<string> { AutoLang };
        foreach (var c in Naming.FetchLangSwitches.Concat(extra ?? []))
        {
            var n = Naming.LangName(c);
            var lab = n.Length > 0 ? $"{c} — {n}" : c;
            if (!outp.Contains(lab))
                outp.Add(lab);
        }
        outp.Add(OtherLang);
        return outp;
    }

    public static string LangFromOption(string? opt)
    {
        if (string.IsNullOrEmpty(opt) || opt == AutoLang)
            return "auto";
        var i = opt.IndexOf(" — ", StringComparison.Ordinal);
        return (i >= 0 ? opt[..i] : opt).Trim().ToLowerInvariant();
    }

    public static string CountsText(PrepCounts c, Config cfg) => string.Create(CultureInfo.InvariantCulture,
        $"Phim: {c.Videos}   |   Phụ đề trong '{cfg.Str("SubFolderName")}': {c.Subs}   |   Phụ đề gốc trong '{cfg.Str("ExtractFolder")}': {c.Refs}");

    public static string AudioIid(int fi, int index) => string.Create(CultureInfo.InvariantCulture, $"a{fi}_{index}");

    /// <summary>Số thứ tự audio (0:a:N) của một dòng bảng audio.</summary>
    public static int AudioIidIndex(string iid) => Py.Int(iid[(iid.LastIndexOf('_') + 1)..]);

    /// <summary><c>audio_rows</c>: bảng audio của MỌI file phim (TĐ-35) — <c>(iid, giá trị, nhãn màu)</c>. Nhãn <c>need</c> = file có bố cục
    /// audio KHÁC đa số; <c>bad</c> = không đọc được audio. Cột <c>ref</c> đánh dấu track làm mốc theo <paramref name="choice"/>.</summary>
    public static List<(string Id, Vals Values, string Tag)> AudioRows(IReadOnlyList<VideoAudio> files, AudioChoice? choice)
    {
        var groups = Subprep.AudioLayoutGroups(files);
        var main = groups.Count > 0 ? groups[0][0].LayoutKey : "";
        var outp = new List<(string, Vals, string)>();
        for (var fi = 0; fi < files.Count; fi++)
        {
            var f = files[fi];
            if (f.Streams.Count == 0)
            {
                outp.Add((string.Create(CultureInfo.InvariantCulture, $"a{fi}_x"), new Vals(StringComparer.Ordinal)
                {
                    ["ep"] = f.EpLabel, ["file"] = Subprep.Name(f.Video), ["idx"] = "", ["lang"] = "", ["codec"] = "", ["ch"] = "",
                    ["title"] = "(không đọc được audio track)", ["def"] = "", ["ref"] = "",
                }, "bad"));
                continue;
            }
            int? pick = null;
            var miss = false;
            if (choice is not null)
            {
                var (p, _desc, m) = Subprep.PickAudioStream(f.Streams, choice);
                (pick, miss) = (p, m);
            }
            var tag = f.LayoutKey != main ? "need" : "";
            for (var n = 0; n < f.Streams.Count; n++)
            {
                var a = f.Streams[n];
                var mark = pick == a.Index ? RefMark + (miss ? " (Default — không có lựa chọn)" : "") : "";
                outp.Add((AudioIid(fi, a.Index), new Vals(StringComparer.Ordinal)
                {
                    ["ep"] = n == 0 ? f.EpLabel : "", ["file"] = n == 0 ? Subprep.Name(f.Video) : "",
                    ["idx"] = a.Index.ToString(CultureInfo.InvariantCulture), ["lang"] = a.Lang, ["codec"] = a.Codec,
                    ["ch"] = a.Channels != 0 ? a.Channels.ToString(CultureInfo.InvariantCulture) : "", ["title"] = a.Title,
                    ["def"] = a.Default ? "✓" : "", ["ref"] = mark,
                }, tag));
            }
        }
        return outp;
    }

    /// <summary><c>audio_summary</c>: dòng tiêu đề của bảng audio → (chữ, mức màu info/warn/error).</summary>
    public static (string Text, string Level) AudioSummary(IReadOnlyList<VideoAudio> files)
    {
        if (files.Count == 0)
            return ("Không tìm thấy file phim (hoặc thiếu ffprobe) để đọc audio track.", "warn");
        var unread = files.Count(f => f.Streams.Count == 0);
        if (unread == files.Count)
            return (string.Create(CultureInfo.InvariantCulture, $"Audio track của {files.Count} file phim: KHÔNG đọc được file nào (thiếu ffprobe?)."), "error");
        var groups = Subprep.AudioLayoutGroups(files.Where(f => f.Streams.Count > 0));
        var txt = string.Create(CultureInfo.InvariantCulture, $"Audio track của {files.Count} file phim");
        var other = groups.Skip(1).Sum(g => g.Count);
        var level = "info";
        if (files.Count == 1)
        {
            txt += $": {Subprep.Name(files[0].Video)}";
        }
        else if (other == 0)
        {
            txt += " — mọi file cùng bố cục audio.";
        }
        else
        {
            txt += string.Create(CultureInfo.InvariantCulture, $" — {other} file có bố cục audio KHÁC đa số (dòng vàng): kiểm tra track làm mốc của từng file.");
            level = "warn";
        }
        if (unread > 0)
        {
            txt += string.Create(CultureInfo.InvariantCulture, $"  {unread} file không đọc được audio (đỏ).");
            level = "warn";
        }
        return (txt, level);
    }

    public static (Vals Values, string Tag) ExtractValues(ExtractRow r)
    {
        var vals = new Vals(StringComparer.Ordinal)
        {
            ["video"] = Subprep.Name(r.Video), ["track"] = r.Track?.Describe() ?? "", ["dest"] = r.Final is not null ? Subprep.Name(r.Final) : "",
            ["why"] = r.Status.Length > 0 ? r.Status : r.Reason,
        };
        var tag = r.Status == "đã trích" ? "good" : r.Status.Length > 0 ? "bad" : r.Reason.Length > 0 ? "dim" : "";
        return (vals, tag);
    }

    /// <summary><c>rename_values</c>: một dòng bảng xem trước đổi tên.</summary>
    public static (Vals Values, string Tag) RenameValues(RenameRow r)
    {
        var why = r.Status.Length > 0 ? r.Status : r.Reason;
        if (r.Warn.Length > 0)
            why = (why.Length > 0 ? why + " | " : "") + "⚠ " + r.Warn;
        var vals = new Vals(StringComparer.Ordinal)
        {
            ["ep"] = r.EpLabel, ["old"] = Subprep.Name(r.Src), ["new"] = r.NewPath is not null ? Subprep.Name(r.NewPath) : "",
            ["lang"] = r.Lang.Length > 0 ? r.Lang : "?", ["why"] = why,
        };
        string tag;
        if (r.Status == "đã đổi tên")
            tag = "good";
        else if (r.Status.Length > 0)
            tag = "bad";
        else if (r.Warn.Length > 0)
            tag = "bad"; // canh bao DO: file nam trong thu muc ngon ngu khac lua chon
        else if (r.Reason == "đã đúng tên")
            tag = "dim";
        else if (r.Reason.Length > 0)
            tag = "need";
        else
            tag = "";
        return (vals, tag);
    }

    public static string RenameTip(RenameRow r)
    {
        var lines = new List<string> { $"Nguồn: {r.Src}" };
        if (r.NewPath is not null)
            lines.Add($"Tên mới: {Subprep.Name(r.NewPath)}");
        if (r.Ref is not null)
            lines.Add($"Mốc: {Subprep.Name(r.Ref.File)}" + (r.How.Length > 0 ? $" ({r.How})" : ""));
        lines.AddRange(r.Details);
        return string.Join("\n", lines);
    }

    /// <summary><c>_f</c>: <c>f"{x:+.2f}"</c>.</summary>
    public static string F(double? x, int d = 2, bool signed = true) => x is { } v ? Py.Fixed(v, d, plus: signed) : "";

    /// <summary><c>pair_values</c>: một dòng bảng cặp đồng bộ (trước và sau khi chạy).</summary>
    public static (Vals Values, string Tag) PairValues(SyncPair p)
    {
        var vals = new Vals(StringComparer.Ordinal)
        {
            ["sub"] = Subprep.Name(p.Sub), ["ref"] = p.Ref is not null ? Subprep.Name(p.Ref.File) : "",
            ["kind"] = p.Ref is not null ? p.RefKindText : "", ["out"] = p.OutPath is not null ? Subprep.Name(p.OutPath) : "",
            ["forecast"] = string.Join("; ", p.Forecast()), ["health"] = "", ["first"] = "", ["last"] = "", ["spread"] = "",
            ["resid"] = "", ["votes"] = "", ["cut"] = "", ["flags"] = p.Reason,
        };
        var res = p.Result;
        var tag = p.Reason.Length > 0 ? "dim" : "";
        if (p.Relation is { FpsMismatch: true } && p.Reason.Length == 0)
            tag = "need";
        if (res is not null)
        {
            var h = res.Health;
            if (res.Ok && h is not null)
            {
                vals["health"] = h.Text;
                vals["first"] = F(h.ShiftFirst);
                vals["last"] = F(h.ShiftLast);
                vals["spread"] = F(h.Spread);
                if (h.ResVotes >= 0 && h.ResOffset is not null)
                {
                    vals["resid"] = F(h.ResOffset);
                    vals["votes"] = h.ResVotes.ToString(CultureInfo.InvariantCulture);
                }
                vals["cut"] = res.Cut != 0 ? res.Cut.ToString(CultureInfo.InvariantCulture) : "";
                var flags = new List<string>(h.Flags);
                if (res.Fixed != 0)
                    flags.Insert(0, string.Create(CultureInfo.InvariantCulture, $"bỏ {res.Fixed} dòng dồn về 00:00:00"));
                vals["flags"] = string.Join("; ", flags.Concat(res.Notes));
                tag = LevelTag.GetValueOrDefault(h.Level, "");
            }
            else
            {
                vals["health"] = res.Rejected ? "TỪ CHỐI" : "LỖI";
                vals["flags"] = res.Notes.Count > 0 ? string.Join("; ", res.Notes) : p.Status;
                tag = "bad";
            }
        }
        else if (p.Status.Length > 0)
        {
            vals["flags"] = p.Status;
        }
        return (vals, tag);
    }

    /// <summary>Lọc "chỉ hiện file cần xem": đã chạy mà CẦN XEM / CÓ VẤN ĐỀ / lỗi / từ chối.</summary>
    public static bool PairNeedsReview(SyncPair p) => p.Result is { } r && (!r.Ok || (r.Level ?? 0) >= 1);

    public static string PairTip(SyncPair p)
    {
        var lines = new List<string> { $"Phụ đề: {p.Sub}" };
        if (p.Ref is not null)
            lines.Add($"Mốc ({p.RefKindText}): {p.Ref.File}" + (p.AudioDesc.Length > 0 ? $" — {p.AudioDesc}" : ""));
        if (p.OutPath is not null)
            lines.Add($"Kết quả: {p.OutPath}");
        lines.AddRange(p.Forecast());
        lines.AddRange(p.Notes);
        lines.AddRange(p.Details);
        if (p.Result is not null)
        {
            lines.AddRange(p.Result.Notes.Select(n => $"({n})"));
            lines.AddRange(p.Result.OutputTail.TakeLast(4));
        }
        return string.Join("\n", lines);
    }

    public static readonly IReadOnlyList<(string Key, string Title)> ExportCols =
    [
        ("sub", "Phụ đề"), ("ref", "Mốc"), ("kind", "Loại mốc"), ("out", "Kết quả"), ("forecast", "Dự báo"), ("health", "Sức khoẻ"),
        ("first", "Dịch đầu (s)"), ("last", "Dịch cuối (s)"), ("spread", "Chênh (s)"), ("resid", "Còn lệch (s)"), ("votes", "Mốc khớp"),
        ("cut", "Cắt recap"), ("flags", "Cờ / lý do"),
    ];

    /// <summary><c>export_csv</c>: nút "Xuất CSV" (UTF-8 có BOM để Excel đọc đúng dấu). Chữ được dịch theo ngôn ngữ giao diện.</summary>
    public static int ExportCsv(IReadOnlyList<SyncPair> pairs, string path)
    {
        var sb = new StringBuilder();
        PyCsv.WriteRowQuoteAll(sb, ExportCols.Select(c => I18n.T(c.Title)));
        foreach (var p in pairs)
        {
            var (vals, _) = PairValues(p);
            PyCsv.WriteRowQuoteAll(sb, ExportCols.Select(c => c.Key is "sub" or "ref" or "out" ? vals.GetValueOrDefault(c.Key, "")
                                                                                              : I18n.T(vals.GetValueOrDefault(c.Key, ""))));
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return pairs.Count;
    }

    public static List<string> CleanupLines(CleanupPlan plan, string which, string root) =>
        [.. plan.Targets(which).Select(p => root.Length > 0 && Tools.IsUnder(p, root) ? Path.GetRelativePath(root, p) : p)];
}
