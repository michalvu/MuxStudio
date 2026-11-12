using System.Globalization;
using System.Text.RegularExpressions;

namespace MuxStudio.Core;

/// <summary>Thông tin tập phim bộ nhận diện từ tên file.</summary>
/// <param name="Season">0 = tên không ghi season (số tuyệt đối).</param>
public sealed record EpisodeInfo(string Series, int? Year, int Season, int Episode, int? EpisodeEnd, bool HasSeason, bool Confident)
{
    /// <summary>Đánh số tuyệt đối, không có season (anime).</summary>
    public bool Absolute => !HasSeason;

    /// <summary>Nhãn kiểu SubPrep (<c>Get-EpLabel</c>): <c>S01E05</c> / <c>E643</c>.</summary>
    public string Label => HasSeason
        ? string.Create(CultureInfo.InvariantCulture, $"S{Season:00}E{Episode:00}")
        : string.Create(CultureInfo.InvariantCulture, $"E{Episode:00}");

    /// <summary>Nhãn kiểu MuxTool (<c>Format-EpRef</c>): <c>S01E05</c> / <c>tap #643</c> — khoá của <c>DonorOffsetMs</c>.</summary>
    public string RefLabel => Absolute
        ? string.Create(CultureInfo.InvariantCulture, $"tap #{Episode}")
        : string.Create(CultureInfo.InvariantCulture, $"S{Season:00}E{Episode:00}");
}

/// <summary>
/// Nhận diện tập phim bộ — bản gộp dùng chung ba bước (docs/SPEC.md mục 3.4). Port <c>core/episodes.py</c>.
/// Thay <c>Get-EpisodeInfo</c> (SubFetch, SubPrep), <c>Split-EpisodeName</c> + <c>Format-EpRef</c> (MuxTool),
/// <c>Get-EpLabel</c> (SubPrep), <c>Test-EpisodeConflict</c> (SubFetch), quy tắc ghép tập của <c>Find-DonorFile</c> (MuxTool).
/// </summary>
public static partial class Episodes
{
    private const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    [GeneratedRegex(@"\[[0-9A-Fa-f]{8}\]")]
    private static partial Regex CrcRe();

    [GeneratedRegex(@"^\[[^\]]+\]\s*")]
    private static partial Regex LeadGroupRe();

    /// <summary>1. S01E05, S21E1085, S01E05-E06, S01E05E06</summary>
    [GeneratedRegex(@"\bS(\d{1,2})\s?E(\d{1,4})(?:\s?-\s?|\s?)?(?:E(\d{1,4}))?\b", IC)]
    private static partial Regex SxeRe();

    /// <summary>2. 1x05</summary>
    [GeneratedRegex(@"(?<!\d)(\d{1,2})x(\d{2})(?!\d)")]
    private static partial Regex NxRe();

    /// <summary>3. Season 2 Episode 5 / Season 2 Ep 5 / Season 2 Tap 5 / Season 2 E05</summary>
    [GeneratedRegex(@"\bSeason\s?(\d{1,2})\b.*?\b(?:Episode|Ep|E|Tap)\s?(\d{1,4})\b", IC)]
    private static partial Regex SeasonWordRe();

    /// <summary>4. Số tuyệt đối sau " - ": "One Piece - 0643", "- 05v2", "- 1085[1080p]"</summary>
    [GeneratedRegex(@"\s-\s(?:E|EP|Episode\s?)?(\d{1,4})(?:v\d)?(?=\s|$|\[|\()", IC)]
    private static partial Regex AbsDashRe();

    /// <summary>5. "Naruto EP 055", "Ep 5", "E05", "Tap 12"</summary>
    [GeneratedRegex(@"\b(?:Episode|Ep|E|Tap)\s?(\d{1,4})(?:v\d)?\b", IC)]
    private static partial Regex EpWordRe();

    [GeneratedRegex(@"[\._]+")]
    private static partial Regex DotUnderscoreRunRe();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WsRunRe();

    [GeneratedRegex(@"[\(\)\[\]\{\}]")]
    private static partial Regex AnyBracketCharRe();

    private static string Prep(string name)
    {
        var t = CrcRe().Replace(name, " ");
        t = LeadGroupRe().Replace(t, "", 1);
        t = DotUnderscoreRunRe().Replace(t, " ");
        return WsRunRe().Replace(t, " ");
    }

    /// <summary>"Phim - 1917" là NĂM, không phải số tập: 4 chữ số trong 1900–2099.</summary>
    private static bool IsYear(string digits) => digits.Length == 4 && Py.Int(digits) is >= 1900 and <= 2099;

    private static (string Series, int? Year) SeriesYear(string head)
    {
        var (title, year) = Naming.PickYear(head);
        title ??= head;
        title = AnyBracketCharRe().Replace(title, " ");
        title = WsRunRe().Replace(title, " ");
        return (Py.Strip(title, " -_"), year);
    }

    [GeneratedRegex(@"\b(19|20)\d{2}\b", IC)]
    private static partial Regex GYearRe();

    [GeneratedRegex(@"\b(4320|2160|1440|1080|720|576|480|360)p?\b", IC)]
    private static partial Regex GResRe();

    [GeneratedRegex(@"\b(x|h)\s?26[45]\b", IC)]
    private static partial Regex GCodecRe();

    [GeneratedRegex(@"\b(10|8)\s?bit\b", IC)]
    private static partial Regex GBitRe();

    [GeneratedRegex(@"\b\d\s?\.\s?\d\b")]
    private static partial Regex GChannelsRe();

    [GeneratedRegex(@"\b(aac|ac3|eac3|dts|flac|opus|ddp?\d?|truehd|atmos|mp3)\d*\b", IC)]
    private static partial Regex GAudioRe();

    [GeneratedRegex(@"\b(bluray|blu ray|bdrip|brrip|webrip|web dl|web|hdtv|dvdrip|remux|hevc|avc|hdr\d*|dovi|sdr|imax)\b", IC)]
    private static partial Regex GSourceRe();

    [GeneratedRegex(@"\bv\d\b", IC)]
    private static partial Regex GVersionRe();

    [GeneratedRegex(@"(?<!\d)(\d{1,4})(?!\d)")]
    private static partial Regex GNumRe();

    /// <summary>Phép đoán cuối của SubPrep: bỏ năm, độ phân giải, codec… rồi lấy số còn lại cuối cùng.</summary>
    private static int? GuessNumber(string t)
    {
        var c = t;
        c = GYearRe().Replace(c, " ");
        c = GResRe().Replace(c, " ");
        c = GCodecRe().Replace(c, " ");
        c = GBitRe().Replace(c, " ");
        c = GChannelsRe().Replace(c, " ");
        c = GAudioRe().Replace(c, " ");
        c = GSourceRe().Replace(c, " ");
        c = GVersionRe().Replace(c, " ");
        var nums = GNumRe().Matches(c);
        return nums.Count > 0 ? Py.Int(nums[^1].Groups[1].Value) : null;
    }

    /// <summary>
    /// Nhận diện tập từ tên file (không kèm đuôi). Trả <c>null</c> nếu là phim lẻ.
    /// <paramref name="allowGuess"/> = true trả thêm phép đoán "không chắc" (<c>Confident = false</c>) —
    /// CHỈ để hiển thị/báo lỗi, không bao giờ dùng để ghép hay chặn.
    /// </summary>
    public static EpisodeInfo? ParseEpisode(string name, bool allowGuess = false)
    {
        var t = Prep(name);

        var m = SxeRe().Match(t);
        if (m.Success)
        {
            var (series, year) = SeriesYear(t[..m.Index]);
            return new EpisodeInfo(series, year, Py.Int(m.Groups[1].Value), Py.Int(m.Groups[2].Value),
                m.Groups[3].Success ? Py.Int(m.Groups[3].Value) : null, true, true);
        }

        m = NxRe().Match(t);
        if (m.Success)
        {
            var (series, year) = SeriesYear(t[..m.Index]);
            return new EpisodeInfo(series, year, Py.Int(m.Groups[1].Value), Py.Int(m.Groups[2].Value), null, true, true);
        }

        m = SeasonWordRe().Match(t);
        if (m.Success)
        {
            var (series, year) = SeriesYear(t[..m.Index]);
            return new EpisodeInfo(series, year, Py.Int(m.Groups[1].Value), Py.Int(m.Groups[2].Value), null, true, true);
        }

        foreach (var pat in new[] { AbsDashRe(), EpWordRe() })
        {
            foreach (Match mm in pat.Matches(t))
            {
                var digits = mm.Groups[1].Value;
                var n = Py.Int(digits);
                if (IsYear(digits) || n <= 0)
                    continue;
                var (series, year) = SeriesYear(t[..mm.Index]);
                return new EpisodeInfo(series, year, 0, n, null, false, true);
            }
        }

        if (allowGuess)
        {
            var n = GuessNumber(t);
            if (n is { } nv)
                return new EpisodeInfo("", null, 0, nv, null, false, false);
        }
        return null;
    }

    public static bool IsEpisode(string name) => ParseEpisode(name) is not null;

    /// <summary>
    /// <c>Test-EpisodeConflict</c>: hai tên đều ghi số tập (chắc chắn) mà khác nhau → chắc chắn khác tập.
    /// Cả hai có season → khác season hoặc khác tập là xung đột; một bên không có season → khác số tập là xung đột.
    /// <c>S16E643</c> / <c>- 0643</c> → không xung đột.
    /// </summary>
    public static bool EpisodeConflict(string nameA, string nameB)
    {
        var a = ParseEpisode(nameA);
        var b = ParseEpisode(nameB);
        if (a is null || b is null)
            return false;
        if (a.HasSeason && b.HasSeason)
            return a.Season != b.Season || a.Episode != b.Episode;
        return a.Episode != b.Episode;
    }

    /// <summary>
    /// Hai tên chỉ cùng một tập (quy tắc của <c>Find-DonorFile</c>).
    /// Cả hai có season → trùng season + tập; cả hai tuyệt đối → trùng tập; một bên mỗi kiểu
    /// (<c>S16E643</c> / <c>- 643</c>) → trùng số tập VÀ (season = 1 hoặc số tập ≥ 100).
    /// </summary>
    public static bool SameEpisode(EpisodeInfo? a, EpisodeInfo? b)
    {
        if (a is null || b is null || !a.Confident || !b.Confident)
            return false;
        if (a.Episode != b.Episode)
            return false;
        if (a.Absolute == b.Absolute)
            return a.Absolute || a.Season == b.Season;
        var nonAbs = a.Absolute ? b : a;
        return nonAbs.Season == 1 || a.Episode >= 100;
    }

    public static bool SameEpisodeNames(string nameA, string nameB) =>
        SameEpisode(ParseEpisode(nameA), ParseEpisode(nameB));
}
