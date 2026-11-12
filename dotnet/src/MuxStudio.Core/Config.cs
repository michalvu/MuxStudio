using System.Collections;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MuxStudio.Core;

/// <summary>Một khoá cấu hình.</summary>
/// <param name="Group">chung | api | fetch | prep | mux</param>
/// <param name="Kind">bool int float str dir file list dict choice langs (rỗng = theo kiểu mặc định)</param>
public sealed record Field(string Key, object Default, string Group, string Label, string Help = "", string Kind = "",
    IReadOnlyList<string>? Choices = null, bool Advanced = false)
{
    public IReadOnlyList<string> ChoiceList => Choices ?? [];
}

/// <summary>Giá trị cấu hình sai kiểu (= <c>ValueError</c> của <c>coerce</c>).</summary>
public sealed class ConfigValueException(string message) : Exception(message);

/// <summary>Bảng khoá + ép kiểu (phần đầu của <c>core/config.py</c>). Bảng <see cref="Fields"/> sinh tự động trong ConfigFields.cs.</summary>
public static partial class ConfigSchema
{
    public const int ConfigVersion = 1;

    // Lazy: thứ tự khởi tạo trường tĩnh giữa hai file của partial class (ConfigFields.cs) không được bảo đảm.
    private static readonly Lazy<IReadOnlyDictionary<string, Field>> FieldByKeyLazy =
        new(() => Fields.ToDictionary(f => f.Key, StringComparer.Ordinal));

    private static readonly Lazy<OrderedDictionary<string, object?>> DefaultsLazy = new(BuildDefaults);

    public static IReadOnlyDictionary<string, Field> FieldByKey => FieldByKeyLazy.Value;

    /// <summary>Giá trị mặc định (KHÔNG sửa trực tiếp — dùng <see cref="Py.DeepCopy"/> khi cần bản riêng).</summary>
    public static OrderedDictionary<string, object?> Defaults => DefaultsLazy.Value;

    private static OrderedDictionary<string, object?> BuildDefaults()
    {
        var d = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var f in Fields)
            d[f.Key] = f.Default;
        return d;
    }

    public static object? DefaultOf(string key) => Py.DeepCopy(Defaults[key]);

    [GeneratedRegex(@"^-?\d+\z")]
    private static partial Regex IntTextRe();

    /// <summary>Ép <paramref name="value"/> về đúng kiểu của khoá. Sai kiểu → <see cref="ConfigValueException"/>.</summary>
    public static object? Coerce(string key, object? value)
    {
        if (!FieldByKey.TryGetValue(key, out var f))
            return value;
        switch (f.Default)
        {
            case bool:
                if (value is bool b)
                    return b;
                if (value is string bs && bs.Trim().ToLowerInvariant() is "true" or "false" or "1" or "0")
                    return bs.Trim().ToLowerInvariant() is "true" or "1";
                throw new ConfigValueException($"{key}: cần True/False");
            case long:
                if (value is bool)
                    throw new ConfigValueException($"{key}: cần số nguyên");
                if (value is long l)
                    return l;
                if (value is int i)
                    return (long)i;
                if (value is double d && double.IsFinite(d) && Math.Floor(d) == d && Math.Abs(d) < 9.2e18)
                    return (long)d;
                if (value is string s && IntTextRe().IsMatch(s.Trim()))
                    return Py.Long(s.Trim());
                throw new ConfigValueException($"{key}: cần số nguyên");
            case double:
                if (value is bool)
                    throw new ConfigValueException($"{key}: cần số");
                if (Py.IsNumber(value))
                    return Py.AsDouble(value!);
                if (value is string fs && Timing.TryParsePyFloat(fs.Trim().Replace(',', '.'), out var fv))
                    return fv;
                throw new ConfigValueException($"{key}: cần số");
            case string:
                if (value is string or long or int or double && value is not bool)
                {
                    var v = Py.Str(value);
                    if (f.ChoiceList.Count > 0 && !f.ChoiceList.Contains(v))
                        throw new ConfigValueException($"{key}: chỉ nhận {string.Join(", ", f.ChoiceList)}");
                    return v;
                }
                throw new ConfigValueException($"{key}: cần chữ");
            case List<string>:
                if (value is string ls)
                    return ls.Length > 0 ? new List<string> { ls } : new List<string>();
                if (value is IList list)
                    return list.Cast<object?>().Select(Py.Str).ToList();
                throw new ConfigValueException($"{key}: cần danh sách");
            case IDictionary:
                if (value is IDictionary dict)
                {
                    var o = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
                    foreach (DictionaryEntry kv in dict)
                    {
                        if (kv.Value is bool || !Py.IsNumber(kv.Value))
                            throw new ConfigValueException($"{key}: giá trị '{Py.Str(kv.Key)}' cần là số");
                        o[Py.Str(kv.Key)] = kv.Value is int iv ? (long)iv : kv.Value;
                    }
                    return o;
                }
                throw new ConfigValueException($"{key}: cần bảng khoá → giá trị");
            default:
                return value;
        }
    }

    // ------------------------------------------------------------------
    //  ĐỌC / GHI config.json
    // ------------------------------------------------------------------

    /// <summary><c>%APPDATA%\MuxStudio</c> (biến <c>MUXSTUDIO_CONFIG_DIR</c> ghi đè, dùng cho test).</summary>
    public static string AppConfigDir()
    {
        var env = Environment.GetEnvironmentVariable("MUXSTUDIO_CONFIG_DIR");
        if (!string.IsNullOrEmpty(env))
            return env;
        var appdata = Environment.GetEnvironmentVariable("APPDATA");
        if (!string.IsNullOrEmpty(appdata))
            return Path.Combine(appdata, "MuxStudio");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "MuxStudio");
    }

    public static string ConfigPath() => Path.Combine(AppConfigDir(), "config.json");

    public static Config LoadConfig(string? path = null)
    {
        var p = path ?? ConfigPath();
        var cfg = new Config(path: p);
        if (!File.Exists(p) && !Directory.Exists(p))
            return cfg;
        var name = Path.GetFileName(p);
        object? raw;
        try
        {
            var bytes = File.ReadAllBytes(p);
            var start = bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? 3 : 0; // utf-8-sig
            raw = PyJson.Loads(Py.Utf8Strict.GetString(bytes, start, bytes.Length - start));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException)
        {
            cfg.Warnings.Add($"Không đọc được {name} ({e.Message}) → dùng cấu hình mặc định.");
            return cfg;
        }
        if (raw is not OrderedDictionary<string, object?> obj)
        {
            cfg.Warnings.Add($"{name} không đúng định dạng → dùng cấu hình mặc định.");
            return cfg;
        }
        foreach (var (k, v) in obj)
        {
            if (k == "_version")
                continue;
            if (Secrets.SecretKeys.Contains(k))
            {
                cfg.Warnings.Add($"Bỏ '{k}' trong {name}: bí mật chỉ lưu trong Windows Credential Manager.");
                continue;
            }
            try
            {
                cfg[k] = v;
            }
            catch (ConfigValueException e)
            {
                cfg.Warnings.Add($"{e.Message} → dùng giá trị mặc định.");
            }
        }
        foreach (var c in cfg.List("Languages"))
            Naming.RegisterLanguage(c);
        return cfg;
    }

    /// <summary>Ghi <c>config.json</c>: UTF-8 không BOM, xuống dòng LF, thụt 2, giữ tiếng Việt; ghi nguyên tử (file tạm + thay thế).</summary>
    public static string SaveConfig(Config cfg, string? path = null)
    {
        var p = path ?? cfg.Path ?? ConfigPath();
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(p))!;
        Directory.CreateDirectory(dir);
        var data = PyJson.Dumps(cfg.ToJsonDict(), indent: 2, ensureAscii: false);
        var tmp = System.IO.Path.Combine(dir, ".config." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            File.WriteAllText(tmp, data, Py.Utf8NoBom);
            File.Move(tmp, p, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(tmp);
            }
            catch (IOException)
            {
            }
            throw;
        }
        cfg.Path = p;
        return p;
    }
}

/// <summary>Cấu hình đang dùng. Khoá thiếu → giá trị mặc định. Không chứa bí mật. Khoá lạ được giữ nguyên khi ghi lại.</summary>
public sealed class Config : IEnumerable<string>
{
    private OrderedDictionary<string, object?> _data = new(StringComparer.Ordinal);

    public Config(IEnumerable<KeyValuePair<string, object?>>? data = null, string? path = null)
    {
        Path = path;
        foreach (var (k, v) in data ?? [])
            this[k] = v;
    }

    public string? Path { get; set; }

    public List<string> Warnings { get; } = [];

    /// <summary>Giá trị của khoá (bản sao của mặc định nếu chưa đặt). Đặt: ép kiểu theo bảng khoá; bí mật → lỗi.</summary>
    public object? this[string key]
    {
        get
        {
            if (_data.TryGetValue(key, out var v))
                return v;
            if (ConfigSchema.Defaults.ContainsKey(key))
                return ConfigSchema.DefaultOf(key);
            throw new KeyNotFoundException(key);
        }
        set
        {
            if (Secrets.SecretKeys.Contains(key))
                throw new KeyNotFoundException($"{key} là bí mật — lưu bằng core.secrets, không lưu trong config.json");
            _data[key] = ConfigSchema.Coerce(key, value);
        }
    }

    public bool ContainsKey(string key) => _data.ContainsKey(key) || ConfigSchema.Defaults.ContainsKey(key);

    public bool Remove(string key) => _data.Remove(key);

    public int Count => ConfigSchema.Defaults.Keys.Union(_data.Keys).Count();

    /// <summary>Khoá mặc định (đúng thứ tự bảng) rồi tới khoá lạ.</summary>
    public IEnumerator<string> GetEnumerator()
    {
        foreach (var k in ConfigSchema.Defaults.Keys)
            yield return k;
        foreach (var k in _data.Keys)
        {
            if (!ConfigSchema.Defaults.ContainsKey(k))
                yield return k;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // ---- truy cập có kiểu (C#) ----
    public bool Bool(string key) => this[key] is true;

    public long Long(string key) => this[key] is long l ? l : 0;

    public int Int(string key) => (int)Math.Clamp(Long(key), int.MinValue, int.MaxValue);

    public double Double(string key) => this[key] is { } v && Py.IsNumber(v) ? Py.AsDouble(v) : 0.0;

    public string Str(string key) => this[key] as string ?? "";

    public List<string> List(string key) => this[key] is IList l ? l.Cast<object?>().Select(Py.Str).ToList() : [];

    public OrderedDictionary<string, object?> Dict(string key) =>
        this[key] as OrderedDictionary<string, object?> ?? new OrderedDictionary<string, object?>(StringComparer.Ordinal);

    public void ResetDefaults(IEnumerable<string>? keys = null)
    {
        if (keys is null)
        {
            var extra = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (k, v) in _data)
            {
                if (!ConfigSchema.Defaults.ContainsKey(k))
                    extra[k] = v;
            }
            _data = extra;
        }
        else
        {
            foreach (var k in keys)
                _data.Remove(k);
        }
    }

    public OrderedDictionary<string, object?> ToJsonDict()
    {
        var o = new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["_version"] = (long)ConfigSchema.ConfigVersion };
        foreach (var k in this)
        {
            if (Secrets.SecretKeys.Contains(k) || k == "_version")
                continue;
            o[k] = this[k];
        }
        return o;
    }
}
