using System.Globalization;
using System.Text.RegularExpressions;
using Vals = System.Collections.Generic.OrderedDictionary<string, string>;

namespace MuxStudio.Core.Ui;

/// <summary>Dữ liệu cho bảng chọn phụ đề theo tập: dòng = file, cột = loại phụ đề (sub ngoài trước, sub trong MKV sau).</summary>
public sealed record SubGridData(
    List<string> Rows,
    List<string> Cols,
    Dictionary<string, string> Labels,
    Dictionary<string, string> Headers,
    Dictionary<(string R, string C), bool> Auto,
    Dictionary<(string R, string C), bool> Current,
    Dictionary<(string R, string C), string> Tips);

/// <summary>Phần thuần của thẻ "③ Mux" (port gui/mux_view.py).</summary>
public static partial class MuxUi
{
    public const string ResetText = "↺ Làm lại từ đầu";

    public static readonly IReadOnlyList<(string Key, string Label)> Dbs = [("tmdb", "TMDB"), ("tvdb", "TVDB"), ("none", "Không đổi tên")];

    public static readonly IReadOnlyList<string> FixedAudio = ["Commentary", "Audio Description"];

    public static string KeyOf(MuxPlan p) => Py.NormCase(Py.AbsPath(p.Src));

    public static string OutRel(MuxPlan plan, string outDir)
    {
        if (plan.OutPath is null)
            return "";
        var a = Path.GetPathRoot(Py.AbsPath(plan.OutPath));
        var b = Path.GetPathRoot(Py.AbsPath(outDir));
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(outDir, plan.OutPath) : plan.OutPath; // khac o dia
    }

    private static bool HasJson(MuxPlan p) => p.Json is { Count: > 0 };

    /// <summary><c>overview_values</c>: một dòng bảng tổng quan + nhãn màu.</summary>
    public static (Vals Values, string Tag) OverviewValues(MuxPlan plan, string outDir)
    {
        var nA = plan.Audio.Count;
        var kA = plan.KeepAudio.Count;
        var audio = nA > 0 ? string.Create(CultureInfo.InvariantCulture, $"{kA}/{nA}") : "0";
        if (plan.Donor.Tracks.Count > 0)
            audio += string.Create(CultureInfo.InvariantCulture, $" + {plan.Donor.Tracks.Count} ghép");
        var subs = plan.IntSubs.Count > 0 || plan.ExtSubs.Count > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{plan.KeepSubs.Count} trong + {plan.ExtSubs.Count} ngoài")
            : "0";
        var st = plan.StatusText;
        if (plan.Reasons.Count > 0)
            st += ": " + string.Join("; ", plan.Reasons);
        var tag = plan.Status switch { "need_choice" => "need", "skip" => "dim", "exists" => "dim", "fail" => "bad", _ => "" };
        if (plan.Result is { } r)
        {
            if (r.Ok)
            {
                st = r.Warning ? "ĐÃ MUX (có cảnh báo)" : "ĐÃ MUX";
                tag = r.Warning ? "need" : "good";
            }
            else
            {
                st = string.Create(CultureInfo.InvariantCulture, $"THẤT BẠI (mã thoát {r.Code})");
                tag = "bad";
            }
        }
        var vals = new Vals(StringComparer.Ordinal)
        {
            ["src"] = plan.Rel, ["new"] = plan.Status != "fail" ? OutRel(plan, outDir) : "",
            ["audio"] = plan.Status is not ("fail" or "exists") ? audio : "", ["subs"] = HasJson(plan) ? subs : "",
            ["donor"] = HasJson(plan) ? plan.Donor.Summary : "", ["tag"] = plan.CodecTag, ["status"] = st,
        };
        return (vals, tag);
    }

    /// <summary><c>audio_rows</c>: bảng audio của một file: (iid, giá trị, ô tick "Xoá" — <c>null</c> = không cho đổi, nhãn màu).</summary>
    public static List<(string Id, Vals Values, bool? Check, string Tag)> AudioRows(MuxPlan plan)
    {
        var outp = new List<(string, Vals, bool?, string)>();
        foreach (var a in plan.Audio)
        {
            var isFixed = FixedAudio.Contains(a.Reason);
            var note = a.Reason.Length > 0 ? a.Reason : "giữ";
            if (a.Default)
                note += " — Default";
            var lname = Naming.LangName(plan.LangOverride.TryGetValue(a.Id, out var lo) ? lo : a.Code);
            var vals = new Vals(StringComparer.Ordinal)
            {
                ["id"] = a.Id.ToString(CultureInfo.InvariantCulture), ["lang"] = lname.Length > 0 ? lname : a.Code, ["codec"] = a.Codec,
                ["ch"] = a.Channels, ["br"] = Probe.FormatKbps(a.Bitrate), ["brsrc"] = a.BitrateSrc, ["name"] = a.TrackName, ["note"] = note,
            };
            var tag = !a.Keep ? "dim" : a.Default ? "ok" : "";
            outp.Add((string.Create(CultureInfo.InvariantCulture, $"a{a.Id}"), vals, isFixed ? null : !a.Keep, tag));
        }
        return outp;
    }

    public static bool MultiEn(MuxPlan plan) => plan.Reasons.Any(r => r.Contains("audio tiếng Anh", StringComparison.Ordinal));

    /// <summary><c>grid_data</c>: dữ liệu cho bảng chọn phụ đề theo tập.</summary>
    public static SubGridData GridData(IReadOnlyList<MuxPlan> plans, IReadOnlyDictionary<string, SubChoice>? choices = null)
    {
        var rows = new List<string>();
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        var auto = new Dictionary<(string, string), bool>();
        var current = new Dictionary<(string, string), bool>();
        var tips = new Dictionary<(string, string), string>();
        var colOrder = new Dictionary<string, (int, int, int)>(StringComparer.Ordinal);
        var n = 0;
        for (var i = 0; i < plans.Count; i++)
        {
            var p = plans[i];
            if (p.SubItems.Count == 0)
                continue;
            var r = string.Create(CultureInfo.InvariantCulture, $"m{i}");
            rows.Add(r);
            labels[r] = p.Rel;
            var ch = choices?.GetValueOrDefault(KeyOf(p));
            foreach (var it in p.SubItems)
            {
                headers.TryAdd(it.Key, it.Header);
                if (!colOrder.ContainsKey(it.Key))
                    colOrder[it.Key] = (it.Kind == "ext" ? 0 : 1, it.Order, n++);
                auto[(r, it.Key)] = it.AutoKeep;
                tips[(r, it.Key)] = it.Tip;
                if (ch is not null)
                    current[(r, it.Key)] = it.Kind == "ext" ? ch.Ext.Contains(it.Code) : ch.IntIds.Contains(it.Id);
            }
        }
        var cols = colOrder.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();
        return new SubGridData(rows, cols, labels, headers, auto, current, tips);
    }

    /// <summary><c>grid_choice</c>: ô đã tick của một dòng → (mã sub ngoài giữ, ID sub trong giữ) cho <c>SetSubChoice</c>.</summary>
    public static (List<string> Ext, List<int> Ints) GridChoice(MuxPlan plan, IReadOnlyDictionary<string, bool> values)
    {
        var ext = new List<string>();
        var ints = new List<int>();
        foreach (var it in plan.SubItems)
        {
            if (values.TryGetValue(it.Key, out var v) && v)
            {
                if (it.Kind == "ext")
                    ext.Add(it.Code);
                else
                    ints.Add(it.Id);
            }
        }
        return (ext, ints);
    }

    public static Vals DonorValues(MuxPlan plan)
    {
        var d = plan.Donor;
        var t = d.Tracks.Count > 0 ? d.Tracks[0] : null;
        return new Vals(StringComparer.Ordinal)
        {
            ["src"] = plan.Rel, ["donor"] = d.Donor is not null ? Subprep.Name(d.Donor) : "", ["langs"] = string.Join(", ", d.Tracks.Select(x => x.Code)),
            ["off"] = t is not null ? (t.OffsetMs >= 0 ? "+" : "") + t.OffsetMs.ToString(CultureInfo.InvariantCulture) + (d.Manual ? " (tay)" : "") : "",
            ["how"] = d.Timing?.How ?? "",
            ["fps"] = t is not null && t.Stretch ? "CÓ → đổi tốc độ (encode lại)" : t is not null ? "không" : "",
            ["note"] = d.Tracks.Count == 0 ? d.Note : "",
        };
    }

    [GeneratedRegex(@"^[-+]?\d+(\.\d+)?\z")]
    private static partial Regex MsRe();

    [GeneratedRegex(@"^(19|20)\d\d\z")]
    private static partial Regex YearRe();

    /// <summary><c>parse_ms</c>: ô sửa độ lệch tay: trống = dùng kết quả đo; <c>+1200</c> / <c>-500</c> / <c>1,5</c> (ms).</summary>
    public static double? ParseMs(string? text)
    {
        var t = (text ?? "").Trim().Replace(" ", "", StringComparison.Ordinal).Replace(',', '.').ToLowerInvariant();
        if (t.EndsWith("ms", StringComparison.Ordinal))
            t = t[..^2];
        if (t.Length == 0)
            return null;
        if (!MsRe().IsMatch(t))
            throw new FormatException("cần một số mili-giây, vd 1200 hoặc -500");
        return Timing.TryParsePyFloat(t, out var v) ? v : throw new FormatException("cần một số mili-giây, vd 1200 hoặc -500");
    }

    public static int? ParseYear(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0)
            return null;
        if (!YearRe().IsMatch(t))
            throw new FormatException("năm cần 4 chữ số, vd 2019");
        return Py.Int(t);
    }

    /// <summary><c>plan_details</c>: ô "Chi tiết" của file đang chọn.</summary>
    public static List<string> PlanDetails(MuxPlan plan, string outDir)
    {
        var L = new List<string> { $"File: {plan.Rel}", $"Trạng thái: {plan.StatusText}" };
        if (plan.OutPath is not null)
            L.Add($"Đầu ra: Output\\{OutRel(plan, outDir)}");
        if (plan.Name.Note.Length > 0)
            L.Add($"Đặt tên: {plan.Name.Note}");
        if (plan.Name.EpisodeNote.Length > 0)
            L.Add($"   {plan.Name.EpisodeNote}");
        L.AddRange(plan.Reasons.Select(r => $"[!] {r}"));
        L.AddRange(plan.Notes.Select(n => $"[i] {n}"));
        if (plan.Audio.Count > 0)
        {
            L.Add("Audio:");
            foreach (var a in plan.Audio)
            {
                var st = a.Keep ? "GIỮ" + (a.Default ? " (Default)" : "") : $"XOÁ ({a.Reason})";
                L.Add($"   #{a.Id.ToString(CultureInfo.InvariantCulture)} {a.Label} {Probe.FormatKbps(a.Bitrate)}"
                      + (a.TrackName.Length > 0 ? $" [{a.TrackName}]" : "") + $" — {st}");
            }
        }
        if (plan.RuleApplied.Length > 0)
            L.Add($"   (quy tắc đã nhớ: {plan.RuleApplied})");
        if (plan.IntSubs.Count > 0)
        {
            L.Add("Phụ đề trong MKV:");
            foreach (var s in plan.IntSubs)
            {
                var st = s.Keep ? "GIỮ" + (s.Default ? " (Default)" : "") : "XOÁ";
                L.Add($"   #{s.Id.ToString(CultureInfo.InvariantCulture)} {(s.Label.Length > 0 ? s.Label : s.Code)} — {st} ({s.Reason})");
            }
        }
        if (plan.ExtSubs.Count > 0)
        {
            L.Add("Phụ đề ngoài:");
            var (kind, val) = plan.DefaultSub;
            for (var i = 0; i < plan.ExtSubs.Count; i++)
            {
                var e = plan.ExtSubs[i];
                L.Add($"   {Subprep.Name(e.Path)} ({e.Label})" + (kind == "ext" && i == val ? " — Default" : ""));
            }
        }
        if (plan.Donor.Tracks.Count > 0 || plan.Donor.Note.Length > 0)
        {
            L.Add($"Audio ghép: {plan.Donor.Summary}");
            if (plan.Donor.Donor is not null)
                L.Add($"   từ: {plan.Donor.Donor}");
            if (plan.Donor.Timing is not null)
                L.Add($"   đo: {plan.Donor.Timing.How}");
        }
        if (plan.CodecTag.Length > 0)
            L.Add($"Thẻ codec: {plan.CodecTag}");
        if (plan.Result is not null)
        {
            L.Add(string.Create(CultureInfo.InvariantCulture, $"Kết quả mux: mã thoát {plan.Result.Code}"));
            L.AddRange(plan.Result.Output.TakeLast(8).Select(x => "   " + x));
        }
        return L;
    }

    public static string SummaryText(MuxReport rep)
    {
        var s = string.Create(CultureInfo.InvariantCulture, $"Thành công: {rep.Ok}")
                + (rep.Warning > 0 ? string.Create(CultureInfo.InvariantCulture, $" ({rep.Warning} có cảnh báo)") : "")
                + string.Create(CultureInfo.InvariantCulture, $"  |  Bỏ qua: {rep.Skipped}  |  Cần chọn: {rep.Pending}  |  Lỗi: {rep.Failed}");
        if (rep.Stopped)
            s += "  |  (đã dừng giữa chừng)";
        return s;
    }

    /// <summary><c>size_text</c>: <c>1536</c> → <c>1,5 KB</c>; <c>734003200</c> → <c>700 MB</c>.</summary>
    public static string SizeText(long n)
    {
        foreach (var (unit, k) in new[] { ("GB", 1L << 30), ("MB", 1L << 20), ("KB", 1L << 10) })
        {
            if (n >= k)
            {
                var v = (double)n / k;
                return (v < 10 ? Py.Fixed(v, 1).Replace('.', ',') : Py.Fixed(v, 0)) + " " + unit;
            }
        }
        return n.ToString(CultureInfo.InvariantCulture) + " B";
    }

    /// <summary><c>trash_values</c>: một dòng của hộp thoại "Dọn file nguồn đã dùng".</summary>
    public static Vals TrashValues(UsedFile u, string? root)
    {
        string size;
        try
        {
            size = SizeText(new FileInfo(u.Path).Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            size = "?";
        }
        var rel = !string.IsNullOrEmpty(root) && Tools.IsUnder(u.Path, root) ? Path.GetRelativePath(root, u.Path) : u.Path;
        return new Vals(StringComparer.Ordinal) { ["kind"] = u.KindText, ["file"] = rel, ["size"] = size, ["used"] = string.Join(", ", u.UsedFor) };
    }
}
