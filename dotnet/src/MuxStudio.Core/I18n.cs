using System.Globalization;
using System.Text.RegularExpressions;

namespace MuxStudio.Core;

/// <summary>
/// Hai ngôn ngữ giao diện: Tiếng Việt (gốc) và English. Port <c>core/i18n.py</c>.
/// <para>Mã nguồn giữ nguyên chữ tiếng Việt — mọi phép so sánh, tên trạng thái, nhật ký hoàn tác… chạy Y NHƯ NHAU ở cả hai
/// ngôn ngữ. Việc dịch chỉ xảy ra ở CHỖ HIỂN THỊ: <see cref="Tr(string?)"/>.</para>
/// <para>Bảng dịch <c>{mẫu tiếng Việt: mẫu tiếng Anh}</c>, mỗi chỗ chèn giá trị viết <c>{}</c>. <c>Tr</c> nhận câu ĐÃ GHÉP giá trị
/// nên phải so ngược với mẫu: (1) trùng nguyên văn; (2) khớp một mẫu có <c>{}</c> (mẫu có phần chữ cố định dài nhất) → các giá trị
/// được dịch tiếp (đệ quy) rồi ghép vào mẫu tiếng Anh; (3) không khớp → tách theo dòng, bỏ ký hiệu đầu/cuối, tách theo
/// <c>" | "</c>, <c>"; "</c>… và dịch từng phần. Không dịch được thì giữ nguyên tiếng Việt.</para>
/// </summary>
public static partial class I18n
{
    public static readonly IReadOnlyList<string> Languages = ["vi", "en"];

    private static volatile string _lang = "vi";
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, string> Cache = new(StringComparer.Ordinal);
    private static readonly HashSet<string> MissingSet = new(StringComparer.Ordinal);
    private const int MaxCache = 20000;

    [GeneratedRegex("[àáảãạăằắẳẵặâầấẩẫậđèéẻẽẹêềếểễệìíỉĩịòóỏõọôồốổỗộơờớởỡợùúủũụưừứửữựỳýỷỹỵ" +
                    "ÀÁẢÃẠĂẰẮẲẴẶÂẦẤẨẪẬĐÈÉẺẼẸÊỀẾỂỄỆÌÍỈĨỊÒÓỎÕỌÔỒỐỔỖỘƠỜỚỞỠỢÙÚỦŨỤƯỪỨỬỮỰỲÝỶỸỴ]")]
    private static partial Regex ViRe();

    [GeneratedRegex(@"\{(\d*)\}")]
    private static partial Regex SlotRe();

    private static readonly string[] Seps = ["  |  ", " | ", "; ", " — ", " · ", " → ", ": ", ", ", " - ", " ("];

    // \w của Python = chữ + mọi loại số (cả ①, ²) + "_" (KHÔNG gồm dấu tổ hợp) -> [\p{L}\p{N}_]
    /// <summary>Phần đầu không cần dịch: khoảng trắng, nhãn [!], ký hiệu ▶ ✓ →, số thứ tự bước "1. ".</summary>
    [GeneratedRegex(@"^((?:\s*\[[^\]\s]{1,4}\])?[^\p{L}\p{N}_(""']*(?:\d+\. )?)(.+)$", RegexOptions.Singleline)]
    private static partial Regex HeadRe();

    [GeneratedRegex(@"^(.*?)([^\p{L}\p{N}_""'%]+)$", RegexOptions.Singleline)]
    private static partial Regex TailRe();

    [GeneratedRegex(@"^(\d+ )(.+)$", RegexOptions.Singleline)]
    private static partial Regex CountRe();

    public static bool HasVietnamese(string text) => ViRe().IsMatch(text);

    public static string Language => _lang;

    public static string Normalize(object? code)
    {
        var c = (code is null ? "" : Py.Str(code)).Trim().ToLowerInvariant();
        return Languages.Contains(c) ? c : "vi";
    }

    /// <summary>Đặt ngôn ngữ hiển thị (<c>vi</c> / <c>en</c>; giá trị lạ → <c>vi</c>). Trả ngôn ngữ đang dùng.</summary>
    public static string SetLanguage(object? code)
    {
        var l = Normalize(code);
        lock (Gate)
        {
            _lang = l;
            Cache.Clear();
        }
        return l;
    }

    /// <summary>Câu có chữ tiếng Việt mà không dịch được (để kiểm tra khi phát triển).</summary>
    public static IReadOnlyCollection<string> Missing
    {
        get
        {
            lock (Gate)
                return [.. MissingSet];
        }
    }

    // ------------------------------------------------------------------
    //  Bảng dịch
    // ------------------------------------------------------------------

    private sealed record Pattern((int, int, int) Prio, string Key, Regex Rx, string En, bool A, bool B);

    /// <summary>
    /// Bảng dịch đã lập chỉ mục. Ngoài mẫu nguyên câu, mẫu bắt đầu / kết thúc bằng ký tự KHÔNG phải chữ-số còn được coi là
    /// MẢNH ghép (mã nguồn hay nối chúng với phần khác), nên phía đó được phép có thêm chữ (dịch tiếp bằng đệ quy).
    /// </summary>
    private sealed class Table
    {
        public Dictionary<string, string> Exact { get; } = new(StringComparer.Ordinal);

        public List<Pattern> Patterns { get; }

        public List<Pattern> AsciiPatterns { get; }

        public Table(OrderedDictionary<string, string> entries)
        {
            var pats = new List<Pattern>();
            foreach (var (vi, en) in entries)
            {
                if (en.Length == 0 || vi.Length == 0)
                    continue;
                var lits = vi.Split("{}");
                var fixedLen = lits.Sum(x => x.Length);
                if (fixedLen == 0)
                    continue;
                var body = string.Join("(.*?)", lits.Select(Regex.Escape));
                var key = lits.MaxBy(x => x.Length)!; // bằng nhau -> phần đầu tiên (như max() của Python)
                var n = lits.Length - 1;
                if (n == 0)
                    Exact[vi] = en;
                else
                    pats.Add(new Pattern((-fixedLen, 0, n), key, Rx("^" + body + "$"), en, false, false));
                if (vi.Trim().Length < 5) // mảnh quá ngắn ("còn ") dễ khớp nhầm giữa câu khác
                    continue;
                var lead = !Py.IsAlnum(vi[0]);
                var trail = !Py.IsAlnum(vi[^1]);
                foreach (var (a, b) in FragmentCombos(lead, trail))
                {
                    var rx = Rx("^" + (a ? "(.+)" : "") + body + (b ? "(.+)" : "") + "$");
                    pats.Add(new Pattern((-fixedLen, 1 + (a ? 1 : 0) + (b ? 1 : 0), n), key, rx, en, a, b));
                }
            }
            Patterns = [.. pats.OrderBy(p => p.Prio)]; // sắp xếp ỔN ĐỊNH như list.sort của Python
            // mẫu KHÔNG DẤU ("trong MKV: {}", "Xong: {}."): danh sách riêng, thử cho câu không có chữ có dấu
            AsciiPatterns = [.. Patterns.Where(p => !ViRe().IsMatch(p.Rx.ToString()) && !p.A && !p.B)];
        }

        private static Regex Rx(string pattern) => new(pattern, RegexOptions.Singleline | RegexOptions.CultureInvariant);

        /// <summary><c>{(lead, False), (False, trail), (lead, trail)} - {(False, False)}</c> theo đúng thứ tự duyệt set của CPython.</summary>
        private static IEnumerable<(bool A, bool B)> FragmentCombos(bool lead, bool trail) => (lead, trail) switch
        {
            (true, false) => [(true, false)],
            (false, true) => [(false, true)],
            (true, true) => [(true, false), (true, true), (false, true)],
            _ => [],
        };

        /// <summary>→ (mẫu tiếng Anh, các giá trị, phần đầu thừa, phần đuôi thừa). <paramref name="plain"/> = câu không có chữ có dấu.</summary>
        public (string En, string[] Groups, string Head, string Tail)? Match(string text, bool plain)
        {
            foreach (var p in plain ? AsciiPatterns : Patterns)
            {
                if (!text.Contains(p.Key, StringComparison.Ordinal))
                    continue;
                var m = p.Rx.Match(text);
                if (!m.Success)
                    continue;
                var g = new string[m.Groups.Count - 1];
                for (var i = 1; i < m.Groups.Count; i++)
                    g[i - 1] = m.Groups[i].Value;
                var head = p.A ? g[0] : "";
                var tail = p.B ? g[^1] : "";
                return (p.En, g[(p.A ? 1 : 0)..(p.B ? g.Length - 1 : g.Length)], head, tail);
            }
            return null;
        }
    }

    private static readonly Lazy<(OrderedDictionary<string, string> En, IReadOnlyList<string> Skip)> Data = new(LoadData);

    private static readonly Lazy<Table> EnTable = new(() => new Table(Data.Value.En));

    /// <summary>Bảng dịch Việt → Anh (= <c>i18n_en.EN</c>).</summary>
    public static OrderedDictionary<string, string> EnEntries => Data.Value.En;

    /// <summary>Câu cố ý không dịch (= <c>i18n_en.SKIP</c>).</summary>
    public static IReadOnlyList<string> SkipEntries => Data.Value.Skip;

    private static (OrderedDictionary<string, string>, IReadOnlyList<string>) LoadData()
    {
        using var s = typeof(I18n).Assembly.GetManifestResourceStream("MuxStudio.Core.i18n_en.json")
            ?? throw new InvalidOperationException("thiếu tài nguyên i18n_en.json");
        using var r = new StreamReader(s, Py.Utf8Strict);
        var root = (OrderedDictionary<string, object?>)PyJson.Loads(r.ReadToEnd())!;
        var en = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in (OrderedDictionary<string, object?>)root["EN"]!)
            en[k] = (string)v!;
        var skip = ((List<object?>)root["SKIP"]!).Select(x => (string)x!).ToList();
        return (en, skip);
    }

    // ------------------------------------------------------------------
    //  Dịch
    // ------------------------------------------------------------------

    /// <summary>Ghép giá trị vào mẫu tiếng Anh: <c>{}</c> lần lượt, hoặc <c>{0}</c> <c>{1}</c> để đổi thứ tự.</summary>
    public static string Fill(string template, IReadOnlyList<string> vals)
    {
        var it = 0;
        return SlotRe().Replace(template, m =>
        {
            int i;
            if (m.Groups[1].Value.Length > 0)
                i = Py.Int(m.Groups[1].Value);
            else
                i = it < vals.Count ? it++ : -1;
            return i >= 0 && i < vals.Count ? vals[i] : "";
        });
    }

    private static string? Whole(string text, int depth)
    {
        var t = EnTable.Value;
        if (t.Exact.TryGetValue(text, out var hit))
            return hit;
        var m = t.Match(text, plain: !ViRe().IsMatch(text));
        if (m is null)
            return null;
        var (en, groups, head, tail) = m.Value;
        return (head.Length > 0 ? TrCore(head, depth + 1) : "")
               + Fill(en, groups.Select(g => g.Length > 0 ? TrCore(g, depth + 1) : g).ToList())
               + (tail.Length > 0 ? TrCore(tail, depth + 1) : "");
    }

    private static string TrCore(string text, int depth = 0)
    {
        if (text.Length == 0 || depth > 6)
            return text;
        lock (Gate)
        {
            if (Cache.TryGetValue(text, out var got))
                return got;
        }
        var outp = Whole(text, depth) ?? Pieces(text, depth);
        lock (Gate)
        {
            if (Cache.Count > MaxCache)
                Cache.Clear();
            Cache[text] = outp;
        }
        return outp;
    }

    private static string ReplaceFirst(string s, string what, string with)
    {
        var i = s.IndexOf(what, StringComparison.Ordinal);
        return i < 0 ? s : string.Concat(s.AsSpan(0, i), with, s.AsSpan(i + what.Length));
    }

    private static List<int> FindAll(string text, string sep)
    {
        var outp = new List<int>();
        var i = text.IndexOf(sep, StringComparison.Ordinal);
        while (i >= 0)
        {
            outp.Add(i);
            i = text.IndexOf(sep, i + 1, StringComparison.Ordinal);
        }
        return outp;
    }

    private static string Pieces(string text, int depth)
    {
        if (text.Contains('\n', StringComparison.Ordinal))
            return string.Join("\n", text.Split('\n').Select(x => TrCore(x, depth + 1)));
        // 1. bỏ phần đầu không cần dịch ("  [!] ", "▶ 1. ", "2 "), rồi cả ký hiệu cuối (" →", "…", ".")
        string head = "", core = text;
        var m = HeadRe().Match(text);
        if (m.Success && m.Groups[1].Value.Length > 0)
        {
            head = m.Groups[1].Value;
            core = m.Groups[2].Value;
            var got = Whole(core, depth);
            if (got is not null)
                return head + got;
        }
        var t = TailRe().Match(core);
        if (t.Success && t.Groups[1].Value.Length > 0)
        {
            var got = Whole(t.Groups[1].Value, depth);
            if (got is not null)
                return head + got + t.Groups[2].Value;
        }
        if (!ViRe().IsMatch(text))
            return text;
        // 2. phần TRƯỚC một dấu ngăn cách là một câu trọn vẹn -> dịch nó, phần sau dịch tiếp
        var cuts = Seps.SelectMany(sep => FindAll(core, sep)).Distinct().Order().ToList();
        foreach (var i in cuts)
        {
            var sep = Seps.First(x => string.CompareOrdinal(core, i, x, 0, x.Length) == 0 && i + x.Length <= core.Length);
            var got = i > 0 ? Whole(core[..i], depth) : null;
            if (got is not null)
                return head + got + sep + TrCore(core[(i + sep.Length)..], depth + 1);
        }
        // 3. "(…)" bọc ngoài
        var st = core.Trim();
        if (st.Length > 2 && st[0] == '(' && st[^1] == ')' && depth < 6)
        {
            var inner = TrCore(st[1..^1], depth + 1);
            if (inner != st[1..^1])
                return head + ReplaceFirst(core, st, "(" + inner + ")");
        }
        if (st.EndsWith(')') && depth < 6) // "… (…)" ở cuối: dịch riêng phần trong ngoặc và phần trước nó
        {
            int level = 0, k = st.Length - 1;
            while (k >= 0)
            {
                level += st[k] == ')' ? 1 : 0;
                level -= st[k] == '(' ? 1 : 0;
                if (level == 0)
                    break;
                k--;
            }
            if (k > 0)
            {
                var before = st[..k];
                var inner = st[(k + 1)..^1];
                var b2 = TrCore(before, depth + 1);
                var i2 = TrCore(inner, depth + 1);
                if (b2 != before || i2 != inner)
                    return head + ReplaceFirst(core, st, b2 + "(" + i2 + ")");
            }
        }
        // 4. tách hết theo một dấu ngăn cách
        foreach (var sep in Seps)
        {
            if (core.Contains(sep, StringComparison.Ordinal))
            {
                var parts = core.Split(sep);
                var done = parts.Select(p => TrCore(p, depth + 1)).ToArray();
                if (!done.SequenceEqual(parts, StringComparer.Ordinal))
                    return head + string.Join(sep, done);
            }
        }
        if (head.Length > 0)
        {
            var got2 = TrCore(core, depth + 1);
            if (got2 != core)
                return head + got2;
        }
        var c = CountRe().Match(core); // "2 tạo", "1 CẦN XEM": số đếm + nhãn
        if (c.Success)
        {
            var got3 = TrCore(c.Groups[2].Value, depth + 1);
            if (got3 != c.Groups[2].Value)
                return head + c.Groups[1].Value + got3;
        }
        lock (Gate)
            MissingSet.Add(text);
        return text;
    }

    /// <summary>Dịch một câu để HIỂN THỊ. Đang dùng tiếng Việt / null / rỗng → trả nguyên.</summary>
    public static string? Tr(string? text)
    {
        if (_lang == "vi" || string.IsNullOrEmpty(text))
            return text;
        return TrCore(text);
    }

    /// <summary>Dịch câu không null (tiện cho giao diện).</summary>
    public static string T(string text) => Tr(text) ?? "";

    /// <summary>Dịch <paramref name="text"/> sang ngôn ngữ <paramref name="code"/> mà không đổi ngôn ngữ đang hiển thị.</summary>
    public static string TranslateTo(string code, string text) => Normalize(code) == "vi" ? text : TrCore(text);

    public static List<string?> TrAll(IEnumerable<string?> items) => items.Select(Tr).ToList();

    /// <summary><c>MUXSTUDIO_LANG=en</c> ép ngôn ngữ (dùng khi thử nghiệm).</summary>
    public static void InitFromEnv()
    {
        var v = Environment.GetEnvironmentVariable("MUXSTUDIO_LANG");
        if (!string.IsNullOrEmpty(v))
            SetLanguage(v);
    }

    /// <summary>Số chỗ chèn của bản dịch khớp mẫu (= <c>slots_ok</c> của tools/i18n_build.py).</summary>
    public static bool SlotsOk(string vi, string en)
    {
        var n = FindAll(vi, "{}").Count;
        var found = SlotRe().Matches(en).Select(m => m.Groups[1].Value).ToList();
        if (found.Any(x => x.Length > 0) && !found.All(x => x.Length > 0))
            return false;
        if (found.Any(x => x.Length > 0))
            return found.All(x => int.Parse(x, CultureInfo.InvariantCulture) < n);
        return found.Count == n;
    }
}
