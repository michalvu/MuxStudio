using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MuxStudio.Core;

/// <summary>Một dòng gán <c>$Ten = giá trị</c> đọc được trong .bat.</summary>
/// <param name="Line">Số dòng trong file .bat (đếm từ 1).</param>
/// <param name="Raw">Đoạn văn bản của giá trị.</param>
public sealed record Assignment(string Name, object? Value, int Line, string Raw, bool Ok);

/// <summary>Một giá trị nhập được.</summary>
/// <param name="Source">Tên file .bat.</param>
public sealed record ImportItem(string Key, object? Value, string Source, int Line);

/// <summary>Kết quả đọc các file .bat (chưa ghi gì — xem rồi gọi <see cref="BatImporter.ApplyBatImport"/>).</summary>
public sealed class BatImport
{
    /// <summary>(đường dẫn, loại: subfetch | subprep | muxtool | null).</summary>
    public List<(string Path, string? Kind)> Files { get; } = [];

    public OrderedDictionary<string, ImportItem> Values { get; } = new(StringComparer.Ordinal);

    public OrderedDictionary<string, List<ImportItem>> Conflicts { get; } = new(StringComparer.Ordinal);

    public OrderedDictionary<string, ImportItem> SecretValues { get; } = new(StringComparer.Ordinal);

    public OrderedDictionary<string, List<ImportItem>> SecretConflicts { get; } = new(StringComparer.Ordinal);

    /// <summary>(file, biến, lý do).</summary>
    public List<(string File, string Name, string Reason)> Ignored { get; } = [];

    /// <summary>(file, biến, dòng, lý do).</summary>
    public List<(string File, string Name, int Line, string Reason)> Problems { get; } = [];
}

/// <summary>Biểu thức PowerShell không tự tính được (= <c>_Unparsed</c>).</summary>
internal sealed class UnparsedException : Exception;

/// <summary>Đọc các dòng gán <c>$Ten = giá trị</c> ở cấp ngoài cùng. KHÔNG thông dịch PowerShell. Port <c>_PsScanner</c>.</summary>
internal sealed partial class PsScanner(string text, int lineOffset, IEnumerable<string> wanted)
{
    private readonly string _s = text;
    private readonly int _n = text.Length;
    private readonly HashSet<string> _wanted = wanted.Select(w => w.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
    private readonly List<Assignment> _out = [];

    // \G = neo tại vị trí bắt đầu (= pattern.match(s, i) của Python)
    [GeneratedRegex(@"\G\$([A-Za-z_][A-Za-z0-9_]*(?::[A-Za-z_][A-Za-z0-9_]*)?)\s*=(?!=)")]
    private static partial Regex NameRe();

    [GeneratedRegex("\\G@['\"][ \\t]*\\r?\\n")]
    private static partial Regex HereStartRe();

    [GeneratedRegex(@"(?<!`)\$[A-Za-z_({]")]
    private static partial Regex VarInStringRe();

    [GeneratedRegex("`(.)")]
    private static partial Regex BacktickEscapeRe();

    [GeneratedRegex(@"\G\$(true|false|null)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PsConstRe();

    [GeneratedRegex(@"\G-?\d+(?:\.\d+)?(?![\w.])")]
    private static partial Regex NumberRe();

    [GeneratedRegex(@"\G[A-Za-z0-9_]+")]
    private static partial Regex HashKeyRe();

    private int LineOf(int pos)
    {
        var c = 0;
        var end = Math.Min(pos, _n);
        for (var k = 0; k < end; k++)
        {
            if (_s[k] == '\n')
                c++;
        }
        return c + 1 + lineOffset;
    }

    private bool StartsWithAt(string what, int i) =>
        i >= 0 && i + what.Length <= _n && string.CompareOrdinal(_s, i, what, 0, what.Length) == 0;

    // ---- bỏ qua chuỗi / comment ----
    private int SkipSingle(int i) // i tại dấu '
    {
        i++;
        while (i < _n)
        {
            if (_s[i] == '\'')
            {
                if (i + 1 < _n && _s[i + 1] == '\'')
                {
                    i += 2;
                    continue;
                }
                return i + 1;
            }
            i++;
        }
        return i;
    }

    private int SkipDouble(int i)
    {
        i++;
        while (i < _n)
        {
            var c = _s[i];
            if (c == '`')
            {
                i += 2;
                continue;
            }
            if (c == '"')
            {
                if (i + 1 < _n && _s[i + 1] == '"')
                {
                    i += 2;
                    continue;
                }
                return i + 1;
            }
            i++;
        }
        return i;
    }

    /// <summary><c>@'</c> hoặc <c>@"</c> rồi xuống dòng = mở here-string.</summary>
    private bool IsHereStart(int i) => HereStartRe().IsMatch(_s, i);

    [GeneratedRegex("^'@", RegexOptions.Multiline)]
    private static partial Regex HereEndSingleRe();

    [GeneratedRegex("^\"@", RegexOptions.Multiline)]
    private static partial Regex HereEndDoubleRe();

    private int SkipHere(int i) // i tại '@'
    {
        var m = (_s[i + 1] == '\'' ? HereEndSingleRe() : HereEndDoubleRe()).Match(_s, Math.Min(i + 2, _n));
        return m.Success ? m.Index + m.Length : _n;
    }

    private int SkipLineComment(int i)
    {
        var j = _s.IndexOf('\n', i);
        return j < 0 ? _n : j;
    }

    private int SkipBlockComment(int i)
    {
        var j = i + 2 <= _n ? _s.IndexOf("#>", i + 2, StringComparison.Ordinal) : -1;
        return j < 0 ? _n : j + 2;
    }

    private int SkipWs(int i, bool newlines)
    {
        while (i < _n)
        {
            var c = _s[i];
            if (c is ' ' or '\t' || (newlines && c is '\r' or '\n'))
                i++;
            else if (c == '#' && newlines)
                i = SkipLineComment(i);
            else if (c == '<' && StartsWithAt("<#", i) && newlines)
                i = SkipBlockComment(i);
            else
                break;
        }
        return i;
    }

    // ---- đọc giá trị ----
    private (object? Value, int End) ParseScalar(int i)
    {
        var s = _s;
        if (i >= _n)
            throw new UnparsedException();
        var c = s[i];
        if (c == '\'')
        {
            var j = SkipSingle(i);
            return (Py.Slice(s, i + 1, j - 1).Replace("''", "'", StringComparison.Ordinal), j);
        }
        if (c == '"')
        {
            var j = SkipDouble(i);
            var body = Py.Slice(s, i + 1, j - 1);
            if (VarInStringRe().IsMatch(body))
                throw new UnparsedException(); // chuỗi có biến -> không tự tính
            body = body.Replace("\"\"", "\"", StringComparison.Ordinal);
            body = BacktickEscapeRe().Replace(body, m => m.Groups[1].Value switch
            {
                "t" => "\t",
                "n" => "\n",
                "r" => "\r",
                "0" => "\0",
                var x => x,
            });
            return (body, j);
        }
        var mc = PsConstRe().Match(s, i);
        if (mc.Success)
        {
            var w = mc.Groups[1].Value.ToLowerInvariant();
            return (w == "true" ? true : w == "false" ? false : null, mc.Index + mc.Length);
        }
        var mn = NumberRe().Match(s, i);
        if (mn.Success)
        {
            var t = mn.Value;
            object v = t.Contains('.', StringComparison.Ordinal)
                ? double.Parse(Py.AsciiDigits(t), NumberStyles.Float, CultureInfo.InvariantCulture)
                : Py.Long(t);
            return (v, mn.Index + mn.Length);
        }
        if (StartsWithAt("@(", i))
            return ParseArray(i + 2);
        if (StartsWithAt("@{", i))
            return ParseHash(i + 2);
        throw new UnparsedException();
    }

    private (object? Value, int End) ParseArray(int i)
    {
        var items = new List<object?>();
        while (true)
        {
            i = SkipWs(i, true);
            if (i >= _n)
                throw new UnparsedException();
            if (_s[i] == ')')
                return (items, i + 1);
            if (_s[i] == ',')
            {
                i++;
                continue;
            }
            var (v, j) = ParseScalar(i);
            items.Add(v);
            i = j;
        }
    }

    private (object? Value, int End) ParseHash(int i)
    {
        var o = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        while (true)
        {
            i = SkipWs(i, true);
            if (i >= _n)
                throw new UnparsedException();
            var c = _s[i];
            if (c == '}')
                return (o, i + 1);
            if (c == ';')
            {
                i++;
                continue;
            }
            object? k;
            if (c is '\'' or '"')
            {
                (k, i) = ParseScalar(i);
            }
            else
            {
                var m = HashKeyRe().Match(_s, i);
                if (!m.Success)
                    throw new UnparsedException();
                k = m.Value;
                i = m.Index + m.Length;
            }
            i = SkipWs(i, false);
            if (i >= _n || _s[i] != '=')
                throw new UnparsedException();
            i = SkipWs(i + 1, false);
            var (v, j) = ParseScalar(i);
            i = j;
            o[Py.Str(k)] = v;
        }
    }

    private (object? Value, int End) ParseValue(int i)
    {
        i = SkipWs(i, false);
        var (v, j) = ParseScalar(i);
        var k = SkipWs(j, false);
        if (k < _n && _s[k] is not ('#' or '\r' or '\n' or ';'))
            throw new UnparsedException(); // biểu thức phức tạp: 'a' + 'b', Join-Path ...
        return (v, j);
    }

    // ---- quét ----
    public List<Assignment> Run()
    {
        var s = _s;
        var i = 0;
        var depth = 0;
        var atStmtStart = true;
        while (i < _n)
        {
            var c = s[i];
            if (c == '\n')
            {
                atStmtStart = true;
                i++;
                continue;
            }
            if (c is ' ' or '\t' or '\r')
            {
                i++;
                continue;
            }
            if (c == '#')
            {
                i = SkipLineComment(i);
                continue;
            }
            if (c == '<' && StartsWithAt("<#", i))
            {
                i = SkipBlockComment(i);
                continue;
            }
            if (c == '@' && IsHereStart(i))
            {
                i = SkipHere(i);
                atStmtStart = false;
                continue;
            }
            if (c == '\'')
            {
                i = SkipSingle(i);
                atStmtStart = false;
                continue;
            }
            if (c == '"')
            {
                i = SkipDouble(i);
                atStmtStart = false;
                continue;
            }
            if (c == '{')
            {
                depth++;
                i++;
                atStmtStart = true;
                continue;
            }
            if (c == '}')
            {
                depth = Math.Max(0, depth - 1);
                i++;
                atStmtStart = false;
                continue;
            }
            if (c == ';')
            {
                atStmtStart = true;
                i++;
                continue;
            }
            if (c == '$' && depth == 0 && atStmtStart)
            {
                var m = NameRe().Match(s, i);
                if (m.Success && _wanted.Contains(m.Groups[1].Value.ToLowerInvariant()))
                {
                    var start = m.Index + m.Length;
                    try
                    {
                        var (v, j) = ParseValue(start);
                        _out.Add(new Assignment(m.Groups[1].Value, v, LineOf(i), Py.Slice(s, start, j).Trim(), true));
                        i = j;
                        atStmtStart = false;
                        continue;
                    }
                    catch (UnparsedException)
                    {
                        var eol = s.IndexOf('\n', start);
                        var raw = Py.Slice(s, start, eol < 0 ? _n : eol).Trim();
                        _out.Add(new Assignment(m.Groups[1].Value, null, LineOf(i), raw, false));
                    }
                }
            }
            atStmtStart = false;
            i++;
        }
        return _out;
    }
}

/// <summary>Nhập cấu hình từ file .bat (phần cuối của <c>core/config.py</c>).</summary>
public static partial class BatImporter
{
    [GeneratedRegex(@"^\$SubSourceApiKey\s*=", RegexOptions.Multiline)]
    private static partial Regex SubFetchMarkRe();

    [GeneratedRegex(@"^\$AlassArgsAudioMode\s*=", RegexOptions.Multiline)]
    private static partial Regex SubPrepMarkRe();

    [GeneratedRegex(@"^\$DonorFolder\s*=", RegexOptions.Multiline)]
    private static partial Regex MuxToolMarkRe();

    [GeneratedRegex(@"^\$(Lang_[A-Za-z]{2,3})\s*=", RegexOptions.Multiline)]
    private static partial Regex LangVarRe();

    [GeneratedRegex(@"^Lang_([a-z]{2,3})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LangSwitchRe();

    /// <summary>Nhận diện file theo nội dung (không theo tên).</summary>
    public static string? DetectBatKind(string text)
    {
        if (SubFetchMarkRe().IsMatch(text))
            return "subfetch";
        if (SubPrepMarkRe().IsMatch(text))
            return "subprep";
        if (MuxToolMarkRe().IsMatch(text))
            return "muxtool";
        return null;
    }

    /// <summary>Phần PowerShell sau dòng <c>#&gt;</c> của khối <c>&lt;# : ... #&gt;</c> đầu file; trả (văn bản, số dòng bù).</summary>
    internal static (string Text, int Offset) PsPart(string text)
    {
        var i = text.IndexOf("#>", StringComparison.Ordinal);
        if (text.TrimStart().StartsWith("<#", StringComparison.Ordinal) && i >= 0)
            return (text[(i + 2)..], text[..(i + 2)].Count(ch => ch == '\n'));
        return (text, 0);
    }

    /// <summary>Tên biến trong .bat → khoá cấu hình (khi khác tên).</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["UserMkvToolNix"] = "MkvToolNixDir",
        ["ApiBase"] = "SubSourceApiBase",
    };

    private static readonly Dictionary<string, string> LogDirByKind = new(StringComparer.Ordinal)
    {
        ["subprep"] = "SubPrepLogDir",
        ["muxtool"] = "MuxLogDir",
    };

    /// <summary>Biến thuộc console / đã thay bằng nút trên GUI → bỏ qua, có báo.</summary>
    public static readonly IReadOnlyDictionary<string, string> IgnoredVars = BuildIgnored();

    private static Dictionary<string, string> BuildIgnored()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var k in new[] { "TableWidth", "TableMinWidth", "ConsoleCols", "ConsoleRows", "ConsoleScrollback", "UseColors", "UseGridView" })
            d[k] = "phần console — GUI thay hết";
        d["UndoLastRun"] = "thay bằng nút \"Hoàn tác…\"";
        d["DryRun"] = "thay bằng nút \"Xem lệnh\"";
        d["PreviewFirst"] = "GUI luôn hiện bảng xem trước";
        d["KnownLangTags"] = "thay bằng bảng ngôn ngữ chung";
        foreach (var k in new[] { "PoolFolder", "UsePool", "PoolTryLimit", "PoolMinVotes", "PoolFirst", "PoolDefaultLang" })
            d[k] = "kho nội bộ srt_pool đã bỏ từ bản 1.2.0";
        return d;
    }

    /// <summary>Khoá dạng danh sách: nhiều file khác nhau → gộp (hợp) thay vì báo xung đột.</summary>
    private static readonly HashSet<string> UnionKeys = new(StringComparer.Ordinal) { "VideoExts" };

    /// <summary>Khoá cấu hình KHÔNG đọc trực tiếp từ .bat (trong .bat là biến tính ra, vd <c>$Languages = @(...)</c>).</summary>
    private static readonly HashSet<string> DerivedKeys = new(StringComparer.Ordinal) { "Languages" };

    private static HashSet<string> WantedNames()
    {
        var names = new HashSet<string>(ConfigSchema.FieldByKey.Keys.Except(DerivedKeys), StringComparer.Ordinal);
        names.UnionWith(Secrets.SecretKeys);
        names.UnionWith(Aliases.Keys);
        names.UnionWith(IgnoredVars.Keys);
        names.Add("LogDirName");
        names.Add("LangExtra");
        foreach (var c in Naming.FetchLangSwitches)
            names.Add($"Lang_{c}");
        return names;
    }

    public static List<Assignment> ParseBatText(string text)
    {
        var (ps, off) = PsPart(text);
        var wanted = WantedNames();
        // Lang_xx của ngôn ngữ ngoài danh sách có sẵn cũng nhận
        foreach (Match m in LangVarRe().Matches(ps))
            wanted.Add(m.Groups[1].Value);
        return new PsScanner(ps, off, wanted).Run();
    }

    private static void Add(BatImport imp, ImportItem item, bool secret)
    {
        var vals = secret ? imp.SecretValues : imp.Values;
        var confl = secret ? imp.SecretConflicts : imp.Conflicts;
        if (confl.TryGetValue(item.Key, out var lst))
        {
            if (lst.All(x => !Py.Equal(x.Value, item.Value)))
                lst.Add(item);
            return;
        }
        if (!vals.TryGetValue(item.Key, out var prev))
        {
            vals[item.Key] = item;
            return;
        }
        if (Py.Equal(prev.Value, item.Value))
            return;
        if (!secret && UnionKeys.Contains(item.Key) && prev.Value is IList pl && item.Value is IList il)
        {
            var merged = pl.Cast<object?>().ToList();
            merged.AddRange(il.Cast<object?>().Where(x => !Py.Contains(pl, x)));
            vals[item.Key] = new ImportItem(item.Key, merged.Select(Py.Str).ToList(), $"{prev.Source} + {item.Source}", prev.Line);
            return;
        }
        vals.Remove(item.Key);
        confl[item.Key] = [prev, item];
    }

    /// <summary>Đọc cấu hình ở đầu các file .bat. Không ghi gì — xem kết quả rồi gọi <see cref="ApplyBatImport"/>.</summary>
    public static BatImport ImportFromBat(IEnumerable<string> paths)
    {
        var imp = new BatImport();
        foreach (var path in paths)
        {
            var p = path.Replace('/', '\\');
            var fname = Path.GetFileName(p);
            string text;
            try
            {
                text = new System.Text.UTF8Encoding(false, false).GetString(File.ReadAllBytes(p));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                imp.Problems.Add((fname, "", 0, $"không đọc được file: {e.Message}"));
                continue;
            }
            var kind = DetectBatKind(text);
            imp.Files.Add((p, kind));
            if (kind is null)
            {
                imp.Problems.Add((fname, "", 0, "không nhận ra là SubFetch / SubPrep / MuxTool"));
                continue;
            }
            var langSw = new OrderedDictionary<string, bool>(StringComparer.Ordinal);
            var langExtra = new List<string>();
            var langLine = 0;
            foreach (var a in ParseBatText(text))
            {
                var name = a.Name;
                if (IgnoredVars.TryGetValue(name, out var why))
                {
                    imp.Ignored.Add((fname, name, why));
                    continue;
                }
                if (!a.Ok)
                {
                    imp.Problems.Add((fname, name, a.Line, $"giá trị không đọc được: {(a.Raw.Length > 60 ? a.Raw[..60] : a.Raw)}"));
                    continue;
                }
                var m = LangSwitchRe().Match(name);
                if (m.Success)
                {
                    if (a.Value is bool bv)
                    {
                        langSw[m.Groups[1].Value.ToLowerInvariant()] = bv;
                        langLine = langLine != 0 ? langLine : a.Line;
                    }
                    else
                    {
                        imp.Problems.Add((fname, name, a.Line, "cần $true/$false"));
                    }
                    continue;
                }
                if (name == "LangExtra")
                {
                    var vals = a.Value is IList l ? l.Cast<object?>() : [a.Value];
                    langExtra = vals.Select(x => Py.Str(x).Trim()).Where(x => x.Length > 0).Select(x => x.ToLowerInvariant()).ToList();
                    continue;
                }
                if (Secrets.SecretKeys.Contains(name))
                {
                    if (a.Value is string sv && sv.Trim().Length > 0)
                        Add(imp, new ImportItem(name, sv.Trim(), fname, a.Line), secret: true);
                    continue;
                }
                string? key;
                if (name == "LogDirName")
                {
                    if (!LogDirByKind.TryGetValue(kind, out key))
                        continue;
                }
                else
                {
                    key = Aliases.GetValueOrDefault(name, name);
                }
                if (!ConfigSchema.FieldByKey.ContainsKey(key))
                    continue;
                object? v;
                try
                {
                    v = ConfigSchema.Coerce(key, a.Value);
                }
                catch (ConfigValueException e)
                {
                    imp.Problems.Add((fname, name, a.Line, e.Message));
                    continue;
                }
                Add(imp, new ImportItem(key, v, fname, a.Line), secret: false);
            }
            if (langSw.Count > 0)
            {
                var order = Naming.FetchLangSwitches.Where(c => langSw.GetValueOrDefault(c)).ToList();
                order.AddRange(langSw.Where(kv => kv.Value && !order.Contains(kv.Key)).Select(kv => kv.Key));
                foreach (var c in langExtra)
                {
                    if (!order.Contains(c))
                        order.Add(c);
                }
                Add(imp, new ImportItem("Languages", order, fname, langLine), secret: false);
            }
        }
        return imp;
    }

    /// <summary>
    /// Ghi kết quả nhập vào <paramref name="cfg"/> (và bí mật vào <paramref name="secretStore"/>). Khoá xung đột chỉ ghi
    /// khi có trong <paramref name="resolutions"/>. Bí mật rỗng KHÔNG xoá bí mật đang có. Trả danh sách khoá đã ghi.
    /// </summary>
    public static List<string> ApplyBatImport(Config cfg, BatImport imp, IReadOnlyDictionary<string, object?>? resolutions = null,
        SecretStore? secretStore = null)
    {
        resolutions ??= new Dictionary<string, object?>();
        var done = new List<string>();
        foreach (var (k, it) in imp.Values)
        {
            cfg[k] = it.Value;
            done.Add(k);
        }
        foreach (var (k, v) in resolutions)
        {
            if (imp.Conflicts.ContainsKey(k))
            {
                cfg[k] = v;
                done.Add(k);
            }
        }
        if (secretStore is not null)
        {
            foreach (var (k, it) in imp.SecretValues)
            {
                secretStore.Set(k, Py.Str(it.Value));
                done.Add(k);
            }
            foreach (var (k, v) in resolutions)
            {
                if (imp.SecretConflicts.ContainsKey(k) && Py.Truthy(v))
                {
                    secretStore.Set(k, Py.Str(v));
                    done.Add(k);
                }
            }
        }
        foreach (var c in cfg.List("Languages"))
            Naming.RegisterLanguage(c);
        return done;
    }
}
