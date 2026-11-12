using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MuxStudio.Core;

/// <summary>Một khối SRT.</summary>
public sealed class SrtBlock(double start, double end, string tail, string text)
{
    public double Start { get; set; } = start;
    public double End { get; set; } = end;

    /// <summary>Phần sau mốc thời gian trên cùng dòng (vd toạ độ <c>X1:...</c>).</summary>
    public string Tail { get; set; } = tail;

    public string Text { get; set; } = text;
}

/// <summary>
/// Đọc/ghi phụ đề và mốc thời gian. Port <c>core/srt.py</c>.
/// Thay <c>Format-SrtStamp</c> (cả 3 file — đã SỬA lỗi giờ, TĐ-01), <c>ConvertFrom-Stamp</c>, <c>Read-SrtBlocks</c>,
/// <c>Write-SrtBlocks</c>, <c>Get-MaxShift</c> (SubPrep), <c>Get-CueTimes</c> (bản SubFetch), <c>Get-PreviewTimes</c> (SubFetch).
/// Định dạng: SRT, ASS/SSA (chỉ đọc mốc <c>Dialogue:</c>), WebVTT. Phụ đề ASS giữ nguyên là ASS (Q7).
/// </summary>
public static partial class Srt
{
    /// <summary>Xuống dòng khi GHI file (<c>StringBuilder.AppendLine</c> trên Windows = CRLF, như .bat).</summary>
    public const string NewLine = "\r\n";

    private const RegexOptions IM = RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant;

    /// <summary>Mốc SRT (và VTT có giờ): "00:01:02,345 -->" hoặc "00:01:02.345 -->", 2-3 chữ số lẻ.</summary>
    [GeneratedRegex(@"(\d{1,2}):(\d{2}):(\d{2})[,.](\d{2,3})\s*-->")]
    private static partial Regex SrtCueRe();

    /// <summary>ASS/SSA: "Dialogue: 0,0:01:02.34," và "Dialogue: Marked=0,0:01:02.34,".</summary>
    [GeneratedRegex(@"^Dialogue:\s*[^,]*,(\d{1,2}):(\d{2}):(\d{2})\.(\d{2}),", IM)]
    private static partial Regex AssCueRe();

    /// <summary>WebVTT: giờ có thể bỏ qua: "01:02.345 -->" hoặc "00:01:02.345 -->".</summary>
    [GeneratedRegex(@"(?:(\d{1,2}):)?(\d{2}):(\d{2})\.(\d{3})\s*-->", RegexOptions.Multiline)]
    private static partial Regex VttCueRe();

    /// <summary>Khối SRT (Read-SrtBlocks của SubPrep; nhận cả dấu chấm mili-giây). <c>\Z</c> của Python = <c>\z</c> của .NET.</summary>
    [GeneratedRegex(@"^\s*(\d+)\s*\r?\n(\d{1,2}:\d{2}:\d{2}[,.]\d{2,3})\s*-->\s*(\d{1,2}:\d{2}:\d{2}[,.]\d{2,3})" +
                    @"([^\r\n]*)\r?\n([\s\S]*?)(?=\r?\n\s*\r?\n|\r?\n?\z)", RegexOptions.Multiline)]
    private static partial Regex SrtBlockRe();

    /// <summary>Mốc ĐẦU + CUỐI ASS (cho kiểm tra sức khoẻ phụ đề không phải SRT - TĐ-14).</summary>
    [GeneratedRegex(@"^Dialogue:\s*[^,]*,(\d{1,2}):(\d{2}):(\d{2})\.(\d{2}),(\d{1,2}):(\d{2}):(\d{2})\.(\d{2}),", IM)]
    private static partial Regex AssSpanRe();

    [GeneratedRegex(@"(?:(\d{1,2}):)?(\d{2}):(\d{2})\.(\d{3})\s*-->\s*(?:(\d{1,2}):)?(\d{2}):(\d{2})\.(\d{3})", RegexOptions.Multiline)]
    private static partial Regex VttSpanRe();

    // ------------------------------------------------------------------
    //  Mốc thời gian
    // ------------------------------------------------------------------

    private static double Hms(string h, string m, string s, string frac) =>
        Py.Int(h) * 3600 + Py.Int(m) * 60 + Py.Int(s) + Timing.ToDouble("0." + frac);

    private static double Hms(Match m, int first) =>
        Hms(Or0(m.Groups[first]), m.Groups[first + 1].Value, m.Groups[first + 2].Value, m.Groups[first + 3].Value);

    private static string Or0(Group g) => g.Success && g.Value.Length > 0 ? g.Value : "0";

    /// <summary><c>"01:02:03,456"</c> → 3723.456 (nhận cả <c>,</c> và <c>.</c>; 2–3 chữ số lẻ như .bat: <c>"0." + lẻ</c>).</summary>
    public static double ParseStamp(string t)
    {
        var p = t.Trim().Split(':');
        var sec = p[2].Replace('.', ',');
        var comma = sec.IndexOf(',', StringComparison.Ordinal);
        var frac = comma >= 0 ? sec[(comma + 1)..] : "";
        if (comma >= 0)
            sec = sec[..comma];
        return Py.Int(p[0]) * 3600 + Py.Int(p[1]) * 60 + Py.Int(sec) + Timing.ToDouble("0." + (frac.Length > 0 ? frac : "0"));
    }

    /// <summary>
    /// Giây → <c>HH:MM:SS,mmm</c>. SỬA LỖI .bat (TĐ-01): giờ = phần nguyên (bản cũ làm tròn nên 0:45:00 thành 01:45:00).
    /// Mili-giây làm tròn tới gần nhất (như <c>TimeSpan.FromSeconds</c> của .NET Framework mà PowerShell 5.1 dùng).
    /// </summary>
    public static string FormatStamp(double sec)
    {
        if (sec < 0)
            sec = 0.0;
        var totalMs = (long)(sec * 1000 + 0.5);
        var h = totalMs / 3_600_000;
        var rem = totalMs % 3_600_000;
        var m = rem / 60_000;
        rem %= 60_000;
        var s = rem / 1000;
        var ms = rem % 1000;
        return string.Create(CultureInfo.InvariantCulture, $"{h:00}:{m:00}:{s:00},{ms:000}");
    }

    private static string StripHead(string txt) => txt.TrimStart('﻿').TrimStart();

    /// <summary>Mốc BẮT ĐẦU của mọi dòng (giây, đã sắp xếp); <c>null</c> nếu không có (<c>Get-CueTimes</c>).</summary>
    public static List<double>? CueTimesFromText(string? txt)
    {
        if (string.IsNullOrEmpty(txt))
            return null;
        List<double> lst;
        if (StripHead(txt).StartsWith("WEBVTT", StringComparison.Ordinal))
        {
            lst = VttCueRe().Matches(txt).Select(m => Hms(m, 1)).ToList();
        }
        else
        {
            lst = SrtCueRe().Matches(txt).Select(m => Hms(m, 1)).ToList();
            if (lst.Count == 0)
                lst = AssCueRe().Matches(txt).Select(m => Hms(m, 1)).ToList();
        }
        if (lst.Count == 0)
            return null;
        lst.Sort();
        return lst;
    }

    private static string? TryReadText(string path)
    {
        try
        {
            return TextEncoding.ReadText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Đọc mốc bắt đầu từ file SRT/ASS/SSA/VTT. Không đọc được → <c>null</c>.</summary>
    public static List<double>? CueTimes(string path) => TryReadText(path) is { } txt ? CueTimesFromText(txt) : null;

    /// <summary>
    /// (định dạng, [(bắt đầu, kết thúc)] theo THỨ TỰ TRONG FILE) cho SRT / ASS-SSA / VTT; không đọc được → <c>null</c>.
    /// Định dạng: <c>srt</c> | <c>ass</c> | <c>vtt</c>. Chỉ đọc mốc, không đổi gì trong file (Q7).
    /// </summary>
    public static (string Format, List<(double Start, double End)> Spans)? CueSpansFromText(string? txt)
    {
        if (string.IsNullOrEmpty(txt))
            return null;
        if (StripHead(txt).StartsWith("WEBVTT", StringComparison.Ordinal))
        {
            var sp = VttSpanRe().Matches(txt).Select(m => (Hms(m, 1), Hms(m, 5))).ToList();
            return sp.Count > 0 ? ("vtt", sp) : null;
        }
        var blocks = ReadBlocksText(txt);
        if (blocks is not null)
            return ("srt", blocks.Select(b => (b.Start, b.End)).ToList());
        var asp = AssSpanRe().Matches(txt).Select(m => (Hms(m, 1), Hms(m, 5))).ToList();
        return asp.Count > 0 ? ("ass", asp) : null;
    }

    public static (string Format, List<(double Start, double End)> Spans)? CueSpans(string path) =>
        TryReadText(path) is { } txt ? CueSpansFromText(txt) : null;

    /// <summary>Mốc trong trường <c>preview</c> của SubSource (<c>Get-PreviewTimes</c>) — chỉ mẫu SRT, rỗng → <c>[]</c>.</summary>
    public static List<double> PreviewTimes(string? txt)
    {
        if (string.IsNullOrEmpty(txt))
            return [];
        var l = SrtCueRe().Matches(txt).Select(m => Hms(m, 1)).ToList();
        l.Sort();
        return l;
    }

    /// <summary>Độ dịch lớn nhất giữa trước/sau sync: lớn hơn giữa dòng đầu và dòng cuối (<c>Get-MaxShift</c>).</summary>
    public static double? MaxShift(IReadOnlyList<double>? before, IReadOnlyList<double>? after)
    {
        if (before is null || before.Count == 0 || after is null || after.Count == 0)
            return null;
        var n = Math.Min(before.Count, after.Count);
        var d1 = Math.Abs(after[0] - before[0]);
        var d2 = Math.Abs(after[n - 1] - before[n - 1]);
        return d2 > d1 ? d2 : d1;
    }

    // ------------------------------------------------------------------
    //  Khối SRT
    // ------------------------------------------------------------------

    public static List<SrtBlock>? ReadBlocksText(string? txt)
    {
        if (string.IsNullOrEmpty(txt))
            return null;
        var outp = SrtBlockRe().Matches(txt)
            .Select(m => new SrtBlock(ParseStamp(m.Groups[2].Value), ParseStamp(m.Groups[3].Value), m.Groups[4].Value,
                m.Groups[5].Value.TrimEnd()))
            .ToList();
        return outp.Count > 0 ? outp : null;
    }

    /// <summary><c>Read-SrtBlocks</c>: chỉ đọc SRT. File ASS/VTT → <c>null</c> (bước gọi tự bỏ qua, không đổi định dạng).</summary>
    public static List<SrtBlock>? ReadBlocks(string path) => TryReadText(path) is { } txt ? ReadBlocksText(txt) : null;

    public static string BlocksToText(IEnumerable<SrtBlock> blocks)
    {
        var sb = new StringBuilder();
        var i = 0;
        foreach (var b in blocks)
        {
            i++;
            sb.Append(i.ToString(CultureInfo.InvariantCulture)).Append(NewLine)
              .Append(FormatStamp(b.Start)).Append(" --> ").Append(FormatStamp(b.End)).Append(b.Tail).Append(NewLine)
              .Append(b.Text).Append(NewLine).Append(NewLine);
        }
        return sb.ToString();
    }

    /// <summary><c>Write-SrtBlocks</c>: đánh số lại, UTF-8 không BOM, CRLF.</summary>
    public static bool WriteBlocks(string path, IEnumerable<SrtBlock> blocks)
    {
        try
        {
            TextEncoding.WriteTextUtf8(path, BlocksToText(blocks));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public static bool IsSrtPath(string path) => path.ToLowerInvariant().EndsWith(".srt", StringComparison.Ordinal);
}
