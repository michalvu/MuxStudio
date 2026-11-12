using System.Globalization;
using System.Text;

namespace MuxStudio.Core;

/// <summary>
/// Đọc mốc thời gian từ phụ đề dạng ảnh PGS (<c>.sup</c>) — KHÔNG cần OCR. Port <c>core/pgs.py</c>
/// (port <c>Convert-PgsToTimingSrt</c>, giống hệt ở cả 3 file .bat).
/// <para>Một segment: <c>'PG' | PTS(4) | DTS(4) | loại(1) | kích thước(2) | dữ liệu</c>.
/// Loại <c>0x16</c> = PCS: <c>dữ liệu[8]</c> = cờ "chỉ đổi bảng màu" → khác 0 thì bỏ qua;
/// <c>dữ liệu[10]</c> = số đối tượng đồ hoạ: &gt; 0 = bắt đầu hiện, = 0 = xoá.</para>
/// Dòng chưa đóng mà gặp dòng mới thì tự đóng; dòng ≤ 0,1 s bị bỏ; dòng cuối chưa đóng bị bỏ (như .bat).
/// </summary>
public static class Pgs
{
    public const double PtsHz = 90000.0;

    /// <summary>Danh sách (bắt đầu, kết thúc) theo giây.</summary>
    public static List<(double Start, double End)> PgsCues(ReadOnlySpan<byte> b)
    {
        var cues = new List<(double, double)>();
        if (b.Length < 16)
            return cues;
        var start = -1.0;
        var i = 0;
        var n = b.Length;
        while (i + 13 <= n)
        {
            if (b[i] != 0x50 || b[i + 1] != 0x47) // 'P' 'G'
                break;
            var pts = (uint)((b[i + 2] << 24) | (b[i + 3] << 16) | (b[i + 4] << 8) | b[i + 5]) / PtsHz;
            var stype = b[i + 10];
            var size = (b[i + 11] << 8) | b[i + 12];
            var body = i + 13;
            if (stype == 0x16 && size >= 11 && (body + 10) < n)
            {
                if (b[body + 8] == 0) // bỏ qua PCS chỉ đổi bảng màu
                {
                    var nobj = b[body + 10];
                    if (start >= 0 && (pts - start) > 0.1)
                        cues.Add((start, pts));
                    start = nobj > 0 ? pts : -1.0;
                }
            }
            i = body + size;
        }
        return cues;
    }

    public static string TimingSrtText(IEnumerable<(double Start, double End)> cues, string placeholder = "*")
    {
        var sb = new StringBuilder();
        var k = 0;
        foreach (var (s, e) in cues)
        {
            k++;
            sb.Append(k.ToString(CultureInfo.InvariantCulture)).Append(Srt.NewLine)
              .Append(Srt.FormatStamp(s)).Append(" --> ").Append(Srt.FormatStamp(e)).Append(Srt.NewLine)
              .Append(placeholder).Append(Srt.NewLine).Append(Srt.NewLine);
        }
        return sb.ToString();
    }

    /// <summary>Ghi SRT chỉ chứa mốc thời gian (mỗi dòng là <paramref name="placeholder"/> — <c>$PgsTimingText</c>). Trả số dòng; 0 = thất bại.</summary>
    public static int PgsToTimingSrt(string supPath, string srtPath, string placeholder = "*")
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(supPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return 0;
        }
        var cues = PgsCues(data);
        if (cues.Count == 0)
            return 0;
        try
        {
            TextEncoding.WriteTextUtf8(srtPath, TimingSrtText(cues, placeholder));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
        return cues.Count;
    }
}
