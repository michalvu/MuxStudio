using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using JDict = System.Collections.Generic.OrderedDictionary<string, object?>;

namespace MuxStudio.Core;

/// <summary>Track phụ đề làm MỐC thời gian (port <c>probe.RefTrack</c>). <see cref="Lang"/> = chuỗi "language language_ietf"
/// (để hiển thị).</summary>
public sealed record RefTrack(int Id, string Ext, bool Sdh, string Lang, string Name, bool IsPgs)
{
    /// <summary><c>describe</c>.</summary>
    public string Describe()
    {
        var kind = IsPgs ? "PGS" : "văn bản";
        var extra = Sdh ? " [SDH — không có bản thường]" : "";
        var lang = Lang.Trim();
        return $"track #{Id.ToString(CultureInfo.InvariantCulture)} ({kind}, {(lang.Length > 0 ? lang : "und")}){extra}";
    }
}

/// <summary>Một audio track (để hiện bảng <c>Show-AudioStreams</c> và chọn mốc đồng bộ) — port <c>probe.AudioStream</c>.
/// <see cref="Index"/>: số thứ tự tương đối, đếm từ 0 (<c>0:a:N</c>); <see cref="Lang"/>: tag ngôn ngữ nguyên văn
/// (<c>und</c> nếu không có).</summary>
public sealed record AudioStream(int Index, string Lang, string Codec, int Channels, string Title, bool Default)
{
    /// <summary><c>describe</c>.</summary>
    public string Describe()
    {
        var ch = Channels != 0 ? $" {Channels.ToString(CultureInfo.InvariantCulture)}ch" : "";
        var ti = Title.Length > 0 ? $" - {Title}" : "";
        return $"[{Index.ToString(CultureInfo.InvariantCulture)}] {Lang} {Codec}{ch}{ti}" + (Default ? " (Default)" : "");
    }
}

/// <summary>Track phụ đề dùng làm mốc khi đo audio ghép (<c>Get-RefSubTracks</c>) — port <c>probe.SubRef</c>.
/// <see cref="Rank"/>: 0 văn bản, 1 SDH, 2 PGS.</summary>
public sealed record SubRef(int Id, string Code, string Ext, int Rank);

/// <summary>Tóm tắt để hiển thị trên bảng quét (<c>summarize_tracks</c>).</summary>
public sealed record TrackSummary(int Video, int Audio, int Subtitles, double? Fps, double Duration);

/// <summary>
/// Đọc thông tin file phim: <c>mkvmerge -J</c>, FPS, thời lượng, nhận diện track phụ đề; audio/HDR/thẻ codec cho bước ③
/// (port <c>core/probe.py</c>). JSON là kết quả <see cref="PyJson.Loads"/> (bảng <c>OrderedDictionary&lt;string, object?&gt;</c>).
/// <para>Thay: <c>Get-VideoFps</c> (SF), <c>Get-VideoFpsOf</c>/<c>Get-DurationOf</c> (MT), <c>Get-VideoDuration</c> (SP),
/// <c>Get-SubExtByCodec</c> (SP), phần chọn track trong <c>Get-RefTimes</c> (SF) và <c>Invoke-ExtractRefSubs</c> (SP — bản được
/// chọn, docs/SPEC.md 3.7), các kiểm tra Forced/SDH viết tay ở cả ba file.</para>
/// </summary>
public static partial class Probe
{
    /// <summary><c>mkv_json</c>: <c>mkvmerge -J</c>. Mã thoát ≤ 1 (1 = có cảnh báo) là đọc được. Có bộ nhớ đệm theo lượt chạy
    /// (khoá = đường dẫn tuyệt đối viết thường).</summary>
    public static JDict? MkvJson(string path, string? mkvmerge, Job? job = null, Dictionary<string, JDict?>? cache = null)
    {
        if (string.IsNullOrEmpty(mkvmerge))
            return null;
        var key = Py.NormCase(Py.AbsPath(path));
        if (cache is not null && cache.TryGetValue(key, out var hit))
            return hit;
        var r = Tools.Run([mkvmerge, "-J", path], job, TimeSpan.FromSeconds(120));
        JDict? j = null;
        if (r.Code <= 1)
        {
            try
            {
                j = PyJson.Loads(r.Stdout) as JDict;
            }
            catch (JsonException)
            {
                j = null;
            }
        }
        if (cache is not null)
            cache[key] = j;
        return j;
    }

    /// <summary><c>tracks</c>: các track có <c>type</c> = <paramref name="kind"/> (<c>video</c>, <c>audio</c>, <c>subtitles</c>).</summary>
    public static List<JDict> Tracks(JDict? j, string kind)
    {
        if (!Py.Truthy(j))
            return [];
        return [.. j.GetList("tracks").OfType<JDict>().Where(t => t.Get("type") is string s && s == kind)];
    }

    /// <summary><c>video_fps_exact</c>: FPS = 1e9 / <c>default_duration</c> (nano giây/khung hình) của track video đầu tiên có giá trị.</summary>
    public static double? VideoFpsExact(JDict? j)
    {
        foreach (var t in Tracks(j, "video"))
        {
            var dd = Timing.ToDouble(t.GetDict("properties").Get("default_duration"));
            if (dd > 0)
                return 1_000_000_000.0 / dd;
        }
        return null;
    }

    /// <summary><c>video_fps</c>: như <c>Get-VideoFps</c> (SubFetch): làm tròn 3 chữ số — dùng để so hạng FPS.</summary>
    public static double? VideoFps(JDict? j) => VideoFpsExact(j) is { } f && f != 0 ? Py.Round(f, 3) : null;

    /// <summary><c>duration</c>: thời lượng (giây) từ <c>container.properties.duration</c> (nano giây); không có → 0.</summary>
    public static double Duration(JDict? j)
    {
        if (!Py.Truthy(j))
            return 0.0;
        var d = j.GetDict("container").GetDict("properties").Get("duration");
        return Py.Truthy(d) ? Timing.ToDouble(d) / 1_000_000_000.0 : 0.0;
    }

    // ---------------------------------------------------------------- nhan dien track

    private static string NameOf(JDict props) => props.GetStr("track_name");

    private const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    [GeneratedRegex("forced", I)]
    private static partial Regex ForcedRe();

    [GeneratedRegex(@"\bSDH\b|hearing", I)]
    private static partial Regex SdhRe();

    [GeneratedRegex(@"\b(commentary|comments?|director'?s? track|filmmaker|isolated score|binh luan)\b", I)]
    private static partial Regex CommentaryRe();

    [GeneratedRegex("S_HDMV/PGS|S_VOBSUB|S_DVBSUB|PGS|VOBSUB", I)]
    private static partial Regex ImageSubRe();

    /// <summary><c>is_forced</c>: tên có 'forced' hoặc cờ Matroska.</summary>
    public static bool IsForced(JDict props) =>
        ForcedRe().IsMatch(NameOf(props)) || props.Get("forced_track") is true || props.Get("flag_forced") is true;

    /// <summary><c>is_sdh</c>: SDH / khiếm thính: tên <c>\bSDH\b</c> hoặc 'hearing', hoặc cờ Matroska.</summary>
    public static bool IsSdh(JDict props) => SdhRe().IsMatch(NameOf(props)) || props.Get("flag_hearing_impaired") is true;

    /// <summary><c>is_commentary</c> (<c>Test-CommentaryTrack</c>).</summary>
    public static bool IsCommentary(JDict props)
    {
        if (props.Get("flag_commentary") is true)
            return true;
        var nm = NameOf(props);
        return nm.Length > 0 && CommentaryRe().IsMatch(nm);
    }

    /// <summary><c>is_image_sub</c> (<c>Test-ImageSub</c>): PGS, VobSub, DVB.</summary>
    public static bool IsImageSub(string? codecId) => ImageSubRe().IsMatch(codecId ?? "");

    /// <summary><c>sub_ext_by_codec</c> (<c>Get-SubExtByCodec</c>): phụ đề văn bản dùng trực tiếp; PGS đọc mốc thời gian (nếu bật).</summary>
    public static string? SubExtByCodec(string? codecId, bool allowPgs = true)
    {
        var c = codecId ?? "";
        if (c.Contains("S_TEXT/UTF8", StringComparison.Ordinal))
            return ".srt";
        if (c.Contains("S_TEXT/ASS", StringComparison.Ordinal))
            return ".ass";
        if (c.Contains("S_TEXT/SSA", StringComparison.Ordinal))
            return ".ssa";
        if (c.Contains("S_TEXT/WEBVTT", StringComparison.Ordinal))
            return ".vtt";
        if (allowPgs && c.Contains("S_HDMV/PGS", StringComparison.Ordinal))
            return ".sup";
        return null;
    }

    /// <summary>
    /// <c>pick_ref_sub_track</c>: chọn track phụ đề làm MỐC thời gian (bản <c>Invoke-ExtractRefSubs</c> của SubPrep).
    /// Bỏ Forced (quá ít dòng). Điểm càng thấp càng tốt: thứ tự ngôn ngữ × 10, SDH +5, PGS +1000
    /// (PGS chỉ dùng khi không còn lựa chọn nào). Bằng điểm → track đứng trước.
    /// </summary>
    public static RefTrack? PickRefSubTrack(JDict? j, IReadOnlyList<string> langPriority, bool allowPgs = true)
    {
        RefTrack? best = null;
        var bestScore = 1_000_000_000;
        foreach (var t in Tracks(j, "subtitles"))
        {
            var p = t.GetDict("properties");
            var ext = SubExtByCodec(p.GetStrOrNull("codec_id"), allowPgs);
            if (ext is null)
                continue; // VobSub / DVB -> khong ho tro
            if (IsForced(p))
                continue;
            var isPgs = ext == ".sup";
            var sdh = IsSdh(p);
            var lang = $"{p.GetStr("language")} {p.GetStr("language_ietf")}".ToLowerInvariant();
            var li = 99;
            for (var i = 0; i < langPriority.Count; i++)
            {
                if (Naming.LangMatch(lang, langPriority[i]))
                {
                    li = i;
                    break;
                }
            }
            var score = li * 10 + (sdh ? 5 : 0) + (isPgs ? 1000 : 0);
            if (score < bestScore)
            {
                bestScore = score;
                best = new RefTrack(Py.ToInt(t.Get("id", 0L)), ext, sdh, lang, NameOf(p), isPgs);
            }
        }
        return best;
    }

    // ---------------------------------------------------------------- ffprobe

    /// <summary>
    /// <c>ffprobe_streams</c> (<c>Get-AudioStreams</c> của SP): <c>ffprobe -show_streams -select_streams &lt;kind&gt;</c>.
    /// Thứ tự = số thứ tự tương đối (<c>0:a:N</c> của ffmpeg). Không đọc được → rỗng. Có bộ nhớ đệm theo lượt chạy.
    /// </summary>
    public static List<JDict> FfprobeStreams(string path, string? ffprobe, string kind = "a", Job? job = null,
                                             Dictionary<(string Path, string Kind), List<JDict>>? cache = null)
    {
        if (string.IsNullOrEmpty(ffprobe))
            return [];
        var key = (Py.NormCase(Py.AbsPath(path)), kind);
        if (cache is not null && cache.TryGetValue(key, out var hit))
            return hit;
        var r = Tools.Run([ffprobe, "-v", "quiet", "-print_format", "json", "-show_streams", "-select_streams", kind, path],
                          job, TimeSpan.FromSeconds(120));
        List<JDict> outp = [];
        if (r.Code == 0 && Py.StripWs(r.Stdout).Length > 0)
        {
            try
            {
                outp = [.. (PyJson.Loads(r.Stdout) as JDict).GetList("streams").OfType<JDict>()];
            }
            catch (JsonException)
            {
                outp = [];
            }
        }
        if (cache is not null)
            cache[key] = outp;
        return outp;
    }

    /// <summary><c>audio_streams</c>: stream của ffprobe → <see cref="AudioStream"/>.</summary>
    public static List<AudioStream> AudioStreams(IEnumerable<JDict> streams)
    {
        var outp = new List<AudioStream>();
        var i = 0;
        foreach (var s in streams)
        {
            var tags = s.GetDict("tags");
            var disp = s.GetDict("disposition");
            var ch = s.Get("channels");
            outp.Add(new AudioStream(i, tags.GetStr("language", "und"), s.GetStr("codec_name"),
                                     Py.ToInt(Py.Truthy(ch) ? ch : 0), tags.GetStr("title"),
                                     Py.ToInt(Timing.ToDouble(disp.Get("default"))) == 1));
            i++;
        }
        return outp;
    }

    // ==========================================================================
    //  BƯỚC ③: audio, video, HDR (Get-CodecName, Get-ChannelName, Get-AudioBitrate, Get-FfprobeBitrates,
    //  Get-VideoCodecName, Get-ResolutionName, Get-DynamicRange, Test-DescriptiveTrack, Get-RefSubTracks)
    // ==========================================================================

    /// <summary><c>_AUDIO_ID_NAMES</c>: codec_id Matroska → tên đẹp (khi mkvmerge không ghi trường 'codec' cấp track).</summary>
    private static readonly (string Prefix, string Name)[] AudioIdNames =
    [
        ("A_TRUEHD", "TrueHD"), ("A_MLP", "TrueHD"), ("A_EAC3", "E-AC-3"), ("A_AC3", "Dolby Digital"), ("A_DTS", "DTS"),
        ("A_AAC", "AAC"), ("A_FLAC", "FLAC"), ("A_OPUS", "Opus"), ("A_VORBIS", "Vorbis"), ("A_MPEG/L3", "MP3"),
        ("A_MPEG/L2", "MP2"), ("A_PCM", "PCM"), ("A_ALAC", "ALAC"),
    ];

    /// <summary><c>_codec_from_text</c>: (mẫu, tên) theo đúng thứ tự thử của Python.</summary>
    private static readonly (Regex Re, string Name)[] CodecFromTextRules =
    [
        (new Regex("TrueHD", I), "TrueHD"),
        (new Regex("DTS-HD Master", I), "DTS-HD MA"),
        (new Regex("DTS-HD High", I), "DTS-HD HRA"),
        (new Regex("DTS-ES", I), "DTS-ES"),
        (new Regex("^DTS", I), "DTS"),
        (new Regex("E-AC-3|EAC3", I), "E-AC-3"),
        (new Regex("AC-3|AC3", I), "Dolby Digital"),
        (new Regex("AAC", I), "AAC"),
        (new Regex("FLAC", I), "FLAC"),
        (new Regex("Opus", I), "Opus"),
        (new Regex("Vorbis", I), "Vorbis"),
        (new Regex("MP3|MPEG-1 Audio layer", I), "MP3"),
        (new Regex("PCM", I), "PCM"),
    ];

    private static string? CodecFromText(string c)
    {
        foreach (var (re, name) in CodecFromTextRules)
        {
            if (re.IsMatch(c))
                return name;
        }
        return null;
    }

    /// <summary>
    /// <c>audio_codec_name</c> (<c>Get-CodecName</c>): TrueHD, DTS-HD MA/HRA, DTS-ES, DTS, E-AC-3, Dolby Digital, AAC, FLAC, Opus,
    /// Vorbis, MP3, PCM.
    /// <para>Sửa lỗi .bat (TĐ-33): <c>mkvmerge -J</c> ghi <c>codec</c> ở cấp TRACK, không nằm trong <c>properties</c> — hàm cũ đọc
    /// <c>properties.codec</c> (luôn trống) nên luôn rơi về <c>codec_id</c> (<c>A_EAC3</c> → <c>EAC3</c>, mọi DTS → <c>DTS</c>).
    /// Bản mới đọc <c>codec</c> của track, không có thì dịch <c>codec_id</c> qua bảng.</para>
    /// </summary>
    public static string AudioCodecName(JDict track)
    {
        var props = track.GetDict("properties");
        var cv = track.Get("codec");
        if (!Py.Truthy(cv))
            cv = props.Get("codec");
        var c = Py.Truthy(cv) ? Py.Str(cv) : "";
        if (c.Length > 0)
            return CodecFromText(c) ?? c;
        var cid = props.GetStr("codec_id");
        var up = cid.ToUpperInvariant();
        foreach (var (prefix, name) in AudioIdNames)
        {
            if (up.StartsWith(prefix, StringComparison.Ordinal))
                return name;
        }
        return cid.StartsWith("A_", StringComparison.Ordinal) ? cid[2..] : cid; // re.sub(r"^A_", "", cid)
    }

    private static readonly Dictionary<int, string> ChannelNames = new()
    {
        [1] = "1.0", [2] = "2.0", [3] = "2.1", [6] = "5.1", [7] = "6.1", [8] = "7.1",
    };

    /// <summary><c>channel_name</c> (<c>Get-ChannelName</c>): 1→1.0, 2→2.0, 3→2.1, 6→5.1, 7→6.1, 8→7.1, khác → <c>n.0</c>.</summary>
    public static string ChannelName(object? n)
    {
        var k = n is null || n is string { Length: 0 } ? 0 : Py.ToInt(Timing.ToDouble(n));
        if (k == 0)
            return "";
        return ChannelNames.TryGetValue(k, out var s) ? s : k.ToString(CultureInfo.InvariantCulture) + ".0";
    }

    [GeneratedRegex(@"\b(audio description|descriptive|narration for the blind|visually impaired)\b", I)]
    private static partial Regex DescriptiveRe();

    /// <summary><c>is_descriptive</c> (<c>Test-DescriptiveTrack</c>): audio description cho người khiếm thị.</summary>
    public static bool IsDescriptive(JDict props)
    {
        if (props.Get("flag_visual_impaired") is true)
            return true;
        var nm = NameOf(props);
        return nm.Length > 0 && DescriptiveRe().IsMatch(nm);
    }

    private static readonly Dictionary<string, int> HeuristicKbps = new(StringComparer.Ordinal)
    {
        ["PCM"] = 4608, ["TrueHD"] = 4000, ["DTS-HD MA"] = 3500, ["DTS-HD HRA"] = 2000, ["FLAC"] = 1400, ["DTS-ES"] = 1509,
        ["DTS"] = 1509, ["E-AC-3"] = 768, ["Dolby Digital"] = 640, ["AAC"] = 256, ["Opus"] = 256, ["Vorbis"] = 224, ["MP3"] = 192,
    };

    /// <summary><c>ffprobe_bitrates</c> (<c>Get-FfprobeBitrates</c>): chỉ số stream (= ID track của mkvmerge trong MKV) → bitrate (bit/s).</summary>
    public static OrderedDictionary<int, double> FfprobeBitrates(IEnumerable<JDict> streams)
    {
        var outp = new OrderedDictionary<int, double>();
        foreach (var s in streams)
        {
            var br = Timing.ToDouble(s.Get("bit_rate"));
            if (br <= 0)
            {
                var tags = s.GetDict("tags");
                foreach (var tn in new[] { "BPS", "BPS-eng", "BPS-en" })
                {
                    if (Timing.ToDouble(tags.Get(tn)) > 0)
                    {
                        br = Timing.ToDouble(tags.Get(tn));
                        break;
                    }
                }
            }
            if (br > 0 && s.Get("index") is { } idx)
                outp[Py.ToInt(idx)] = br;
        }
        return outp;
    }

    /// <summary>
    /// <c>audio_bitrate</c> (<c>Get-AudioBitrate</c>): 1) tag <c>BPS</c> của mkvmerge, 2) ffprobe, 3) ước lượng theo codec × kênh/6.
    /// Trả (bit/s, nguồn: <c>tag</c> | <c>ffprobe</c> | <c>ước lượng</c>).
    /// </summary>
    public static (double Bps, string Source) AudioBitrate(JDict track, IReadOnlyDictionary<int, double>? ffMap = null)
    {
        var p = track.GetDict("properties");
        foreach (var k in new[] { "tag_bps", "tag_bps_eng" })
        {
            var v = Timing.ToDouble(p.Get(k));
            if (v > 0)
                return (v, "tag");
        }
        if (ffMap is { Count: > 0 })
        {
            var v = ffMap.GetValueOrDefault(Py.ToInt(track.Get("id", -1L)), 0.0);
            if (v > 0)
                return (v, "ffprobe");
        }
        var @base = HeuristicKbps.GetValueOrDefault(AudioCodecName(track), 300);
        var ch = Py.ToInt(Timing.ToDouble(p.Get("audio_channels")));
        if (ch == 0)
            ch = 2;
        return (@base * 1000.0 * (Math.Max(ch, 1) / 6.0), "ước lượng");
    }

    /// <summary><c>format_kbps</c> (<c>Format-Kbps</c>): <c>640 kbps</c> ('' nếu không có).</summary>
    public static string FormatKbps(double? bps)
    {
        if (bps is not { } b || b <= 0)
            return ""; // NaN đi tiếp và lỗi ở round() như Python

        return checked((long)Math.Round(b / 1000)).ToString("#,0", CultureInfo.InvariantCulture) + " kbps";
    }

    [GeneratedRegex("AV1", I)]
    private static partial Regex Av1Re();

    [GeneratedRegex(@"HEVC|H\.?265|MPEGH", I)]
    private static partial Regex HevcRe();

    [GeneratedRegex(@"AVC|H\.?264|MPEG4", I)]
    private static partial Regex H264Re();

    [GeneratedRegex("VP9", I)]
    private static partial Regex Vp9Re();

    [GeneratedRegex("MPEG-?2", I)]
    private static partial Regex Mpeg2Re();

    /// <summary><c>video_codec_name</c> (<c>Get-VideoCodecName</c>): AV1, HEVC, H264, VP9, MPEG2.</summary>
    public static string VideoCodecName(string? codecId, string? codec = null)
    {
        var s = $"{codecId ?? ""} {codec ?? ""}";
        if (Av1Re().IsMatch(s))
            return "AV1";
        if (HevcRe().IsMatch(s))
            return "HEVC";
        if (H264Re().IsMatch(s))
            return "H264";
        if (Vp9Re().IsMatch(s))
            return "VP9";
        if (Mpeg2Re().IsMatch(s))
            return "MPEG2";
        return "";
    }

    [GeneratedRegex(@"^(\d+)x(\d+)\z")]
    private static partial Regex DimRe();

    /// <summary><c>resolution_name</c> (<c>Get-ResolutionName</c>) theo chiều cao <c>pixel_dimensions</c> (<c>1920x1080</c>).</summary>
    public static string ResolutionName(object? dim)
    {
        var m = DimRe().Match(Py.Truthy(dim) ? Py.Str(dim) : "");
        if (!m.Success)
            return "";
        long h;
        try
        {
            h = Py.Long(m.Groups[2].Value);
        }
        catch (OverflowException)
        {
            h = long.MaxValue; // int() của Python không tràn
        }
        if (h >= 2000)
            return "2160p";
        if (h >= 1400)
            return "1440p";
        if (h >= 900)
            return "1080p";
        if (h >= 600)
            return "720p";
        if (h >= 500)
            return "576p";
        return "480p";
    }

    [GeneratedRegex("smpte2084", I)]
    private static partial Regex Pq2084Re();

    [GeneratedRegex("arib-std-b67", I)]
    private static partial Regex HlgTransferRe();

    [GeneratedRegex("dolby vision|DOVI", I)]
    private static partial Regex DoviSideRe();

    [GeneratedRegex("HDR Dynamic Metadata", I)]
    private static partial Regex Hdr10PlusSideRe();

    [GeneratedRegex(@"\b(DV|DoVi|Dolby[\. ]?Vision)\b", I)]
    private static partial Regex DvNameRe();

    [GeneratedRegex(@"\bHDR10\+|\bHDR10P\b", I)]
    private static partial Regex Hdr10PlusNameRe();

    [GeneratedRegex(@"\bHDR(?:10)?\b", I)]
    private static partial Regex HdrNameRe();

    [GeneratedRegex(@"\bHLG\b", I)]
    private static partial Regex HlgNameRe();

    /// <summary><c>dynamic_range</c> (<c>Get-DynamicRange</c>): <c>DV</c>, <c>HDR</c>, <c>HDR10+</c>, <c>HLG</c> (vd <c>DV HDR</c>) từ
    /// stream video của ffprobe (<c>color_transfer</c>, <c>side_data_list</c>); thiếu thì đọc từ tên file gốc.</summary>
    public static string DynamicRange(JDict? videoStream, string origName)
    {
        var dv = false;
        var hdr = "";
        var v = videoStream ?? new JDict(StringComparer.Ordinal);
        var ct = v.GetStr("color_transfer");
        if (Pq2084Re().IsMatch(ct))
            hdr = "HDR";
        else if (HlgTransferRe().IsMatch(ct))
            hdr = "HLG";
        foreach (var sd in v.GetList("side_data_list"))
        {
            var t = PyDict.AsDict(sd).GetStr("side_data_type");
            if (DoviSideRe().IsMatch(t))
                dv = true;
            if (Hdr10PlusSideRe().IsMatch(t))
                hdr = "HDR10+";
        }
        if (!dv && DvNameRe().IsMatch(origName))
            dv = true;
        if (hdr.Length == 0)
        {
            if (Hdr10PlusNameRe().IsMatch(origName))
                hdr = "HDR10+";
            else if (HdrNameRe().IsMatch(origName)) // TĐ-33: 'HDR10' cũng là HDR (.bat bỏ sót)
                hdr = "HDR";
            else if (HlgNameRe().IsMatch(origName))
                hdr = "HLG";
        }
        return string.Join(' ', new[] { dv ? "DV" : "", hdr }.Where(x => x.Length > 0));
    }

    /// <summary><c>ref_sub_tracks</c> (<c>Get-RefSubTracks</c>): bỏ Forced; hạng văn bản 0 / SDH 1 / PGS 2 (sắp xếp ổn định theo hạng).</summary>
    public static List<SubRef> RefSubTracks(JDict? j)
    {
        var outp = new List<SubRef>();
        foreach (var t in Tracks(j, "subtitles"))
        {
            var p = t.GetDict("properties");
            var cid = p.GetStr("codec_id");
            string ext;
            if (cid.Contains("S_TEXT/UTF8", StringComparison.Ordinal))
                ext = ".srt";
            else if (cid.Contains("S_TEXT/ASS", StringComparison.Ordinal))
                ext = ".ass";
            else if (cid.Contains("S_TEXT/SSA", StringComparison.Ordinal))
                ext = ".ssa";
            else if (cid.Contains("S_HDMV/PGS", StringComparison.Ordinal))
                ext = ".sup";
            else
                continue;
            if (IsForced(p))
                continue;
            var rank = ext == ".sup" ? 2 : IsSdh(p) ? 1 : 0;
            outp.Add(new SubRef(Py.ToInt(t.Get("id", 0L)),
                                Naming.LangFromTrack(p.GetStrOrNull("language"), p.GetStrOrNull("language_ietf")), ext, rank));
        }
        return [.. outp.OrderBy(s => s.Rank)];
    }

    /// <summary><c>track_lang</c>: mã 2 chữ của track (<see cref="Naming.LangFromTrack"/>).</summary>
    public static string TrackLang(JDict track)
    {
        var p = track.GetDict("properties");
        return Naming.LangFromTrack(p.GetStrOrNull("language"), p.GetStrOrNull("language_ietf"));
    }

    /// <summary><c>summarize_tracks</c>: tóm tắt để hiển thị trên bảng quét.</summary>
    public static TrackSummary SummarizeTracks(JDict? j) =>
        new(Tracks(j, "video").Count, Tracks(j, "audio").Count, Tracks(j, "subtitles").Count, VideoFps(j), Duration(j));
}
