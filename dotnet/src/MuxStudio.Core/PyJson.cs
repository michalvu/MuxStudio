using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MuxStudio.Core;

/// <summary>
/// Giá trị "kiểu Python" dùng trong cấu hình: <c>null</c>, <c>bool</c>, <c>long</c> (int), <c>double</c> (float),
/// <c>string</c>, danh sách (<see cref="IList"/>), bảng (<see cref="OrderedDictionary{TKey,TValue}"/> giữ thứ tự chèn).
/// </summary>
internal static partial class Py
{
    public static bool IsNumber(object? x) => x is long or int or double or float;

    public static double AsDouble(object x) => x switch
    {
        bool b => b ? 1 : 0,
        long l => l,
        int i => i,
        double d => d,
        float f => f,
        _ => throw new InvalidCastException(),
    };

    /// <summary><c>repr(float)</c> của Python: số ngắn nhất đọc lại đúng; mũ khi số mũ &lt; -4 hoặc ≥ 16; số nguyên thêm ".0".</summary>
    public static string FloatRepr(double d)
    {
        if (double.IsNaN(d))
            return "nan";
        if (double.IsPositiveInfinity(d))
            return "inf";
        if (double.IsNegativeInfinity(d))
            return "-inf";
        var neg = d < 0 || (d == 0 && double.IsNegative(d));
        var r = Math.Abs(d).ToString("R", CultureInfo.InvariantCulture);
        // tách chữ số + vị trí dấu chấm
        var exp = 0;
        var ePos = r.IndexOfAny(['E', 'e']);
        if (ePos >= 0)
        {
            exp = int.Parse(r[(ePos + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            r = r[..ePos];
        }
        var dot = r.IndexOf('.', StringComparison.Ordinal);
        var intPart = dot >= 0 ? r[..dot] : r;
        var fracPart = dot >= 0 ? r[(dot + 1)..] : "";
        var digits = (intPart + fracPart).TrimStart('0');
        var lead = (intPart + fracPart).Length - (intPart + fracPart).TrimStart('0').Length;
        var decpt = intPart.Length + exp - lead; // x = 0.digits × 10^decpt
        digits = digits.TrimEnd('0');
        string body;
        if (digits.Length == 0)
        {
            body = "0.0";
        }
        else
        {
            var e = decpt - 1; // số mũ khoa học
            if (e < -4 || e >= 16)
            {
                var mant = digits.Length > 1 ? digits[0] + "." + digits[1..] : digits;
                body = mant + "e" + (e < 0 ? "-" : "+") + Math.Abs(e).ToString("00", CultureInfo.InvariantCulture);
            }
            else if (decpt <= 0)
            {
                body = "0." + new string('0', -decpt) + digits;
            }
            else if (digits.Length <= decpt)
            {
                body = digits + new string('0', decpt - digits.Length) + ".0";
            }
            else
            {
                body = digits[..decpt] + "." + digits[decpt..];
            }
        }
        return neg ? "-" + body : body;
    }

    /// <summary>Phép so <c>==</c> của Python cho giá trị cấu hình (1 == 1.0, True == 1, so sâu danh sách/bảng).</summary>
    public static bool Equal(object? a, object? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        if ((IsNumber(a) || a is bool) && (IsNumber(b) || b is bool))
        {
            if (a is double or float || b is double or float)
                return AsDouble(a) == AsDouble(b);
            return AsLong(a) == AsLong(b);
        }
        if (a is string sa)
            return b is string sb && string.Equals(sa, sb, StringComparison.Ordinal);
        if (b is string)
            return false;
        if (a is IDictionary da)
        {
            if (b is not IDictionary db || da.Count != db.Count)
                return false;
            foreach (DictionaryEntry kv in da)
            {
                if (!db.Contains(kv.Key) || !Equal(kv.Value, db[kv.Key]))
                    return false;
            }
            return true;
        }
        if (a is IList la)
        {
            if (b is not IList lb || la.Count != lb.Count)
                return false;
            for (var i = 0; i < la.Count; i++)
            {
                if (!Equal(la[i], lb[i]))
                    return false;
            }
            return true;
        }
        return a.Equals(b);
    }

    private static long AsLong(object x) => x switch
    {
        bool b => b ? 1 : 0,
        long l => l,
        int i => i,
        _ => throw new InvalidCastException(),
    };

    /// <summary><c>x in lst</c> theo phép so của Python.</summary>
    public static bool Contains(IList lst, object? x)
    {
        foreach (var y in lst)
        {
            if (Equal(y, x))
                return true;
        }
        return false;
    }

    /// <summary><c>copy.deepcopy</c> cho giá trị cấu hình.</summary>
    public static object? DeepCopy(object? x) => x switch
    {
        List<string> ls => new List<string>(ls),
        OrderedDictionary<string, object?> d => CopyDict(d),
        IList l when x is not string => l.Cast<object?>().Select(DeepCopy).ToList(),
        _ => x,
    };

    private static OrderedDictionary<string, object?> CopyDict(OrderedDictionary<string, object?> d)
    {
        var o = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in d)
            o[k] = DeepCopy(v);
        return o;
    }
}

/// <summary>
/// <c>json.loads</c> / <c>json.dumps</c> của Python, để <c>config.json</c> do bản .NET ghi giống từng byte bản Python
/// (vd số thực <c>1.0</c> ghi là <c>1.0</c>, không phải <c>1</c>; thứ tự khoá giữ nguyên).
/// </summary>
public static class PyJson
{
    private static readonly JsonDocumentOptions DocOptions = new() { MaxDepth = 512 };

    /// <summary>Đọc JSON thành giá trị kiểu Python. Lỗi cú pháp → <see cref="JsonException"/>.
    /// (Khác Python: không nhận <c>NaN</c>/<c>Infinity</c>.)</summary>
    public static object? Loads(string text)
    {
        using var doc = JsonDocument.Parse(text, DocOptions);
        return Convert(doc.RootElement);
    }

    private static object? Convert(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var d = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
                foreach (var p in e.EnumerateObject())
                    d[p.Name] = Convert(p.Value); // khoá trùng: giá trị sau thắng, giữ vị trí đầu (như dict Python)
                return d;
            case JsonValueKind.Array:
                return e.EnumerateArray().Select(Convert).ToList();
            case JsonValueKind.String:
                return e.GetString();
            case JsonValueKind.Number:
                var raw = e.GetRawText();
                if (raw.IndexOfAny(['.', 'e', 'E']) < 0 && long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
                    return l;
                return double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }

    /// <summary><c>json.dumps(obj, ensure_ascii=…, indent=…)</c>. Mặc định như Python: ASCII, một dòng.</summary>
    public static string Dumps(object? obj, int? indent = null, bool ensureAscii = true)
    {
        var sb = new StringBuilder();
        Write(sb, obj, indent, ensureAscii, 0);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, object? o, int? indent, bool ascii, int level)
    {
        switch (o)
        {
            case null:
                sb.Append("null");
                return;
            case bool b:
                sb.Append(b ? "true" : "false");
                return;
            case long or int:
                sb.Append(System.Convert.ToString(o, CultureInfo.InvariantCulture));
                return;
            case double or float:
                var d = Py.AsDouble(o);
                sb.Append(double.IsNaN(d) ? "NaN" : double.IsPositiveInfinity(d) ? "Infinity"
                    : double.IsNegativeInfinity(d) ? "-Infinity" : Py.FloatRepr(d));
                return;
            case string s:
                WriteString(sb, s, ascii);
                return;
            case IDictionary dict:
                if (dict.Count == 0)
                {
                    sb.Append("{}");
                    return;
                }
                sb.Append('{');
                var first = true;
                foreach (DictionaryEntry kv in dict)
                {
                    sb.Append(first ? "" : indent is null ? ", " : ",");
                    first = false;
                    NewLine(sb, indent, level + 1);
                    WriteString(sb, Py.Str(kv.Key), ascii);
                    sb.Append(": ");
                    Write(sb, kv.Value, indent, ascii, level + 1);
                }
                NewLine(sb, indent, level);
                sb.Append('}');
                return;
            case IList list:
                if (list.Count == 0)
                {
                    sb.Append("[]");
                    return;
                }
                sb.Append('[');
                for (var i = 0; i < list.Count; i++)
                {
                    if (i > 0)
                        sb.Append(indent is null ? ", " : ",");
                    NewLine(sb, indent, level + 1);
                    Write(sb, list[i], indent, ascii, level + 1);
                }
                NewLine(sb, indent, level);
                sb.Append(']');
                return;
            default:
                throw new ArgumentException($"Object of type {o.GetType().Name} is not JSON serializable");
        }
    }

    private static void NewLine(StringBuilder sb, int? indent, int level)
    {
        if (indent is { } n)
            sb.Append('\n').Append(' ', n * level);
    }

    private static void WriteString(StringBuilder sb, string s, bool ascii)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20 || (ascii && c > 0x7E))
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
