using System.Globalization;
using System.Text.RegularExpressions;

namespace MuxStudio.Core;

/// <summary>
/// So khớp thời gian giữa hai bộ mốc phụ đề — lõi của mọi phép đo ở cả ba bước.
/// Port từ <c>core/timing.py</c> (vốn port CHÍNH XÁC từ các hàm PowerShell của .bat):
/// <c>ConvertTo-Double</c>, <c>Format-Inv</c>, <c>Get-EvenSample</c>, <c>Get-OffsetVote</c>,
/// <c>Get-TimingRelation</c>, <c>Get-QuickOffset</c> và bảng <c>$script:FpsRatios</c>.
/// <list type="bullet">
/// <item><c>Math.Round(x)</c> mặc định làm tròn về số CHẴN — giống <c>[int]x</c> của PowerShell và <c>round()</c> của Python.
/// KHÔNG dùng ép kiểu <c>(int)x</c> (cắt phần lẻ).</item>
/// <item>Bằng phiếu → giữ khoá được thêm vào TRƯỚC: dùng <see cref="OrderedDictionary{TKey,TValue}"/>.</item>
/// <item>Thứ tự cộng dồn số thực giữ y như bản Python/.bat để tổng ra từng bit như nhau.</item>
/// </list>
/// </summary>
public static partial class Timing
{
    // ------------------------------------------------------------------
    //  Số thập phân không phụ thuộc vùng miền
    // ------------------------------------------------------------------

    /// <summary>Cú pháp số của <c>float()</c> trong Python (không gạch dưới).</summary>
    [GeneratedRegex(@"^[+-]?(?:(?:[0-9]+\.?[0-9]*|\.[0-9]+)(?:[eE][+-]?[0-9]+)?|inf(?:inity)?|nan)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PyFloatRe();

    /// <summary>
    /// <c>ConvertTo-Double</c>: đọc số thập phân, không phụ thuộc vùng miền Windows.
    /// "23.976" và "23,976" đều ra 23.976. Không đọc được → 0.0 (giống .bat).
    /// </summary>
    public static double ToDouble(object? v) => v switch
    {
        null => 0.0,
        bool => 0.0, // PowerShell: [string]$true = "True" -> không đọc được
        double d => d,
        float f => f,
        int i => i,
        long l => l,
        decimal m => (double)m,
        string s => ToDouble(s),
        _ => ToDouble(Convert.ToString(v, CultureInfo.InvariantCulture)),
    };

    /// <inheritdoc cref="ToDouble(object?)"/>
    public static double ToDouble(string? v)
    {
        if (v is null)
            return 0.0;
        var s = v.Trim();
        if (s.Length == 0)
            return 0.0;
        foreach (var cand in new[] { s, s.Replace(',', '.') })
        {
            if (TryParsePyFloat(cand, out var r))
                return r;
        }
        return 0.0;
    }

    /// <summary>Đọc chuỗi theo đúng cú pháp <c>float()</c> của Python (đã trim).</summary>
    internal static bool TryParsePyFloat(string s, out double value)
    {
        value = 0.0;
        if (!PyFloatRe().IsMatch(s))
            return false;
        var body = s.TrimStart('+', '-');
        var neg = s.StartsWith('-');
        if (body.StartsWith("inf", StringComparison.OrdinalIgnoreCase))
        {
            value = neg ? double.NegativeInfinity : double.PositiveInfinity;
            return true;
        }
        if (body.Equals("nan", StringComparison.OrdinalIgnoreCase))
        {
            value = double.NaN;
            return true;
        }
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// <c>Format-Inv</c>: số thập phân luôn dấu CHẤM, bỏ số 0 thừa (như mẫu <c>0.######</c>).
    /// Dùng cho mọi số đưa vào dòng lệnh ffmpeg/mkvmerge.
    /// </summary>
    public static string FmtInv(double x, int decimals = 6)
    {
        var s = x.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        if (s.Contains('.', StringComparison.Ordinal))
            s = s.TrimEnd('0').TrimEnd('.');
        if (s is "-0" or "")
            s = "0";
        return s;
    }

    // ------------------------------------------------------------------
    //  Bảng tỉ lệ FPS
    // ------------------------------------------------------------------

    /// <summary>Cùng 7 tỉ lệ, cùng thứ tự như <c>$script:FpsRatios</c>.</summary>
    public static readonly IReadOnlyList<FpsRatio> FpsRatios =
    [
        new(1.0, "không co giãn"),
        new(24 / 23.976, "24 → 23.976 (hoặc 30 → 29.97)"),
        new(23.976 / 24, "23.976 → 24 (hoặc 29.97 → 30)"),
        new(25 / 23.976, "PAL 25 → NTSC 23.976"),
        new(23.976 / 25, "NTSC 23.976 → PAL 25"),
        new(25.0 / 24, "25 → 24"),
        new(24.0 / 25, "24 → 25"),
    ];

    /// <summary>Ngưỡng coi là lệch FPS: <c>[math]::Abs($best.Scale - 1.0) -gt 0.0002</c>.</summary>
    public const double FpsMismatchEps = 0.0002;

    /// <summary>Số mốc tối thiểu mỗi bên để đo: <c>Count -lt 30 -> $null</c>.</summary>
    public const int MinCuesForRelation = 30;

    // ------------------------------------------------------------------
    //  Lấy mẫu đều + bỏ phiếu độ lệch
    // ------------------------------------------------------------------

    /// <summary><c>Get-EvenSample</c>: lấy <paramref name="n"/> phần tử rải đều. Chỉ số = <c>[int](i*step)</c> (làm tròn về số chẵn).</summary>
    public static List<T> EvenSample<T>(IReadOnlyList<T> arr, int n)
    {
        if (arr.Count <= n)
            return [.. arr];
        var step = (double)arr.Count / n;
        var outp = new List<T>(n);
        for (var i = 0; i < n; i++)
            outp.Add(arr[(int)Math.Round(i * step)]);
        return outp;
    }

    private static bool IsSorted(List<double> a)
    {
        for (var i = 0; i < a.Count - 1; i++)
        {
            if (!(a[i] <= a[i + 1]))
                return false;
        }
        return true;
    }

    /// <summary><c>bisect_left</c> của Python.</summary>
    internal static int BisectLeft(List<double> a, double x)
    {
        int lo = 0, hi = a.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (a[mid] < x)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    /// <summary><c>bisect_right</c> của Python.</summary>
    internal static int BisectRight(List<double> a, double x)
    {
        int lo = 0, hi = a.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (x < a[mid])
                hi = mid;
            else
                lo = mid + 1;
        }
        return lo;
    }

    /// <summary>
    /// <c>Get-OffsetVote</c>: bỏ phiếu độ lệch cố định <c>ref - tgt</c> theo ô <paramref name="binSize"/> trong ±<paramref name="maxOff"/>.
    /// Trả về độ lệch = trung bình các hiệu nằm trong ±0,35 s quanh ô thắng, và số phiếu của ô thắng.
    /// Khác .bat duy nhất: chỉ duyệt các mốc gốc trong cửa sổ quanh mỗi mốc cần đo (tìm nhị phân) — kết quả giống hệt.
    /// </summary>
    public static OffsetVote? OffsetVoteOf(IEnumerable<double> refTimes, IReadOnlyList<double> tgt,
        double maxOff = 400.0, double binSize = 0.15)
    {
        var refs = refTimes.ToList();
        var votes = new OrderedDictionary<long, int>();
        var useWindow = IsSorted(refs);
        const double margin = 1.0; // cửa sổ rộng hơn một chút; điều kiện chính xác vẫn kiểm từng cặp

        foreach (var t in tgt)
        {
            int lo = 0, hi = refs.Count;
            if (useWindow)
            {
                lo = BisectLeft(refs, t - maxOff - margin);
                hi = BisectRight(refs, t + maxOff + margin);
            }
            for (var j = lo; j < hi; j++)
            {
                var d = refs[j] - t;
                if (d < -maxOff || d > maxOff)
                    continue;
                var k = (long)Math.Round(d / binSize);
                votes[k] = votes.TryGetValue(k, out var c) ? c + 1 : 1;
            }
        }

        if (votes.Count == 0)
            return null;
        long bk = 0;
        var bv = -1;
        foreach (var (k, c) in votes) // bằng phiếu -> giữ khoá gặp trước (như .NET Dictionary của .bat)
        {
            if (c > bv)
            {
                bv = c;
                bk = k;
            }
        }
        var center = bk * binSize;

        var total = 0.0;
        var cnt = 0;
        foreach (var t in tgt)
        {
            int lo = 0, hi = refs.Count;
            if (useWindow)
            {
                lo = BisectLeft(refs, t + center - 0.35 - margin);
                hi = BisectRight(refs, t + center + 0.35 + margin);
            }
            for (var j = lo; j < hi; j++)
            {
                var d = refs[j] - t;
                if (Math.Abs(d - center) <= 0.35)
                {
                    total += d;
                    cnt++;
                }
            }
        }
        var off = cnt > 0 ? total / cnt : center;
        return new OffsetVote(off, bv);
    }

    // ------------------------------------------------------------------
    //  Quan hệ thời gian: co giãn (tỉ lệ FPS) + độ lệch
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>Get-TimingRelation</c>: thử từng tỉ lệ FPS, chọn tỉ lệ nhiều phiếu nhất.
    /// Cần ≥ 30 mốc mỗi bên. Bằng phiếu → giữ tỉ lệ đứng trước trong <see cref="FpsRatios"/>.
    /// <paramref name="nRef"/>/<paramref name="nTgt"/> = <c>$FpsSampleRef</c>/<c>$FpsSampleTgt</c> (400/100).
    /// </summary>
    public static TimingRelation? TimingRelationOf(IReadOnlyList<double>? refTimes, IReadOnlyList<double>? tgtTimes,
        int nRef = 400, int nTgt = 100)
    {
        if (refTimes is null || refTimes.Count == 0 || tgtTimes is null || tgtTimes.Count == 0)
            return null;
        if (refTimes.Count < MinCuesForRelation || tgtTimes.Count < MinCuesForRelation)
            return null;
        var rS = EvenSample(refTimes, nRef);
        var tS = EvenSample(tgtTimes, nTgt);

        FpsRatio? bestFr = null;
        OffsetVote? bestV = null;
        foreach (var fr in FpsRatios)
        {
            var scaled = new List<double>(tS.Count);
            foreach (var x in tS)
                scaled.Add(x * fr.Ratio);
            var v = OffsetVoteOf(rS, scaled);
            if (v is null)
                continue;
            if (bestV is null || v.Votes > bestV.Votes)
            {
                bestFr = fr;
                bestV = v;
            }
        }
        if (bestFr is null || bestV is null)
            return null;
        var lastT = tgtTimes[tgtTimes.Count - 1];
        return new TimingRelation(
            Scale: bestFr.Ratio,
            Name: bestFr.Name,
            Offset: bestV.Offset,
            Votes: bestV.Votes,
            Drift: Math.Abs((bestFr.Ratio - 1.0) * lastT),
            FpsMismatch: Math.Abs(bestFr.Ratio - 1.0) > FpsMismatchEps);
    }

    // ------------------------------------------------------------------
    //  Ước tính nhanh từ "preview" của SubSource
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>Get-QuickOffset</c>: chỉ kết luận khi TẤT CẢ mốc preview cùng đồng ý một độ lệch.
    /// Ô 0,25 s, phạm vi ±180 s, cần ≥ 3 mốc preview.
    /// </summary>
    public static QuickOffset? QuickOffsetOf(IReadOnlyList<double>? refTimes, IReadOnlyList<double> pvTimes)
    {
        if (refTimes is null || refTimes.Count == 0 || pvTimes.Count < 3)
            return null;
        var votes = new OrderedDictionary<long, int>();
        foreach (var t in pvTimes)
        {
            foreach (var r in refTimes)
            {
                var d = r - t;
                if (d < -180 || d > 180)
                    continue;
                var k = (long)Math.Round(d / 0.25);
                votes[k] = votes.TryGetValue(k, out var c) ? c + 1 : 1;
            }
        }
        if (votes.Count == 0)
            return null;
        long bk = 0;
        var bv = -1;
        foreach (var (k, c) in votes)
        {
            if (c > bv)
            {
                bv = c;
                bk = k;
            }
        }
        if (bv < pvTimes.Count)
            return null;
        return new QuickOffset(bk * 0.25, bv, pvTimes.Count);
    }
}

/// <summary>Một tỉ lệ co giãn FPS.</summary>
public sealed record FpsRatio(double Ratio, string Name);

/// <summary>Kết quả bỏ phiếu độ lệch.</summary>
public sealed record OffsetVote(double Offset, int Votes);

/// <summary>Quan hệ <c>mốc_gốc ≈ mốc_cần_đo × Scale + Offset</c>.</summary>
/// <param name="Drift">Độ trôi tích luỹ ở cuối phim nếu bỏ qua co giãn (giây).</param>
public sealed record TimingRelation(double Scale, string Name, double Offset, int Votes, double Drift, bool FpsMismatch);

/// <param name="Total">Số mốc preview (&gt; 0 nghĩa là "ước tính", hiện dấu ~).</param>
public sealed record QuickOffset(double Offset, int Votes, int Total);

/// <summary>
/// Tập ô <c>floor(giây)</c> của các mốc gốc (từ <c>Remove-LeadingExtra</c>).
/// <see cref="Near"/> = có mốc gốc trong khoảng ~±1,5 s quanh x: kiểm 4 ô <c>c, c-1, c+1, c-2</c> với <c>c = floor(x)</c> — đúng như .bat.
/// </summary>
public sealed class RefBins
{
    private readonly HashSet<long> _bins;

    public RefBins(IEnumerable<double> refTimes)
    {
        _bins = [];
        foreach (var r in refTimes)
            _bins.Add((long)Math.Floor(r));
    }

    public bool Near(double x)
    {
        var c = (long)Math.Floor(x);
        return _bins.Contains(c) || _bins.Contains(c - 1) || _bins.Contains(c + 1) || _bins.Contains(c - 2);
    }

    public int Count => _bins.Count;
}
