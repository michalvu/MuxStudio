using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MuxStudio.Core.Ui;

// ==========================================================================
//  Phần THUẦN của giao diện (không phụ thuộc WPF) — port các hàm "PHẦN THUẦN" của gui/widgets.py, gui/app.py,
//  gui/log_panel.py. Giao diện WPF (MuxStudio.App) chỉ gọi các hàm này; test chạy không cần màn hình.
// ==========================================================================

/// <summary>Phiên bản + tên cửa sổ (<c>APP_TITLE</c>).</summary>
public static class AppInfo
{
    public static string Version { get; } = typeof(Config).Assembly.GetName().Version is { } v
        ? string.Create(CultureInfo.InvariantCulture, $"{v.Major}.{v.Minor}.{v.Build}")
        : "1.3.3";

    public static string Title => $"MuxStudio {Version}";

    /// <summary><c>os_name</c>: <c>Windows 11 (10.0.22631)</c>.</summary>
    public static string OsName()
    {
        var v = Environment.OSVersion.Version;
        var ver = string.Create(CultureInfo.InvariantCulture, $"{v.Major}.{v.Minor}.{v.Build}");
        if (OperatingSystem.IsWindows())
            return $"Windows {(v.Major == 10 && v.Build >= 22000 ? "11" : v.Major.ToString(CultureInfo.InvariantCulture))} ({ver})";
        return Environment.OSVersion.ToString();
    }

    /// <summary><c>build_info</c>: một dòng — phiên bản MuxStudio, bản .exe hay mã nguồn, .NET, hệ điều hành, thư viện giao diện.</summary>
    public static string BuildInfo(bool frozen, string uiLibrary)
    {
        var parts = new List<string>
        {
            $"{Title} ({(frozen ? "bản .exe" : "chạy từ mã nguồn")})",
            $".NET {Environment.Version.ToString(3)}",
            OsName(),
        };
        if (uiLibrary.Length > 0)
            parts.Add(uiLibrary);
        return string.Join(" | ", parts);
    }
}

/// <summary>Rút gọn chữ cho vừa ô (port <c>ellipsize_middle</c>, <c>fit_text</c>, <c>shorten_name</c>).</summary>
public static partial class UiText
{
    public const string Ell = "…";

    /// <summary><c>ellipsize_middle</c> (<c>Limit-Text</c>): cắt cho vừa ô, giữ phần ĐUÔI (tên nhóm release nằm ở cuối).</summary>
    public static string EllipsizeMiddle(string? text, int n)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        if (text.Length <= n)
            return text;
        if (n < 12)
            return text[..n];
        var head = (int)Math.Round((n - 3) * 0.55); // [int] cua PowerShell = lam tron ve so chan, nhu round()
        var tail = n - 3 - head;
        return text[..head] + "..." + text[(text.Length - tail)..];
    }

    [GeneratedRegex(@"S\d{1,2}\s?E\d{1,4}(?:-?E\d{1,4})?|(?<!\d)\d{1,2}x\d{2}(?!\d)|\s-\s(?:E|EP)?\d{1,4}(?:v\d)?(?=\s|$|\.|\[|\()|\b(?:Episode|Ep|Tap)\s?\d{1,4}\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpRe();

    [GeneratedRegex(@"(?:\.[a-z]{2,3})?\.(?:mkv|mp4|m4v|avi|ts|mov|mka|srt|ass|ssa|vtt|sup)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExtRe();

    private static string FitEnd(string text, double width, Func<string, double> measure)
    {
        if (measure(text) <= width)
            return text;
        if (measure(Ell) > width)
            return "";
        int lo = 0, hi = text.Length - 1;
        var best = Ell;
        while (lo <= hi)
        {
            var n = (lo + hi) / 2;
            var cand = text[..n].TrimEnd() + Ell;
            if (measure(cand) <= width)
            {
                best = cand;
                lo = n + 1;
            }
            else
            {
                hi = n - 1;
            }
        }
        return best;
    }

    private static string FitMiddle(string text, double width, Func<string, double> measure, double headRatio = 0.55)
    {
        if (measure(text) <= width)
            return text;
        if (measure(Ell) > width)
            return "";
        int lo = 0, hi = text.Length - 1;
        var best = Ell;
        while (lo <= hi)
        {
            var n = (lo + hi) / 2;
            var h = (int)Math.Ceiling(n * headRatio);
            var t = n - h;
            var cand = text[..h] + Ell + (t > 0 ? text[(text.Length - t)..] : "");
            if (measure(cand) <= width)
            {
                best = cand;
                lo = n + 1;
            }
            else
            {
                hi = n - 1;
            }
        }
        return best;
    }

    /// <summary><c>fit_text</c>: <c>end</c> = cắt đuôi + "…"; <c>name</c> = tên file, GIỮ số tập và phần đuôi, bỏ phần giữa.</summary>
    public static string FitText(string? text, double width, Func<string, double>? measure = null, string mode = "end")
    {
        measure ??= s => s.Length;
        var s = text ?? "";
        if (s.Length == 0 || measure(s) <= width)
            return s;
        if (mode != "name")
            return FitEnd(s, width, measure);
        var ext = ExtRe().Match(s);
        if (ext.Success && measure(ext.Value) * 4 <= width) // giu nguyen duoi file (.mkv, .vi.srt) neu con cho
            return FitName(s[..ext.Index], width - measure(ext.Value), measure) + ext.Value;
        return FitName(s, width, measure);
    }

    private static string FitName(string s, double width, Func<string, double> measure)
    {
        if (measure(s) <= width)
            return s;
        var m = EpRe().Match(s);
        if (m.Success)
        {
            var end = m.Index + m.Length;
            string head = s[..end], rest = s[end..];
            if (measure(head + Ell) <= width)
                return head + FitMiddle(rest, width - measure(head), measure, 0.4);
            var tok = m.Value;
            if (measure(Ell + tok + Ell) <= width) // ten series qua dai: rut gon ten, van giu so tap
            {
                var pre = FitEnd(s[..m.Index], (width - measure(tok)) * (rest.Length > 0 ? 0.6 : 1.0), measure);
                var left = width - measure(pre) - measure(tok);
                var tail = rest.Length > 0 && left >= measure(Ell) ? FitMiddle(rest, left, measure, 0.4) : "";
                return pre + tok + tail;
            }
        }
        return FitMiddle(s, width, measure);
    }

    /// <summary><c>shorten_name</c>: tên file rút gọn còn tối đa <paramref name="n"/> ký tự, giữ số tập + đuôi.</summary>
    public static string ShortenName(string? text, int n) => FitText(text ?? "", n, null, "name");

    /// <summary><c>progress_percent</c>: 0–100 (làm tròn xuống: chỉ ghi 100% khi thật sự xong hết).</summary>
    public static int ProgressPercent(int done, int total) => total <= 0 ? 0 : Math.Max(0, Math.Min(100, done * 100 / total));

    /// <summary><c>progress_text</c>: <c>37% · 3/8</c>; chưa biết tổng → <c>0%</c>.</summary>
    public static string ProgressText(int done, int total) =>
        total <= 0 ? "0%" : string.Create(CultureInfo.InvariantCulture, $"{ProgressPercent(done, total)}% · {done}/{total}");
}

/// <summary>Thanh các bước của một thẻ (port <c>step_states</c>, <c>visible_step_states</c>, <c>step_label</c>).</summary>
public static class Steps
{
    /// <summary><c>done</c> (✓) | <c>next</c> (nên bấm tiếp) | <c>todo</c> | <c>off</c> (chưa bấm được). Bước kế tiếp = bước CHƯA xong đầu
    /// tiên, chỉ khi bấm được.</summary>
    public static List<string> States(IReadOnlyList<bool> done, IReadOnlyList<bool> enabled)
    {
        var first = -1;
        for (var i = 0; i < done.Count; i++)
        {
            if (!done[i])
            {
                first = i;
                break;
            }
        }
        var nxt = first >= 0 && first < enabled.Count && enabled[first] ? first : -1;
        var outp = new List<string>();
        for (var i = 0; i < Math.Min(done.Count, enabled.Count); i++)
            outp.Add(i == nxt ? "next" : done[i] ? "done" : enabled[i] ? "todo" : "off");
        return outp;
    }

    /// <summary>Như <see cref="States"/> nhưng bỏ qua bước đang ẩn (trạng thái <c>hidden</c>).</summary>
    public static List<string> VisibleStates(IReadOnlyList<string> keys, IEnumerable<string> hidden, IReadOnlyList<bool> done,
                                             IReadOnlyList<bool> enabled)
    {
        var hid = hidden.ToHashSet(StringComparer.Ordinal);
        var vis = Enumerable.Range(0, keys.Count).Where(i => !hid.Contains(keys[i])).ToList();
        var sts = States([.. vis.Select(i => done[i])], [.. vis.Select(i => enabled[i])]);
        var outp = Enumerable.Repeat("hidden", keys.Count).ToList();
        for (var j = 0; j < vis.Count && j < sts.Count; j++)
            outp[vis[j]] = sts[j];
        return outp;
    }

    /// <summary><c>1. Quét thư mục</c>; đã xong: <c>✓ 1. …</c>; bước kế tiếp: <c>▶ 1. …</c>.</summary>
    public static string Label(int i, string title, string state, bool numbered = true)
    {
        var b = numbered ? string.Create(CultureInfo.InvariantCulture, $"{i + 1}. {title}") : title;
        return (state switch { "done" => "✓ ", "next" => "▶ ", _ => "" }) + b;
    }
}

/// <summary>
/// Logic của bảng chọn phụ đề theo tập (port <c>GridModel</c> — <c>Show-SubPickerGrid</c>). <c>Cells[(r, c)]</c>: true/false = ô có,
/// đang tick / không tick; không có khoá = tập đó KHÔNG có loại phụ đề này (ô xám). <c>Auto</c> = quyết định của quy tắc tự động.
/// </summary>
public sealed class GridModel
{
    public GridModel(IEnumerable<string> rows, IEnumerable<string> cols, IReadOnlyDictionary<(string R, string C), bool> auto,
                     IReadOnlyDictionary<(string R, string C), bool>? current = null)
    {
        Rows = [.. rows];
        Cols = [.. cols];
        Auto = new Dictionary<(string, string), bool>(auto);
        Cells = new Dictionary<(string, string), bool>(auto);
        if (current is not null)
        {
            foreach (var (k, v) in current)
            {
                if (Cells.ContainsKey(k))
                    Cells[k] = v;
            }
        }
    }

    public List<string> Rows { get; }
    public List<string> Cols { get; }
    public Dictionary<(string R, string C), bool> Auto { get; }
    public Dictionary<(string R, string C), bool> Cells { get; private set; }

    public bool Present(string r, string c) => Cells.ContainsKey((r, c));

    public bool? Get(string r, string c) => Cells.TryGetValue((r, c), out var v) ? v : null;

    public bool Differs(string r, string c) => Cells.TryGetValue((r, c), out var v) && v != Auto.GetValueOrDefault((r, c));

    public bool ToggleCell(string r, string c)
    {
        if (!Cells.TryGetValue((r, c), out var v))
            return false;
        Cells[(r, c)] = !v;
        return true;
    }

    private bool ToggleMany(IEnumerable<(string, string)> keys)
    {
        var ks = keys.Where(Cells.ContainsKey).ToList();
        if (ks.Count == 0)
            return false;
        var nv = !ks.All(k => Cells[k]); // con o chua tick -> tick het; da tick het -> bo het
        foreach (var k in ks)
            Cells[k] = nv;
        return true;
    }

    /// <summary>Bấm tiêu đề cột → tick/bỏ cả cột.</summary>
    public bool ToggleCol(string c) => ToggleMany(Rows.Select(r => (r, c)));

    /// <summary>Bấm tên tập → tick/bỏ cả dòng.</summary>
    public bool ToggleRow(string r) => ToggleMany(Cols.Select(c => (r, c)));

    public void SetAll(bool state)
    {
        foreach (var k in Cells.Keys.ToList())
            Cells[k] = state;
    }

    /// <summary>Nút "Trả về mặc định".</summary>
    public void Reset() => Cells = new Dictionary<(string, string), bool>(Auto);

    public OrderedDictionary<string, bool> RowValues(string r)
    {
        var d = new OrderedDictionary<string, bool>(StringComparer.Ordinal);
        foreach (var c in Cols)
        {
            if (Cells.TryGetValue((r, c), out var v))
                d[c] = v;
        }
        return d;
    }

    public bool RowDiffers(string r) => Cols.Any(c => Differs(r, c));

    public List<string> ChangedRows() => [.. Rows.Where(RowDiffers)];
}

/// <summary>Kích thước cửa sổ chính theo màn hình (đơn vị LOGIC) — port <c>WindowPlan</c> / <c>plan_window</c>.</summary>
public sealed record WindowPlan(int Width, int Height, int MinWidth, int MinHeight, bool Zoomed, string Density);

public static partial class WindowLayout
{
    public static string DensityFor(double screenH) => screenH < 900 ? "compact" : screenH >= 1250 ? "large" : "normal";

    [GeneratedRegex(@"^\s*(\d+)x(\d+)(?:[+-]\d+[+-]\d+)?\s*\z")]
    private static partial Regex SavedRe();

    /// <summary>Cửa sổ vừa màn hình: 720p (và 1080p đặt phóng to 150%) → phóng to hết màn hình; 1080p / 1440p → ~86% màn hình, tối đa
    /// 1900×1250. Kích thước lần trước (<c>RỘNGxCAO</c>) chỉ dùng lại nếu còn vừa màn hình.</summary>
    public static WindowPlan PlanWindow(double screenW, double screenH, string saved = "", bool zoomed = false)
    {
        int sw = Math.Max(640, (int)screenW), sh = Math.Max(480, (int)screenH);
        int availW = sw - 16, availH = sh - 72; // chua thanh tac vu + thanh tieu de
        var dens = DensityFor(sh);
        int minW = Math.Min(960, availW), minH = Math.Min(560, availH);
        var w = (int)Math.Min(Math.Min(Math.Max(1060, sw * 0.86), 1900), availW);
        var h = (int)Math.Min(Math.Min(Math.Max(680, sh * 0.86), 1250), availH);
        var small = sw < 1400 || sh < 860;
        var mt = SavedRe().Match(saved ?? "");
        if (mt.Success)
        {
            int sw2 = Py.Int(mt.Groups[1].Value), sh2 = Py.Int(mt.Groups[2].Value);
            if (minW <= sw2 && sw2 <= availW && minH <= sh2 && sh2 <= availH)
                (w, h) = (sw2, sh2);
        }
        if (small)
            (w, h) = (availW, availH);
        return new WindowPlan(w, h, minW, minH, zoomed || small, dens);
    }

    /// <summary><c>ui_factor</c>: ô "Cỡ chữ và giao diện": <c>auto</c> → 1,1 trên màn hình lớn (1440p / 4K đặt 100%), còn lại 1,0; hoặc %.</summary>
    public static double UiFactor(string? choice, double screenW, double screenH)
    {
        var c = (choice ?? "auto").Trim().ToLowerInvariant().TrimEnd('%');
        if (c is "auto" or "")
            return screenW >= 2400 && screenH >= 1300 ? 1.1 : 1.0;
        return int.TryParse(c, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Max(0.75, Math.Min(2.0, n / 100.0)) : 1.0;
    }
}

/// <summary>Khung log (port phần thuần của <c>gui/log_panel.py</c>).</summary>
public static class LogUi
{
    public static readonly IReadOnlyList<string> Levels = ["info", "ok", "warn", "error", "dim", "head"];

    /// <summary>Dòng giữ trong khung (file "Lưu log" giữ đủ).</summary>
    public const int MaxLines = 20000;

    /// <summary><c>format_log</c>: nội dung file "Lưu log": <c>HH:MM:SS  [mức]  chữ</c> (mức <c>info</c> không ghi nhãn).</summary>
    public static string FormatLog(IEnumerable<(string Time, string Level, string Message)> lines)
    {
        var sb = new StringBuilder();
        var any = false;
        foreach (var (t, lv, msg) in lines)
        {
            if (any)
                sb.Append('\n');
            any = true;
            var tag = lv switch
            {
                "ok" => "[OK]   ",
                "warn" => "[!]    ",
                "error" => "[LỖI]  ",
                "dim" => "       ",
                _ => "",
            };
            sb.Append(t).Append("  ").Append(tag).Append(msg);
        }
        if (any)
            sb.Append('\n');
        return sb.ToString();
    }

    public const int MinHeight = 40;

    /// <summary><c>drag_height</c>: kéo LÊN (<paramref name="dy"/> âm) → cao hơn; giữ trong [min, max].</summary>
    public static int DragHeight(int startH, double dy, int maxH, int minH = MinHeight) =>
        (int)Math.Round(Math.Max(minH, Math.Min(Math.Max(minH, maxH), startH - dy)));
}

// ==========================================================================
//  HOÀN TÁC — phần thuần (gui/app.py)
// ==========================================================================

/// <summary>Một lần chạy trong màn hình Hoàn tác (port <c>UndoEntry</c>).</summary>
public sealed record UndoEntry(string Step, RunInfo Run, string Stamp)
{
    public string Label => UndoUi.StepLabel.GetValueOrDefault(Step, Step);

    public string When => Stamp.Length >= 15
        ? $"{Stamp[6..8]}/{Stamp[4..6]}/{Stamp[..4]} {Stamp[9..11]}:{Stamp[11..13]}:{Stamp[13..15]}"
        : Run.Name;
}

public static partial class UndoUi
{
    public static readonly IReadOnlyDictionary<string, string> StepLabel = new OrderedDictionary<string, string>(StringComparer.Ordinal)
    {
        ["subfetch"] = "① Tải phụ đề", ["subprep"] = "② Chuẩn bị phụ đề", ["mux"] = "③ Mux",
    };

    public static readonly IReadOnlyDictionary<string, string> StepLogDir = new OrderedDictionary<string, string>(StringComparer.Ordinal)
    {
        ["subfetch"] = "SubFetchLogDir", ["subprep"] = "SubPrepLogDir", ["mux"] = "MuxLogDir",
    };

    public static readonly IReadOnlyDictionary<string, string> ActionText = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Undo.Create] = "tạo", [Undo.Copy] = "chép", [Undo.Move] = "di chuyển", [Undo.Rename] = "đổi tên", [Undo.Mkdir] = "thư mục mới",
        [Undo.Delete] = "vào Thùng rác", [Undo.Encoding] = "chuyển mã (.bat cũ)", ["Skip"] = "bỏ qua", ["Fail"] = "lỗi",
    };

    public static readonly IReadOnlyDictionary<string, string> OpText = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["trash"] = "Vào Thùng rác", ["move_back"] = "Trả về chỗ cũ", ["rename_back"] = "Đổi lại tên cũ", ["rmdir"] = "Xoá thư mục (nếu rỗng)",
    };

    /// <summary><c>{"Create": 3, "Rename": 2}</c> → <c>3 tạo, 2 đổi tên</c>.</summary>
    public static string CountsText(IEnumerable<KeyValuePair<string, int>> counts)
    {
        var parts = counts.Where(kv => kv.Value != 0)
                          .Select(kv => $"{kv.Value.ToString(CultureInfo.InvariantCulture)} {ActionText.GetValueOrDefault(kv.Key, kv.Key)}")
                          .ToList();
        return parts.Count > 0 ? string.Join(", ", parts) : "(trống)";
    }

    [GeneratedRegex(@"(\d{8}_\d{6})")]
    private static partial Regex StampRe();

    /// <summary><c>undo_entries</c>: mọi lần chạy của ba bước trong thư mục phim (mới nhất trước).</summary>
    public static List<UndoEntry> Entries(string folder, Config cfg)
    {
        var outp = new List<UndoEntry>();
        foreach (var (step, key) in StepLogDir)
        {
            foreach (var r in Undo.ListRuns(folder, cfg.Str(key), step))
            {
                var m = StampRe().Match(r.Name);
                outp.Add(new UndoEntry(step, r, m.Success ? m.Groups[1].Value : ""));
            }
        }
        return [.. outp.OrderByDescending(e => e.Stamp, StringComparer.Ordinal).ThenByDescending(e => e.Run.Name, StringComparer.Ordinal)];
    }

    /// <summary><c>plan_lines</c>: dòng của hộp thoại xác nhận hoàn tác.</summary>
    public static List<string> PlanLines(UndoPlan plan, string? root = null)
    {
        string Rel(string p) => !string.IsNullOrEmpty(root) && Tools.IsUnder(p, root) ? Path.GetRelativePath(root, p) : p;

        var lines = new List<string>();
        foreach (var op in plan.Ops)
        {
            var t = OpText.GetValueOrDefault(op.Kind, op.Kind);
            lines.Add(op.Kind is "move_back" or "rename_back" ? $"{t}: {Rel(op.Path)}  →  {Rel(op.Target)}" : $"{t}: {Rel(op.Path)}");
        }
        foreach (var m in plan.Missing)
            lines.Add($"(không còn, bỏ qua): {Rel(m)}");
        foreach (var n in plan.Notes)
            lines.Add($"[i] {n}");
        return lines;
    }
}
