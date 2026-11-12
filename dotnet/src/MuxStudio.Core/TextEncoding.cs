using System.Text;
using System.Text.Unicode;

namespace MuxStudio.Core;

/// <summary>Kiểu mã hoá của file phụ đề (giá trị chuỗi giữ y như bản Python vì còn hiện trong nhật ký).</summary>
public static class EncodingKind
{
    public const string Empty = "empty";
    public const string Utf8Bom = "utf8bom";
    public const string Utf16Le = "utf16le";
    public const string Utf16Be = "utf16be";
    public const string Utf8 = "utf8";
    public const string Legacy = "legacy";
    public const string Error = "error";
}

/// <summary>
/// Mã hoá ký tự của phụ đề. Port <c>core/encoding.py</c>.
/// Thay <c>Get-SubEncodingKind</c> (SubPrep = MuxTool), <c>Get-LegacyCodePage</c> + <c>ConvertTo-Utf8Sub</c> (SubPrep),
/// <c>Get-SubCharsetArg</c> (MuxTool). Khác .bat: <see cref="ToUtf8"/> ghi ra file MỚI, không ghi đè file nguồn (TĐ-12).
/// </summary>
public static class TextEncoding
{
    static TextEncoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Gọi một lần lúc khởi động để các bảng mã cũ (cp1250, cp1258, cp932…) dùng được ở mọi nơi.</summary>
    public static void EnsureCodePages() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static readonly Encoding Utf8Replace = new UTF8Encoding(false, false);
    private static readonly Encoding Utf16LeReplace = new UnicodeEncoding(false, false, false);
    private static readonly Encoding Utf16BeReplace = new UnicodeEncoding(true, false, false);
    private static readonly Encoding Utf32LeReplace = new UTF32Encoding(false, false, false);
    private static readonly Encoding Utf32BeReplace = new UTF32Encoding(true, false, false);

    /// <summary>Kiểu mã hoá (<c>Get-SubEncodingKind</c>): BOM UTF-8/UTF-16 → UTF-8 nghiêm ngặt → <c>legacy</c>.</summary>
    public static string DetectKindBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return EncodingKind.Empty;
        if (data.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            return EncodingKind.Utf8Bom;
        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
            return EncodingKind.Utf16Le;
        if (data.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
            return EncodingKind.Utf16Be;
        return Utf8.IsValid(data) ? EncodingKind.Utf8 : EncodingKind.Legacy;
    }

    public static string DetectKind(string path)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return EncodingKind.Error;
        }
        return DetectKindBytes(data);
    }

    /// <summary>Bảng mã cũ theo ngôn ngữ (<c>Get-LegacyCodePage</c>): vi 1258, cs 1250, ja 932… mặc định 1252.</summary>
    public static int LegacyCodepage(string? lang)
    {
        var l = (lang ?? "").TrimStart('.');
        var w = Naming.LangFromWord(l);
        var code = w.Length > 0 ? w : l.ToLowerInvariant();
        return Naming.GetLang(code)?.Codepage ?? 1252;
    }

    /// <summary>Bảng mã cũ; byte không có trong bảng → ký tự cùng mã (bộ xử lý lỗi <c>muxstudio_c1</c>).</summary>
    private static Encoding LegacyEncoding(int cp) =>
        Encoding.GetEncoding(cp, EncoderFallback.ReplacementFallback, C1PassthroughDecoderFallback.Instance);

    /// <summary>
    /// Giải mã nội dung phụ đề. Trả (văn bản, kiểu ban đầu) — kiểu: <c>utf8</c>, <c>utf8bom</c>, <c>utf16le</c>,
    /// <c>utf16be</c>, <c>cp1258</c>… (<c>legacy</c> được giải bằng bảng mã cũ theo ngôn ngữ).
    /// </summary>
    public static (string Text, string How) DecodeBytes(byte[] data, string? lang = null)
    {
        var kind = DetectKindBytes(data);
        switch (kind)
        {
            case EncodingKind.Empty:
                return ("", EncodingKind.Empty);
            case EncodingKind.Utf8Bom:
                return (Utf8Replace.GetString(data, 3, data.Length - 3), kind);
            case EncodingKind.Utf16Le:
                return (Utf16LeReplace.GetString(data, 2, data.Length - 2), kind);
            case EncodingKind.Utf16Be:
                return (Utf16BeReplace.GetString(data, 2, data.Length - 2), kind);
            case EncodingKind.Utf8:
                return (Py.Utf8Strict.GetString(data), kind);
        }
        var cp = LegacyCodepage(lang);
        Encoding enc;
        try
        {
            enc = LegacyEncoding(cp);
        }
        catch (Exception e) when (e is NotSupportedException or ArgumentException)
        {
            return (DecodeLegacy(LegacyEncoding(1252), data), "cp1252");
        }
        return (DecodeLegacy(enc, data), $"cp{cp}");
    }

    /// <summary>
    /// Giải bằng bảng mã cũ. Với bảng mã MỘT byte, Windows/.NET ánh xạ các byte "không định nghĩa" (vd cp874 0xDB, 0xFF)
    /// sang vùng ký tự riêng U+E000–U+F8FF, còn Python coi là lỗi → <c>muxstudio_c1</c> giữ nguyên mã byte.
    /// Bảng mã một byte của Python không có ký tự vùng riêng hợp lệ nào, nên đổi lại về mã byte cho giống hệt.
    /// </summary>
    internal static string DecodeLegacy(Encoding enc, byte[] data)
    {
        var s = enc.GetString(data);
        if (!enc.IsSingleByte || s.Length != data.Length)
            return s;
        char[]? buf = null;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] is >= '' and <= '')
            {
                buf ??= s.ToCharArray();
                buf[i] = (char)data[i];
            }
        }
        return buf is null ? s : new string(buf);
    }

    /// <summary>
    /// Đọc văn bản như <c>[IO.File]::ReadAllText</c>: theo BOM nếu có, còn lại UTF-8 (byte hỏng → U+FFFD).
    /// Dùng để đọc mốc thời gian (chỉ là ký tự ASCII) và kiểm tra ký tự hỏng.
    /// </summary>
    public static string ReadText(string path)
    {
        var data = File.ReadAllBytes(path);
        ReadOnlySpan<byte> d = data;
        if (d.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            return Utf8Replace.GetString(data, 3, data.Length - 3);
        if (d.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0x00, 0x00]))
            return Utf32LeReplace.GetString(data, 4, data.Length - 4);
        if (d.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0xFE, 0xFF]))
            return Utf32BeReplace.GetString(data, 4, data.Length - 4);
        if (d.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
            return Utf16LeReplace.GetString(data, 2, data.Length - 2);
        if (d.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
            return Utf16BeReplace.GetString(data, 2, data.Length - 2);
        return Utf8Replace.GetString(data);
    }

    /// <summary>Ghi UTF-8 KHÔNG BOM, giữ nguyên ký tự xuống dòng có trong <paramref name="text"/>.</summary>
    public static void WriteTextUtf8(string path, string text) => File.WriteAllText(path, text, Py.Utf8NoBom);

    /// <summary>
    /// Chuyển phụ đề về UTF-8 không BOM, GHI RA <paramref name="dst"/> (<c>ConvertTo-Utf8Sub</c>, không ghi đè nguồn).
    /// Trả kiểu ban đầu: <c>utf8</c> (đã đúng, chỉ chép), <c>empty</c>, <c>error</c> (không làm gì),
    /// hoặc <c>utf8bom</c>/<c>utf16le</c>/<c>utf16be</c>/<c>cp1258</c>… (đã chuyển).
    /// </summary>
    public static string ToUtf8(string src, string dst, string? lang = null)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(src);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return EncodingKind.Error;
        }
        var kind = DetectKindBytes(data);
        var same = string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.Ordinal);
        if (kind is EncodingKind.Utf8 or EncodingKind.Empty)
        {
            if (!same)
                File.Copy(src, dst, overwrite: true);
            return kind;
        }
        var (text, how) = DecodeBytes(data, lang);
        try
        {
            WriteTextUtf8(dst, text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return EncodingKind.Error;
        }
        return how;
    }

    /// <summary>Tên bảng mã cho <c>mkvmerge --sub-charset 0:&lt;...&gt;</c>; '' nếu file đã là UTF-8 (<c>Get-SubCharsetArg</c>).</summary>
    public static string MkvmergeCharset(string path, string? lang)
    {
        var kind = DetectKind(path);
        if (kind is EncodingKind.Utf8 or EncodingKind.Utf8Bom or EncodingKind.Empty or EncodingKind.Error)
            return "";
        if (kind == EncodingKind.Utf16Le)
            return "UTF-16LE";
        if (kind == EncodingKind.Utf16Be)
            return "UTF-16BE";
        var w = Naming.LangFromWord((lang ?? "").TrimStart('.'));
        var code = w.Length > 0 ? w : (lang ?? "").ToLowerInvariant();
        return Naming.GetLang(code)?.Charset ?? "windows-1252";
    }
}

/// <summary>
/// Byte không có trong bảng mã cũ → ký tự cùng mã (như .NET/Windows làm với cp1252: 0x81 → U+0081).
/// Tương đương bộ xử lý lỗi <c>muxstudio_c1</c> của bản Python.
/// </summary>
internal sealed class C1PassthroughDecoderFallback : DecoderFallback
{
    public static readonly C1PassthroughDecoderFallback Instance = new();

    public override int MaxCharCount => 4;

    public override DecoderFallbackBuffer CreateFallbackBuffer() => new Buffer();

    private sealed class Buffer : DecoderFallbackBuffer
    {
        private byte[] _bytes = [];
        private int _pos;

        public override bool Fallback(byte[] bytesUnknown, int index)
        {
            _bytes = bytesUnknown;
            _pos = 0;
            return true;
        }

        public override char GetNextChar() => _pos < _bytes.Length ? (char)_bytes[_pos++] : '\0';

        public override bool MovePrevious()
        {
            if (_pos == 0)
                return false;
            _pos--;
            return true;
        }

        public override int Remaining => _bytes.Length - _pos;

        public override void Reset()
        {
            _bytes = [];
            _pos = 0;
        }
    }
}
