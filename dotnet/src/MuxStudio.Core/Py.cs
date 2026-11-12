using System.Globalization;
using System.Text;

namespace MuxStudio.Core;

/// <summary>
/// Vài hành vi của thư viện chuẩn Python mà bản port cần giữ y nguyên (khác mặc định của .NET).
/// </summary>
internal static partial class Py
{
    /// <summary><c>int(s)</c> cho chuỗi chữ số (khớp <c>\d</c> của regex): đọc được cả chữ số Unicode như Python.</summary>
    public static int Int(string digits)
    {
        long v = 0;
        foreach (var ch in digits)
        {
            var d = CharUnicodeInfo.GetDecimalDigitValue(ch);
            if (d < 0)
                throw new FormatException($"invalid literal for int(): '{digits}'");
            v = checked(v * 10 + d);
        }
        return checked((int)v);
    }

    /// <summary><c>int(s)</c> cho <c>-?\d+</c> (chữ số Unicode như Python), kết quả 64 bit.</summary>
    public static long Long(string s)
    {
        var neg = s.StartsWith('-');
        long v = 0;
        foreach (var ch in neg ? s[1..] : s)
        {
            var d = CharUnicodeInfo.GetDecimalDigitValue(ch);
            if (d < 0)
                throw new FormatException($"invalid literal for int(): '{s}'");
            v = checked(v * 10 + d);
        }
        return neg ? -v : v;
    }

    /// <summary>Đổi chữ số Unicode (khớp <c>\d</c>) về chữ số ASCII để <c>double.Parse</c> đọc được như <c>float()</c>.</summary>
    public static string AsciiDigits(string s)
    {
        StringBuilder? sb = null;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c > 0x7F && CharUnicodeInfo.GetDecimalDigitValue(c) is var d and >= 0)
            {
                sb ??= new StringBuilder(s[..i]);
                sb.Append((char)('0' + d));
            }
            else
            {
                sb?.Append(c);
            }
        }
        return sb?.ToString() ?? s;
    }

    /// <summary>Cắt chuỗi kiểu Python <c>s[a:b]</c> (chỉ số không âm, tự kẹp vào độ dài).</summary>
    public static string Slice(string s, int a, int b)
    {
        a = Math.Clamp(a, 0, s.Length);
        b = Math.Clamp(b, 0, s.Length);
        return b > a ? s[a..b] : "";
    }

    /// <summary><c>str.isalnum()</c> cho một ký tự: chữ (L*) hoặc mọi loại số (Nd, Nl, No — vd ①, ²), khác <c>char.IsLetterOrDigit</c>.</summary>
    public static bool IsAlnum(char c) => char.GetUnicodeCategory(c) switch
    {
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
            or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber => true,
        _ => false,
    };

    /// <summary><c>str.strip(chars)</c>.</summary>
    public static string Strip(string s, string chars) => s.Trim(chars.ToCharArray());

    /// <summary><c>str.isdigit()</c> (gần đúng: chữ số thập phân Unicode), chuỗi rỗng → false.</summary>
    public static bool IsDigit(string s)
    {
        if (s.Length == 0)
            return false;
        foreach (var ch in s)
        {
            if (!char.IsDigit(ch))
                return false;
        }
        return true;
    }

    /// <summary><c>os.path.basename</c> (ntpath: tách theo cả <c>\</c> và <c>/</c>, bỏ ổ đĩa).</summary>
    public static string Basename(string p)
    {
        var i = Math.Max(p.LastIndexOf('\\'), p.LastIndexOf('/'));
        var rest = i >= 0 ? p[(i + 1)..] : p;
        if (i < 0 && rest.Length >= 2 && rest[1] == ':')
            rest = rest[2..];
        return rest;
    }

    /// <summary><c>os.path.splitext</c>: (gốc, đuôi). Dấu chấm ở đầu tên (".bashrc") không tính là đuôi.</summary>
    public static (string Root, string Ext) SplitExt(string p)
    {
        var sep = Math.Max(p.LastIndexOf('\\'), p.LastIndexOf('/'));
        var dot = p.LastIndexOf('.');
        if (dot > sep)
        {
            // bỏ qua các dấu chấm ở đầu tên file
            var fileStart = sep + 1;
            while (fileStart < dot)
            {
                if (p[fileStart] != '.')
                    return (p[..dot], p[dot..]);
                fileStart++;
            }
        }
        return (p, "");
    }

    /// <summary><c>str(x)</c> cho số/chuỗi (định dạng bất biến).</summary>
    public static string Str(object? x) => x switch
    {
        null => "None",
        string s => s,
        bool b => b ? "True" : "False",
        double d => FloatRepr(d),
        float f1 => FloatRepr(f1),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => x.ToString() ?? "",
    };

    /// <summary>Giá trị "đúng" theo Python (<c>bool(x)</c>) cho các kiểu hay gặp.</summary>
    public static bool Truthy(object? x) => x switch
    {
        null => false,
        bool b => b,
        string s => s.Length > 0,
        int i => i != 0,
        long l => l != 0,
        double d => d != 0.0,
        System.Collections.ICollection c => c.Count > 0,
        _ => true,
    };

    /// <summary>UTF-8 không BOM (= <c>"utf-8"</c> của Python khi ghi).</summary>
    public static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>UTF-8 nghiêm ngặt: byte sai → ngoại lệ (= <c>errors="strict"</c>).</summary>
    public static readonly UTF8Encoding Utf8Strict = new(false, true);
}
