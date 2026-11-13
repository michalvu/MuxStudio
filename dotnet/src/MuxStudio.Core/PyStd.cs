using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace MuxStudio.Core;

/// <summary>
/// Thêm các hành vi của thư viện chuẩn Python mà <c>tools.py</c> / <c>probe.py</c> dựa vào: <c>os.path</c> (Windows),
/// <c>pathlib</c>, <c>shutil.which</c>, <c>str.splitlines</c>, <c>round(x, n)</c>, <c>int(x)</c>.
/// </summary>
internal static partial class Py
{
    // ------------------------------------------------------------------
    //  os.path / pathlib (ntpath)
    // ------------------------------------------------------------------

    /// <summary><c>os.path.normcase</c> trên Windows: <c>/</c> → <c>\</c> rồi viết thường.</summary>
    public static string NormCase(string p) => p.Replace('/', '\\').ToLowerInvariant();

    /// <summary><c>os.path.abspath</c> (Windows: <c>GetFullPathNameW</c>, nên cũng bỏ dấu cách / dấu chấm cuối tên). Rỗng → thư mục hiện tại.</summary>
    public static string AbsPath(string p) => Path.GetFullPath(p.Length == 0 ? "." : p);

    /// <summary>
    /// <c>str(PureWindowsPath(p))</c>: <c>/</c> → <c>\</c>, gộp dấu tách lặp, bỏ thành phần <c>.</c>, bỏ dấu tách cuối
    /// (trừ gốc <c>C:\</c>, <c>\\máy\chia-sẻ\</c>). Không đụng tới <c>..</c>. Rỗng → <c>.</c>.
    /// </summary>
    public static string PurePath(string p)
    {
        var s = p.Replace('/', '\\');
        string drive = "", rest = s;
        if (s.StartsWith(@"\\", StringComparison.Ordinal) && !s.StartsWith(@"\\\", StringComparison.Ordinal))
        {
            // UNC: \\máy\chia-sẻ là "ổ đĩa"
            var i = s.IndexOf('\\', 2);
            var j = i < 0 ? -1 : s.IndexOf('\\', i + 1);
            drive = j < 0 ? s : s[..j];
            rest = j < 0 ? "" : s[j..];
            if (i < 0 || j < 0)
                return drive.Length == 0 ? "." : drive + (i >= 0 && j < 0 ? "\\" : "");
        }
        else if (s.Length >= 2 && s[1] == ':')
        {
            drive = s[..2];
            rest = s[2..];
        }
        var root = rest.StartsWith('\\') || (drive.StartsWith(@"\\", StringComparison.Ordinal)) ? "\\" : "";
        var parts = rest.Split('\\').Where(x => x.Length > 0 && x != ".");
        var r = drive + root + string.Join('\\', parts);
        return r.Length == 0 ? "." : r;
    }

    /// <summary><c>str(Path(a) / b)</c> với <paramref name="name"/> là một tên file (không phải đường dẫn tuyệt đối).</summary>
    public static string PathJoin(string dir, string name) => PurePath(Join(dir, name));

    /// <summary><c>ntpath.splitroot</c>: (ổ đĩa, gốc, phần còn lại).</summary>
    public static (string Drive, string Root, string Tail) SplitRoot(string p)
    {
        var n = p.Replace('/', '\\');
        if (n.StartsWith('\\'))
        {
            if (n.Length > 1 && n[1] == '\\')
            {
                // UNC \\máy\chia-sẻ, \\?\UNC\máy\chia-sẻ; thiết bị \\.\x, \\?\x
                var start = n.Length >= 8 && n[..8].ToUpperInvariant() == @"\\?\UNC\" ? 8 : 2;
                var i = n.IndexOf('\\', start);
                if (i < 0)
                    return (p, "", "");
                var j = n.IndexOf('\\', i + 1);
                if (j < 0)
                    return (p, "", "");
                return (p[..j], p[j..(j + 1)], p[(j + 1)..]);
            }
            return ("", p[..1], p[1..]);
        }
        if (n.Length > 1 && n[1] == ':')
            return n.Length > 2 && n[2] == '\\' ? (p[..2], p[2..3], p[3..]) : (p[..2], "", p[2..]);
        return ("", "", p);
    }

    /// <summary><c>os.path.join</c> (ntpath). Khác <see cref="Path.Combine(string, string)"/>: <c>join("C:", "x")</c> = <c>C:x</c>,
    /// vế phải có gốc <c>\x</c> giữ ổ đĩa của vế trái.</summary>
    public static string Join(string path, params string[] paths)
    {
        var (rDrive, rRoot, rPath) = SplitRoot(path);
        foreach (var p in paths)
        {
            var (pDrive, pRoot, pPath) = SplitRoot(p);
            if (pRoot.Length > 0)
            {
                if (pDrive.Length > 0 || rDrive.Length == 0)
                    rDrive = pDrive;
                rRoot = pRoot;
                rPath = pPath;
                continue;
            }
            if (pDrive.Length > 0 && pDrive != rDrive)
            {
                if (pDrive.ToLowerInvariant() != rDrive.ToLowerInvariant())
                {
                    rDrive = pDrive;
                    rRoot = pRoot;
                    rPath = pPath;
                    continue;
                }
                rDrive = pDrive;
            }
            if (rPath.Length > 0 && rPath[^1] is not ('\\' or '/'))
                rPath += "\\";
            rPath += pPath;
        }
        if (rPath.Length > 0 && rRoot.Length == 0 && rDrive.Length > 0 && rDrive[^1] is not (':' or '\\' or '/'))
            return rDrive + "\\" + rPath;
        return rDrive + rRoot + rPath;
    }

    /// <summary>
    /// <c>shutil.which(cmd)</c> trên Windows (Python 3.14): tìm trong thư mục hiện tại (trừ khi đặt
    /// <c>NoDefaultCurrentDirectoryInExePath</c>) rồi <c>PATH</c>, thử các đuôi trong <c>PATHEXT</c>.
    /// Trả đường dẫn đúng như ghép (không chuẩn hoá); không thấy → <c>null</c>.
    /// </summary>
    public static string? Which(string cmd)
    {
        var sep = Math.Max(cmd.LastIndexOf('\\'), cmd.LastIndexOf('/'));
        List<string> path;
        if (sep >= 0)
        {
            path = [cmd[..sep].Length == 0 ? cmd[..(sep + 1)] : cmd[..sep]];
            cmd = cmd[(sep + 1)..];
        }
        else
        {
            var env = Environment.GetEnvironmentVariable("PATH") ?? ".;C:\\bin"; // os.defpath
            if (env.Length == 0)
                return null;
            path = [.. env.Split(';')];
            if (Environment.GetEnvironmentVariable("NoDefaultCurrentDirectoryInExePath") is null) // NeedCurrentDirectoryForExePathW
                path.Insert(0, ".");
        }
        var pathextSrc = Environment.GetEnvironmentVariable("PATHEXT") is { Length: > 0 } pe ? pe : ".COM;.EXE;.BAT;.CMD;.VBS;.JS;.WS;.MSC";
        var pathext = pathextSrc.Split(';').Where(e => e.Length > 0).Select(e => e.TrimEnd('.')).ToList();
        var files = pathext.Select(ext => cmd + ext).ToList();
        var normcmd = cmd.ToUpperInvariant();
        if (pathext.Any(ext => normcmd.EndsWith(ext.ToUpperInvariant(), StringComparison.Ordinal)))
            files.Insert(0, cmd);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in path)
        {
            if (!seen.Add(NormCase(dir)))
                continue;
            foreach (var f in files)
            {
                var name = Join(dir, f);
                if (File.Exists(name)) // _access_check: tồn tại và không phải thư mục (X_OK luôn đúng trên Windows)
                    return name;
            }
        }
        return null;
    }

    // ------------------------------------------------------------------
    //  str
    // ------------------------------------------------------------------

    /// <summary><c>str.splitlines()</c>: tách theo \n, \r\n, \r, \v, \f, \x1c–\x1e, \x85, \u2028, \u2029; không có phần tử rỗng cuối.</summary>
    public static List<string> SplitLines(string s)
    {
        var o = new List<string>();
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\u2028' or '\u2029')
            {
                o.Add(s[start..i]);
                if (c == '\r' && i + 1 < s.Length && s[i + 1] == '\n')
                    i++;
                start = i + 1;
            }
        }
        if (start < s.Length)
            o.Add(s[start..]);
        return o;
    }

    /// <summary><c>str.strip()</c>: như <c>Trim()</c> nhưng bỏ cả \x1c–\x1f (Python coi là khoảng trắng, .NET thì không).</summary>
    public static string StripWs(string s)
    {
        static bool Ws(char c) => char.IsWhiteSpace(c) || c is >= '\x1c' and <= '\x1f';
        int a = 0, b = s.Length;
        while (a < b && Ws(s[a]))
            a++;
        while (b > a && Ws(s[b - 1]))
            b--;
        return a == 0 && b == s.Length ? s : s[a..b];
    }

    /// <summary><c>str.split()</c> (không tham số): tách theo dãy khoảng trắng (kể cả \x1c–\x1f), bỏ phần tử rỗng.</summary>
    public static List<string> SplitWs(string s)
    {
        static bool Ws(char c) => char.IsWhiteSpace(c) || c is >= '\x1c' and <= '\x1f';
        var o = new List<string>();
        var i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && Ws(s[i]))
                i++;
            var start = i;
            while (i < s.Length && !Ws(s[i]))
                i++;
            if (i > start)
                o.Add(s[start..i]);
        }
        return o;
    }

    // ------------------------------------------------------------------
    //  số
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>round(x, n)</c> của Python cho số thực: làm tròn về số CHẴN trên giá trị nhị phân CHÍNH XÁC của <paramref name="x"/>
    /// (vd <c>round(0.0625, 3)</c> = 0.062, <c>round(1.0005, 3)</c> = 1.0 vì 1.0005 thật ra nhỏ hơn một chút).
    /// <c>Math.Round(x, n)</c> nhân với 10ⁿ trước nên có thể lệch ở các ca này.
    /// </summary>
    public static double Round(double x, int ndigits)
    {
        if (!double.IsFinite(x) || x == 0 || ndigits > 323)
            return x;
        if (ndigits < -308)
            return 0.0 * x;
        var bits = BitConverter.DoubleToInt64Bits(x);
        var neg = bits < 0;
        var exp = (int)((bits >> 52) & 0x7FF);
        var man = bits & 0xFFFFFFFFFFFFFL;
        if (exp == 0)
            exp = 1;
        else
            man |= 1L << 52;
        exp -= 1075; // |x| = man × 2^exp
        BigInteger num = man, den = BigInteger.One;
        if (exp > 0)
            num <<= exp;
        else
            den <<= -exp;
        if (ndigits >= 0)
            num *= BigInteger.Pow(10, ndigits);
        else
            den *= BigInteger.Pow(10, -ndigits);
        var q = BigInteger.DivRem(num, den, out var rem);
        var cmp = (rem * 2).CompareTo(den);
        if (cmp > 0 || (cmp == 0 && !q.IsEven))
            q += 1;
        var text = q.ToString(CultureInfo.InvariantCulture) + "E" + (-ndigits).ToString(CultureInfo.InvariantCulture);
        var r = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture); // đọc lại: làm tròn đúng như strtod
        return neg ? -r : r;
    }

    /// <summary><c>f"{x:.{d}f}"</c> / <c>f"{x:+.{d}f}"</c>: làm tròn đúng như Python (về số chẵn trên giá trị nhị phân chính xác),
    /// giữ dấu của -0.0 (Python in <c>-0.00</c>).</summary>
    public static string Fixed(double x, int d, bool plus = false)
    {
        var r = Round(x, d);
        var body = Math.Abs(r).ToString("F" + d.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return double.IsNegative(r) ? "-" + body : (plus ? "+" : "") + body;
    }

    /// <summary>
    /// <c>int(x)</c>: bool → 0/1, số thực → cắt phần lẻ (NaN/vô cực → lỗi như Python), chuỗi → số nguyên (bỏ khoảng trắng,
    /// có dấu, chữ số Unicode). Kiểu khác (vd <c>None</c>) → lỗi.
    /// </summary>
    public static int ToInt(object? v) => v switch
    {
        bool b => b ? 1 : 0,
        int i => i,
        long l => checked((int)l),
        double d => TruncInt(d),
        float f => TruncInt(f),
        string s => checked((int)ParseIntLiteral(s)),
        _ => throw new InvalidCastException($"int() argument must be a string or a real number, not '{TypeName(v)}'"),
    };

    private static int TruncInt(double d)
    {
        if (double.IsNaN(d))
            throw new FormatException("cannot convert float NaN to integer");
        if (double.IsInfinity(d))
            throw new OverflowException("cannot convert float infinity to integer");
        return checked((int)Math.Truncate(d));
    }

    private static long ParseIntLiteral(string s)
    {
        var t = s.Trim();
        if (t.StartsWith('+'))
            t = t[1..];
        if (t.Length == 0 || t == "-")
            throw new FormatException($"invalid literal for int() with base 10: '{s}'");
        return Long(t);
    }

    private static string TypeName(object? v) => v switch
    {
        null => "NoneType",
        System.Collections.IDictionary => "dict",
        System.Collections.IList => "list",
        _ => v.GetType().Name,
    };

    // ------------------------------------------------------------------
    //  Windows: symlink thư mục (os.walk không đi vào symlink, nhưng CÓ đi vào junction)
    // ------------------------------------------------------------------

    private const uint IoReparseTagSymlink = 0xA000000C;

    /// <summary><c>os.path.islink</c> trên Windows: chỉ symlink (thẻ reparse <c>IO_REPARSE_TAG_SYMLINK</c>), junction → false.</summary>
    public static bool IsLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
        var h = FindFirstFileExW(path.TrimEnd('\\', '/'), 1 /* FindExInfoBasic */, out var data, 0, 0, 0);
        if (h == -1)
            return false;
        FindClose(h);
        return (data.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0 && data.Reserved0 == IoReparseTagSymlink;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Win32FindData
    {
        public uint FileAttributes;
        public fixed uint Times[6]; // 3 × FILETIME
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint Reserved0; // thẻ reparse
        public uint Reserved1;
        public fixed char FileName[260];
        public fixed char AlternateFileName[14];
    }

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstFileExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint FindFirstFileExW(string fileName, int infoLevel, out Win32FindData data, int searchOp, nint filter, int flags);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FindClose(nint handle);

    /// <summary>UTF-8, byte sai → U+FFFD (= <c>decode("utf-8", errors="replace")</c>); không bỏ BOM.</summary>
    public static readonly UTF8Encoding Utf8Replace = new(false, false);
}
