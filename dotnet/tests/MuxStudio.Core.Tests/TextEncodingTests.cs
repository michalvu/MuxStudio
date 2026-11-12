using System.Text;
using MuxStudio.Core.Tests.Support;

namespace MuxStudio.Core.Tests;

/// <summary>§6.3 — Chuẩn hoá phụ đề về UTF-8 không BOM; §6.2 — --sub-charset. Port <c>tests/test_encoding.py</c>.</summary>
public class TextEncodingTests
{
    static TextEncodingTests() => TextEncoding.EnsureCodePages();

    /// <summary>"Xin chào Việt Nam" (ê + dấu nặng tổ hợp).</summary>
    private static readonly byte[] ViCp1258 = [.. "Xin ch"u8, 0xE0, .. "o Vi"u8, 0xEA, 0xF2, .. "t Nam"u8];

    private static byte[] CsCp1250 => Encoding.GetEncoding(1250).GetBytes("Příliš žluťoučký kůň");

    private static byte[] B(params int[] xs) => xs.Select(x => (byte)x).ToArray();

    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    public static TheoryData<byte[], string> DetectCases() => new()
    {
        { [], "empty" },
        { Cat(B(0xEF, 0xBB, 0xBF), "abc"u8.ToArray()), "utf8bom" },
        { B(0xFF, 0xFE, 'a', 0), "utf16le" },
        { B(0xFE, 0xFF, 0, 'a'), "utf16be" },
        { Encoding.UTF8.GetBytes("Tiếng Việt"), "utf8" },
        { ViCp1258, "legacy" },
        { CsCp1250, "legacy" },
    };

    [Theory]
    [MemberData(nameof(DetectCases))]
    public void Detect_kind(byte[] data, string kind) => Assert.Equal(kind, TextEncoding.DetectKindBytes(data));

    [Fact]
    public void Detect_kind_missing_file()
    {
        using var tmp = new TempDir();
        Assert.Equal("error", TextEncoding.DetectKind(tmp["khong-co.srt"]));
    }

    [Theory]
    [InlineData("vi", 1258)]
    [InlineData(".vi", 1258)]
    [InlineData("cs", 1250)]
    [InlineData("cz", 1250)]
    [InlineData("ja", 932)]
    [InlineData("ko", 949)]
    [InlineData("zh", 936)]
    [InlineData("th", 874)]
    [InlineData("ru", 1251)]
    [InlineData("en", 1252)]
    [InlineData("", 1252)]
    [InlineData("xx", 1252)]
    public void Legacy_codepage(string lang, int cp) => Assert.Equal(cp, TextEncoding.LegacyCodepage(lang));

    [Fact]
    public void To_utf8_vietnamese_cp1258_new_file_source_untouched()
    {
        using var tmp = new TempDir();
        var movieDir = tmp.MovieDir();
        var src = Path.Combine(movieDir, "Phim.vi.srt");
        File.WriteAllBytes(src, Cat("1\r\n00:00:01,000 --> 00:00:02,000\r\n"u8.ToArray(), ViCp1258, "\r\n"u8.ToArray()));
        var before = File.ReadAllBytes(src);
        var dst = Path.Combine(movieDir, "tmp_copy.srt");
        Assert.Equal("cp1258", TextEncoding.ToUtf8(src, dst, "vi"));
        Assert.Equal(before, File.ReadAllBytes(src)); // TĐ-12: không ghi đè nguồn
        var raw = File.ReadAllBytes(dst);
        Assert.False(raw.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]));
        var txt = Py.Utf8Strict.GetString(raw);
        Assert.EndsWith("Xin chào Việt Nam\r\n", txt.Normalize(NormalizationForm.FormC), StringComparison.Ordinal);
    }

    [Fact]
    public void To_utf8_czech_cp1250()
    {
        using var tmp = new TempDir();
        var src = tmp["a.cs.srt"];
        File.WriteAllBytes(src, CsCp1250);
        var dst = tmp["b.srt"];
        Assert.Equal("cp1250", TextEncoding.ToUtf8(src, dst, "cs"));
        Assert.Equal("Příliš žluťoučký kůň", File.ReadAllText(dst, Encoding.UTF8));
    }

    public static TheoryData<byte[], string, string> BomCases() => new()
    {
        { B(0xEF, 0xBB, 0xBF), "utf-8", "utf8bom" },
        { B(0xFF, 0xFE), "utf-16-le", "utf16le" },
        { B(0xFE, 0xFF), "utf-16-be", "utf16be" },
    };

    [Theory]
    [MemberData(nameof(BomCases))]
    public void To_utf8_bom_variants(byte[] bom, string codec, string kind)
    {
        Encoding enc = codec switch
        {
            "utf-8" => new UTF8Encoding(false),
            "utf-16-le" => new UnicodeEncoding(false, false),
            _ => new UnicodeEncoding(true, false),
        };
        using var tmp = new TempDir();
        var src = tmp["x.srt"];
        File.WriteAllBytes(src, Cat(bom, enc.GetBytes("Ahoj Tiếng Việt")));
        var dst = tmp["y.srt"];
        Assert.Equal(kind, TextEncoding.ToUtf8(src, dst, "vi"));
        Assert.Equal(new UTF8Encoding(false).GetBytes("Ahoj Tiếng Việt"), File.ReadAllBytes(dst));
    }

    [Fact]
    public void To_utf8_already_utf8_copies()
    {
        using var tmp = new TempDir();
        var src = tmp["x.srt"];
        File.WriteAllBytes(src, Encoding.UTF8.GetBytes("ok ✓"));
        var dst = tmp["y.srt"];
        Assert.Equal("utf8", TextEncoding.ToUtf8(src, dst));
        Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(dst));
    }

    [Fact]
    public void Cp1252_undefined_bytes_pass_through_like_dotnet()
    {
        var (text, how) = TextEncoding.DecodeBytes(B('A', 0x81, 'B', 0x9D, 'C', 0xE9), "en");
        Assert.Equal("cp1252", how);
        Assert.Equal("A\u0081B\u009dCé", text);
    }

    [Fact]
    public void Read_text_emulates_readalltext()
    {
        using var tmp = new TempDir();
        var p = tmp["x.srt"];
        File.WriteAllBytes(p, ViCp1258);
        Assert.Contains("�", TextEncoding.ReadText(p), StringComparison.Ordinal); // không phải UTF-8 -> ký tự hỏng
        File.WriteAllBytes(p, Cat(B(0xEF, 0xBB, 0xBF), "abc"u8.ToArray()));
        Assert.Equal("abc", TextEncoding.ReadText(p));
    }

    public static TheoryData<byte[], string, string> CharsetCases() => new()
    {
        { "ok"u8.ToArray(), "vi", "" },
        { Cat(B(0xEF, 0xBB, 0xBF), "ok"u8.ToArray()), "vi", "" },
        { B(0xFF, 0xFE, 'o', 0, 'k', 0), "vi", "UTF-16LE" },
        { B(0xFE, 0xFF, 0, 'o', 0, 'k'), "vi", "UTF-16BE" },
        { ViCp1258, "vi", "windows-1258" },
        { CsCp1250, "cs", "windows-1250" },
        { CsCp1250, "cz", "windows-1250" },
        { Encoding.GetEncoding(932).GetBytes("日本"), "ja", "shift_jis" },
        { B(0xE9, 't', 0xE9), "fr", "windows-1252" },
        { B(0xE9, 't', 0xE9), "", "windows-1252" },
    };

    [Theory]
    [MemberData(nameof(CharsetCases))]
    public void Mkvmerge_charset(byte[] data, string lang, string cs)
    {
        using var tmp = new TempDir();
        var p = tmp["s.srt"];
        File.WriteAllBytes(p, data);
        Assert.Equal(cs, TextEncoding.MkvmergeCharset(p, lang));
    }

    // ---- thêm cho bản C#: so với kết quả giải mã của Python (cp1258 giữ dấu tổ hợp, cp932 nhiều byte) ----
    [Fact]
    public void Legacy_decode_matches_python_codecs()
    {
        // Python: b"Xin ch\xe0o Vi\xea\xf2t Nam".decode("cp1258") == "Xin chào Việt Nam"
        Assert.Equal(("Xin chào Việt Nam", "cp1258"), TextEncoding.DecodeBytes(ViCp1258, "vi"));
        // Python: b"\x93\xfa\x96\x7b\x81".decode("cp932", errors="muxstudio_c1") == "日本\x81"
        Assert.Equal(("日本\u0081", "cp932"), TextEncoding.DecodeBytes(B(0x93, 0xFA, 0x96, 0x7B, 0x81), "ja"));
        // Python: các byte cp1252/cp874 không có trong bảng -> giữ nguyên mã
        Assert.Equal(("A\u0081B\u009dCé\u008d\u008f\u0090", "cp1252"),
            TextEncoding.DecodeBytes(B('A', 0x81, 'B', 0x9D, 'C', 0xE9, 0x8D, 0x8F, 0x90), "en"));
        Assert.Equal((" ÿ€þ", "cp874"), TextEncoding.DecodeBytes(B(0xA0, 0xFF, 0x80, 0xFE), "th"));
    }

    /// <summary>
    /// Mọi bảng mã MỘT byte mà bảng ngôn ngữ thật sự dùng (+1252 mặc định). Bảng mã ngoài bảng ngôn ngữ (vd cp1255)
    /// không bao giờ được chọn; ở đó Windows có thêm ký tự mới hơn bảng của Python (cp1255 0xCA = U+05BA).
    /// </summary>
    public static TheoryData<int> SingleByteCodepages()
    {
        TextEncoding.EnsureCodePages();
        var d = new TheoryData<int>();
        foreach (var cp in Naming.Langs.Values.Select(l => l.Codepage).Append(1252).Distinct().Order())
        {
            if (Encoding.GetEncoding(cp).IsSingleByte)
                d.Add(cp);
        }
        return d;
    }

    /// <summary>Đủ 256 byte của từng bảng mã một byte, so với bảng xuất từ Python (<c>Data/python_codepages.json</c>).</summary>
    [Theory]
    [MemberData(nameof(SingleByteCodepages))]
    public void Single_byte_codepage_table_matches_python(int cp)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "python_codepages.json"));
        var table = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int[]>>(json)!;
        var expected = table[cp.ToString(System.Globalization.CultureInfo.InvariantCulture)];
        var all = Enumerable.Range(0, 256).Select(b => (byte)b).ToArray();
        var enc = Encoding.GetEncoding(cp, EncoderFallback.ReplacementFallback, C1PassthroughDecoderFallback.Instance);
        var got = TextEncoding.DecodeLegacy(enc, all).Select(c => (int)c).ToArray();
        var diffs = Enumerable.Range(0, 256).Where(i => got[i] != expected[i])
            .Select(i => $"0x{i:X2}: C# U+{got[i]:X4} / Python U+{expected[i]:X4}").ToList();
        Assert.True(diffs.Count == 0, $"cp{cp} lệch {diffs.Count} byte: " + string.Join(", ", diffs));
    }
}
