using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MuxStudio.Core.Ui;

/// <summary>Một dòng của bảng "Nhập từ .bat": khoá — giá trị hiện tại — giá trị từ .bat (port <c>ImportRow</c>).
/// <see cref="Group"/> rỗng = dòng thường; khác rỗng = một lựa chọn của khoá xung đột (cùng group).</summary>
public sealed record ImportRow(string Key, string Label, string Current, string New, string Source, bool Secret, string Group = "",
                               object? Value = null, bool Same = false);

/// <summary>Phần thuần của thẻ "Cài đặt" (port gui/settings_view.py).</summary>
public static partial class SettingsUi
{
    /// <summary>Danh sách mã mỗi mục trên một dòng (giá trị có thể chứa dấu cách).</summary>
    public static readonly IReadOnlySet<string> LineLists = new HashSet<string>(StringComparer.Ordinal) { "ExtraToolDirs", "RefExcludeFolders" };

    public static readonly IReadOnlyDictionary<string, string> ToolByKey =
        Tools.ConfigPathKey.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);

    public static readonly IReadOnlyDictionary<string, string> DirFinders =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["MkvToolNixDir"] = "mkvmerge", ["AlassDir"] = "alass" };

    public static readonly IReadOnlyList<(string Key, string Label)> CodecParts =
        [("res", "Độ phân giải"), ("hdr", "HDR / DV"), ("vcodec", "Codec video"), ("acodec", "Codec audio")];

    /// <summary>9 bí mật: (nhãn, chú thích, nút "Kiểm tra kết nối" nào).</summary>
    public static readonly IReadOnlyDictionary<string, (string Label, string Help, string? Check)> SecretInfo =
        new OrderedDictionary<string, (string, string, string?)>(StringComparer.Ordinal)
        {
            ["SubSourceApiKey"] = ("SubSource API key", "subsource.net › My Profile › API Key.", "subsource"),
            ["TmdbApiKey"] = ("TMDB API key / token", "themoviedb.org › Settings › API: key 32 ký tự hoặc token dài bắt đầu 'eyJ'. "
                                                      + "Dùng ở bước ① (tìm IMDb ID) và ③ (đặt tên).", "tmdb"),
            ["TvdbApiKey"] = ("TVDB API key", "thetvdb.com › Dashboard › API Keys.", "tvdb"),
            ["TvdbPin"] = ("TVDB PIN", "Chỉ cần với key loại 'user-supported'.", null),
            ["OsApiKey"] = ("OpenSubtitles API key", "opensubtitles.com › API consumers.", "opensubtitles"),
            ["OsUsername"] = ("OpenSubtitles — tên đăng nhập", "Cần để TẢI phụ đề (không có thì chỉ tìm được).", null),
            ["OsPassword"] = ("OpenSubtitles — mật khẩu", "", null),
            ["TitulkyUsername"] = ("Titulky.com — tên đăng nhập", "Không bắt buộc (nguồn phụ đề tiếng Séc). Không có tài khoản vẫn tìm "
                                   + "và tải được kiểu khách, nhưng hay phải gõ mã CAPTCHA. Theo thông báo của trang: tài khoản "
                                   + "Premium tải được 25 phụ đề mỗi ngày, không phụ thuộc địa chỉ IP.", "titulky"),
            ["TitulkyPassword"] = ("Titulky.com — mật khẩu", "", null),
        };

    public static readonly IReadOnlyDictionary<string, string> CheckNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["subsource"] = "SubSource", ["tmdb"] = "TMDB", ["tvdb"] = "TVDB", ["opensubtitles"] = "OpenSubtitles", ["titulky"] = "Titulky",
    };

    /// <summary><c>==</c> của Python giữa hai giá trị cấu hình (<c>is_dirty</c>).</summary>
    public static bool Same(object? a, object? b) => Py.Equal(a, b);

    /// <summary>Bản sao cấu hình (cả khoá lạ) — để "Tự tìm" / "Kiểm tra kết nối" dùng giá trị đang gõ mà chưa lưu.</summary>
    public static Config CopyConfig(Config cfg) =>
        new(cfg.Where(cfg.ContainsKey).Select(k => new KeyValuePair<string, object?>(k, Py.DeepCopy(cfg[k]))), cfg.Path);

    /// <summary><c>field_kind</c>: bool int float str dir file list dict choice langs parts.</summary>
    public static string FieldKind(Field f)
    {
        if (f.Key == "CodecTagParts")
            return "parts";
        if (f.Kind.Length > 0)
            return f.Kind;
        return f.Default switch
        {
            bool => "bool",
            long or int => "int",
            double => "float",
            IList => "list",
            IDictionary => "dict",
            _ => "str",
        };
    }

    private static string NumText(object? v) => v switch
    {
        bool b => b ? "True" : "False",
        long or int => Py.Str(v),
        double d => Timing.FmtInv(d),
        _ => Py.Str(v),
    };

    /// <summary><c>value_to_text</c>: giá trị cấu hình → chữ trong ô nhập.</summary>
    public static string ValueToText(Field f, object? v)
    {
        var k = FieldKind(f);
        if (k is "list" or "parts")
        {
            var items = v is IList l ? l.Cast<object?>().Select(Py.Str).ToList() : [];
            return string.Join(LineLists.Contains(f.Key) ? "\n" : " ", items);
        }
        if (k == "dict")
        {
            if (v is not IDictionary d)
                return "";
            var lines = new List<string>();
            foreach (DictionaryEntry kv in d)
                lines.Add($"{Py.Str(kv.Key)} = {NumText(kv.Value)}");
            return string.Join("\n", lines);
        }
        if (k == "langs")
            return v is IList l2 ? string.Join(" ", l2.Cast<object?>().Select(Py.Str)) : "";
        if (v is null)
            return "";
        return NumText(v);
    }

    [GeneratedRegex(@"^(.*?)\s*[=:]\s*([-+]?\d+(?:[.,]\d+)?)\s*$")]
    private static partial Regex DictLineRe();

    [GeneratedRegex(@"^[-+]?\d+\z")]
    private static partial Regex IntRe();

    /// <summary><c>parse_dict_text</c>: <c>DonorOffsetMs</c>: mỗi dòng <c>khoá = số</c> (khoá có thể có dấu cách: <c>tap #643 = -500</c>).</summary>
    public static OrderedDictionary<string, object?> ParseDictText(string text)
    {
        var outp = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        var lines = Py.SplitLines(text);
        for (var n = 1; n <= lines.Count; n++)
        {
            var ln = Py.StripWs(lines[n - 1]);
            if (ln.Length == 0 || (ln.StartsWith('#') && !ln.Contains('=', StringComparison.Ordinal)))
                continue;
            var m = DictLineRe().Match(ln);
            if (!m.Success || Py.StripWs(m.Groups[1].Value).Length == 0)
                throw new ConfigValueException(string.Create(CultureInfo.InvariantCulture, $"dòng {n}: cần dạng 'khoá = số' (vd S01E03 = 1200)"));
            var key = Py.StripWs(m.Groups[1].Value).Trim('\'', '"');
            var num = m.Groups[2].Value.Replace(',', '.');
            outp[key] = IntRe().IsMatch(num) ? Py.Long(num) : Timing.TryParsePyFloat(num, out var d) ? d : 0.0;
        }
        return outp;
    }

    /// <summary><c>text_to_value</c>: chữ trong ô nhập → giá trị đúng kiểu (<see cref="ConfigValueException"/> kèm lý do tiếng Việt nếu sai).</summary>
    public static object? TextToValue(Field f, string? text)
    {
        var k = FieldKind(f);
        var t = text ?? "";
        if (k is "list" or "parts")
        {
            var items = LineLists.Contains(f.Key) ? Py.SplitLines(t) : Py.SplitWs(t);
            return items.Select(Py.StripWs).Where(x => x.Length > 0).ToList();
        }
        if (k == "langs")
            return Py.SplitWs(t.Replace(',', ' ')).Select(x => Py.StripWs(x).ToLowerInvariant()).Where(x => x.Length > 0).ToList();
        if (k == "dict")
            return ConfigSchema.Coerce(f.Key, ParseDictText(t));
        if (k is "int" or "float" && Py.StripWs(t).Length == 0)
            throw new ConfigValueException($"{f.Label}: không được để trống");
        try
        {
            return ConfigSchema.Coerce(f.Key, Py.StripWs(t));
        }
        catch (ConfigValueException e)
        {
            throw new ConfigValueException(e.Message.Replace($"{f.Key}:", $"{f.Label}:", StringComparison.Ordinal));
        }
    }

    /// <summary><c>langs_from_parts</c>: 12 ô tick + ô "mã khác" → <c>Languages</c> (thứ tự = thứ tự xử lý).</summary>
    public static List<string> LangsFromParts(IReadOnlyDictionary<string, bool> switches, string extraText)
    {
        var outp = Naming.FetchLangSwitches.Where(c => switches.GetValueOrDefault(c)).ToList();
        foreach (var x0 in Py.SplitWs(extraText.Replace(',', ' ')))
        {
            var x = Py.StripWs(x0).ToLowerInvariant();
            var code = Naming.LangFromWord(x) is { Length: > 0 } w ? w : x;
            if (code.Length > 0 && !outp.Contains(code))
                outp.Add(code);
        }
        return outp;
    }

    public static (Dictionary<string, bool> Switches, string Extra) LangsToParts(IReadOnlyList<string> langs)
    {
        var sw = Naming.FetchLangSwitches.ToDictionary(c => c, langs.Contains, StringComparer.Ordinal);
        return (sw, string.Join(" ", langs.Where(c => !Naming.FetchLangSwitches.Contains(c))));
    }

    /// <summary><c>check_connection</c>: nút "Kiểm tra kết nối" (chạy ở luồng phụ). <paramref name="vals"/>: bí mật đang gõ (chưa cần lưu).</summary>
    public static ConnStatus CheckConnection(string which, IReadOnlyDictionary<string, string> vals, Config cfg, Job? job = null,
                                             Http? http = null, Action<double>? sleep = null)
    {
        string V(string k) => vals.GetValueOrDefault(k, "");
        return which switch
        {
            "subsource" => new SubSourceClient(V("SubSourceApiKey"), cfg.Str("SubSourceApiBase"), cfg.Int("ApiPauseMs"), 1, http, sleep, job: job).Check(),
            "tmdb" => new TmdbClient(V("TmdbApiKey"), cfg.Str("TmdbLang"), cfg.Int("TmdbRetry"), http, sleep, job).Check(),
            "tvdb" => new TvdbClient(V("TvdbApiKey"), V("TvdbPin"), cfg.Str("TvdbLang"), cfg.Str("TvdbSeasonType"), cfg.Int("TmdbRetry"), http, sleep,
                                     job).Check(),
            "opensubtitles" => new OpenSubtitlesClient(V("OsApiKey"), V("OsUsername"), V("OsPassword"), cfg.Str("OsUserAgent"), cfg.Int("ApiPauseMs"),
                                                       cfg.Int("OsWarnRemaining"), cfg.Int("OsStopRemaining"), http, sleep, job).Check(),
            "titulky" => new TitulkyClient(V("TitulkyUsername"), V("TitulkyPassword"), cfg.Int("ApiPauseMs"), http, sleep, job).Check(),
            _ => throw new ArgumentException(which, nameof(which)),
        };
    }

    /// <summary><c>tool_status_text</c>: dòng dưới ô đường dẫn công cụ: <c>✓ v82.0 — tự tìm thấy: C:\…\mkvmerge.exe</c>.</summary>
    public static string ToolStatusText(ToolInfo info, bool auto)
    {
        if (info.Path is null)
        {
            var role = Tools.ToolRoles.GetValueOrDefault(info.Name, new ToolRole("", false));
            var tail = role.Required ? " — BẮT BUỘC" : "";
            return $"✗ không tìm thấy{tail} (dùng cho: {role.Role}). Bấm \"Chọn…\" để chỉ tới {Tools.ExeName(info.Name)}.";
        }
        var where = (auto ? "tự tìm thấy: " : "") + info.Path;
        if (!info.Ok)
            return $"✗ {info.Problem} — {where}";
        return $"✓ {(info.Version.Length > 0 ? info.Version + " — " : "")}{where}";
    }

    /// <summary><c>tool_status_color</c> → mức màu: ok / error (bắt buộc) / warn.</summary>
    public static string ToolStatusLevel(ToolInfo info) =>
        info.Ok ? "ok" : Tools.ToolRoles.GetValueOrDefault(info.Name, new ToolRole("", false)).Required ? "error" : "warn";

    /// <summary><c>tool_summary</c>: dòng log sau khi kiểm tra công cụ — một dòng tóm tắt + một dòng cho mỗi công cụ thiếu / hỏng.</summary>
    public static List<(string Level, string Message)> ToolSummary(IReadOnlyDictionary<string, (ToolInfo Info, bool Auto)> res)
    {
        var parts = new List<string>();
        var outp = new List<(string, string)>();
        foreach (var n in Tools.ToolNames)
        {
            var info = res[n].Info;
            parts.Add($"{n} {(info.Ok ? "✓" : "✗")}" + (info.Ok && info.Version.Length > 0 ? $" {info.Version}" : ""));
            if (!info.Ok)
            {
                var role = Tools.ToolRoles.GetValueOrDefault(n, new ToolRole("", false));
                var why = info.Path is null ? "chưa tìm thấy" : info.Problem;
                outp.Add((role.Required ? "error" : "warn",
                          $"[!] {Tools.ExeName(n)}: {why} → không làm được: {role.Role}. Vào Cài đặt › Chung › Công cụ ngoài để chọn đường dẫn."));
            }
        }
        return [(outp.Count == 0 ? "ok" : "warn", "Công cụ: " + string.Join(" · ", parts)), .. outp];
    }

    private static string Show(Field? f, object? v)
    {
        if (f is null)
            return Py.Str(v);
        if (FieldKind(f) == "bool")
            return Py.Truthy(v) ? "bật" : "tắt";
        return ValueToText(f, v).Replace("\n", " | ", StringComparison.Ordinal);
    }

    /// <summary><c>import_rows</c>: dựng bảng xem trước (bí mật luôn được che).</summary>
    public static List<ImportRow> ImportRows(BatImport imp, Config cfg, Func<string, string> currentSecret)
    {
        var rows = new List<ImportRow>();
        string Src(ImportItem it) => string.Create(CultureInfo.InvariantCulture, $"{it.Source}:{it.Line}");
        foreach (var (k, it) in imp.Values)
        {
            var f = ConfigSchema.FieldByKey.GetValueOrDefault(k);
            var cur = cfg[k];
            rows.Add(new ImportRow(k, f?.Label ?? k, Show(f, cur), Show(f, it.Value), Src(it), false, Value: it.Value, Same: Py.Equal(cur, it.Value)));
        }
        foreach (var (k, items) in imp.Conflicts)
        {
            var f = ConfigSchema.FieldByKey.GetValueOrDefault(k);
            var cur = cfg[k];
            foreach (var it in items)
                rows.Add(new ImportRow(k, f?.Label ?? k, Show(f, cur), Show(f, it.Value), Src(it), false, k, it.Value, Py.Equal(cur, it.Value)));
        }
        foreach (var (k, it) in imp.SecretValues)
        {
            var cur = currentSecret(k);
            var lab = SecretInfo.TryGetValue(k, out var si) ? si.Label : k;
            rows.Add(new ImportRow(k, lab, Secrets.Mask(cur) is { Length: > 0 } mc ? mc : "(trống)", Secrets.Mask(Py.Str(it.Value)), Src(it), true,
                                   Value: it.Value, Same: cur == Py.Str(it.Value)));
        }
        foreach (var (k, items) in imp.SecretConflicts)
        {
            var cur = currentSecret(k);
            var lab = SecretInfo.TryGetValue(k, out var si) ? si.Label : k;
            foreach (var it in items)
                rows.Add(new ImportRow(k, lab, Secrets.Mask(cur) is { Length: > 0 } mc ? mc : "(trống)", Secrets.Mask(Py.Str(it.Value)), Src(it), true, k,
                                       it.Value, cur == Py.Str(it.Value)));
        }
        return rows;
    }

    /// <summary><c>filtered_import</c>: bản sao chỉ còn các khoá được tick (bỏ tick = không nhập khoá đó).</summary>
    public static BatImport FilteredImport(BatImport imp, IReadOnlySet<string> keepValues, IReadOnlySet<string> keepSecrets)
    {
        var outp = new BatImport();
        outp.Files.AddRange(imp.Files);
        outp.Ignored.AddRange(imp.Ignored);
        outp.Problems.AddRange(imp.Problems);
        foreach (var (k, v) in imp.Values)
        {
            if (keepValues.Contains(k))
                outp.Values[k] = v;
        }
        foreach (var (k, v) in imp.Conflicts)
            outp.Conflicts[k] = v;
        foreach (var (k, v) in imp.SecretValues)
        {
            if (keepSecrets.Contains(k))
                outp.SecretValues[k] = v;
        }
        foreach (var (k, v) in imp.SecretConflicts)
            outp.SecretConflicts[k] = v;
        return outp;
    }
}
