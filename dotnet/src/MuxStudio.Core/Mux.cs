using System.Globalization;
using System.Text.RegularExpressions;
using JDict = System.Collections.Generic.OrderedDictionary<string, object?>;

namespace MuxStudio.Core;

// ==========================================================================
//  AUDIO
// ==========================================================================

/// <summary>Một audio track của file (port <c>mux.AudioTrack</c>).</summary>
public sealed class AudioTrack(int id, string code, string codec, string channels, double bitrate, string bitrateSrc, string trackName)
{
    public int Id { get; } = id;
    public string Code { get; set; } = code;
    public string Codec { get; } = codec;
    public string Channels { get; } = channels;
    public double Bitrate { get; } = bitrate;
    public string BitrateSrc { get; } = bitrateSrc;
    public string TrackName { get; } = trackName;
    public bool Keep { get; set; } = true;

    /// <summary>Lý do XOÁ.</summary>
    public string Reason { get; set; } = "";

    public bool Default { get; set; }

    /// <summary>Tên track mới: <c>English E-AC-3 5.1</c> (§6.7).</summary>
    public string Label => string.Join(" ", new[] { Naming.LangName(Code), Codec, Channels }.Where(x => x.Length > 0));

    public string Describe() => $"#{Id.ToString(CultureInfo.InvariantCulture)}  {Label}  {Probe.FormatKbps(Bitrate)}"
                                + (TrackName.Length > 0 ? $"  [{TrackName}]" : "");
}

/// <summary>
/// <c>Select-AudioTracks</c> (port <c>mux.AudioRule</c>) — ghi nhớ = nhớ những gì GIỮ: ngôn ngữ giữ; ngôn ngữ vừa giữ vừa xoá thì nhớ
/// thêm hệ kênh được giữ ("English 5.1"). Nhờ vậy ngôn ngữ lạ ở file sau tự bị loại.
/// </summary>
public sealed class AudioRule(List<string> keepLangs, OrderedDictionary<string, List<string>>? keepSig = null)
{
    public List<string> KeepLangs { get; } = keepLangs;
    public OrderedDictionary<string, List<string>>? KeepSig { get; } = keepSig;

    public static AudioRule FromChoice(IReadOnlyList<AudioTrack> kept, IReadOnlyList<AudioTrack> dropped)
    {
        var keepLangs = kept.Select(a => a.Code).Distinct(StringComparer.Ordinal).ToList();
        var dropLangs = dropped.Select(a => a.Code).Distinct(StringComparer.Ordinal).ToList();
        var mixed = dropLangs.Where(keepLangs.Contains).ToList();
        OrderedDictionary<string, List<string>>? sig = null;
        if (mixed.Count > 0)
        {
            sig = new OrderedDictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var m in mixed)
                sig[m] = kept.Where(a => a.Code == m).Select(a => a.Channels).Distinct(StringComparer.Ordinal).ToList();
        }
        return new AudioRule(keepLangs, sig);
    }

    /// <summary>Track được giữ theo quy tắc; quy tắc làm mất HẾT audio → <c>null</c> (bỏ qua quy tắc cho file đó).</summary>
    public List<AudioTrack>? Apply(IEnumerable<AudioTrack> tracks)
    {
        var keep = new List<AudioTrack>();
        foreach (var a in tracks)
        {
            var ok = KeepLangs.Contains(a.Code);
            if (ok && KeepSig is not null && KeepSig.TryGetValue(a.Code, out var chs) && !chs.Contains(a.Channels))
                ok = false;
            if (ok)
                keep.Add(a);
        }
        return keep.Count > 0 ? keep : null;
    }

    /// <summary><c>Get-AudioRuleText</c>: <c>chỉ giữ English (5.1), Czech</c>.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        foreach (var lg in KeepLangs)
        {
            var n = Naming.LangName(lg) is { Length: > 0 } nm ? nm : lg;
            parts.Add(KeepSig is not null && KeepSig.TryGetValue(lg, out var chs) ? $"{n} ({string.Join("/", chs)})" : n);
        }
        return "chỉ giữ " + string.Join(", ", parts);
    }
}

/// <summary>Lựa chọn của bạn trong bảng audio của MỘT file (port <c>mux.AudioInput</c>).</summary>
public sealed class AudioInput(HashSet<int>? dropIds = null, bool keepAllDefaultHighest = false)
{
    public HashSet<int> DropIds { get; } = dropIds ?? [];

    /// <summary>"Giữ cả hai, Default = bitrate cao nhất" (Q3).</summary>
    public bool KeepAllDefaultHighest { get; } = keepAllDefaultHighest;
}

// ==========================================================================
//  PHỤ ĐỀ — nguồn quy tắc DUY NHẤT (Get-SubAutoPlan + BƯỚC 3/4/4b/5 của MuxTool)
// ==========================================================================

/// <summary>Phụ đề ngoài cạnh phim (port <c>mux.ExtSub</c>).</summary>
public sealed record ExtSub(string Path, string Code, int Rank)
{
    public string Label => Naming.LangName(Code) is { Length: > 0 } n ? n : "Không rõ";
}

/// <summary>Phụ đề trong MKV (port <c>mux.IntSub</c>).</summary>
public sealed class IntSub(int id, string code, bool sdh, bool forced, bool image, bool commentary, string codecId, string trackName)
{
    public int Id { get; } = id;
    public string Code { get; } = code;
    public bool Sdh { get; } = sdh;
    public bool Forced { get; } = forced;
    public bool Image { get; } = image;
    public bool Commentary { get; } = commentary;
    public string CodecId { get; } = codecId;
    public string TrackName { get; } = trackName;
    public bool Keep { get; set; } = true;
    public string Reason { get; set; } = "giữ";
    public bool Default { get; set; }

    /// <summary>Tên track: <c>Ngôn ngữ [SDH] [Forced]</c>.</summary>
    public string Label => string.Join(" ", new[] { Naming.LangName(Code), Sdh ? "SDH" : "", Forced ? "Forced" : "" }.Where(x => x.Length > 0));
}

/// <summary>Một ô của bảng chọn phụ đề theo tập (<c>Get-SubAutoPlan</c>). <see cref="Key"/>: cột <c>E|vi</c> (ngoài) /
/// <c>I|English SDH</c> (trong); <see cref="Id"/> = -1 với sub ngoài.</summary>
public sealed record SubItem(string Key, string Kind, int Id, string Code, bool AutoKeep, string Header, string Tip, int Order);

/// <summary>Kết quả bảng chọn phụ đề cho MỘT file: mã ngôn ngữ sub ngoài giữ + ID sub trong MKV giữ.</summary>
public sealed class SubChoice(IEnumerable<string>? ext = null, IEnumerable<int>? intIds = null)
{
    public HashSet<string> Ext { get; } = [.. ext ?? []];
    public HashSet<int> IntIds { get; } = [.. intIds ?? []];
}

// ==========================================================================
//  KẾ HOẠCH + KẾT QUẢ
// ==========================================================================

/// <summary>Kết quả mux một file (port <c>mux.MuxResult</c>).</summary>
public sealed class MuxResult
{
    public long Code { get; set; } = -1;
    public bool Ok { get; set; }
    public bool Warning { get; set; }
    public List<string> Output { get; set; } = [];
    public string? Out { get; set; }
}

/// <summary>File nguồn đã dùng cho file mux THÀNH CÔNG — đề xuất đưa vào Thùng rác sau khi mux (port <c>mux.UsedFile</c>).</summary>
public sealed class UsedFile(string path, string kind)
{
    public string Path { get; } = path;

    /// <summary><c>video</c> | <c>sub</c> | <c>donor</c>.</summary>
    public string Kind { get; } = kind;

    /// <summary>Tên file kết quả trong Output.</summary>
    public List<string> UsedFor { get; } = [];

    public string KindText => Kind switch
    {
        "video" => "Phim gốc",
        "sub" => "Phụ đề ngoài",
        "donor" => "Nguồn audio ghép",
        _ => Kind,
    };
}

/// <summary><c>mux.TrashReport</c>.</summary>
public sealed class TrashReport
{
    public List<string> Done { get; } = [];
    public List<string> Failed { get; } = [];
}

/// <summary>Kế hoạch mux của MỘT file (port <c>mux.MuxPlan</c>). <see cref="Status"/>: <c>ok</c> | <c>need_choice</c> | <c>skip</c> |
/// <c>exists</c> | <c>fail</c>.</summary>
public sealed class MuxPlan(string src, string rel, OutputName name)
{
    public string Src { get; } = src;
    public string Rel { get; } = rel;
    public OutputName Name { get; set; } = name;
    public JDict? Json { get; set; }
    public string Status { get; set; } = "ok";
    public List<string> Reasons { get; } = [];
    public List<string> Notes { get; } = [];
    public string OutBase { get; set; } = "";
    public string? OutPath { get; set; }
    public string CodecTag { get; set; } = "";
    public List<int> VideoIds { get; } = [];
    public OrderedDictionary<int, string> VideoNames { get; } = [];
    public List<AudioTrack> Audio { get; set; } = [];
    public OrderedDictionary<int, string> LangOverride { get; } = [];
    public int DefaultAudio { get; set; } = -1;
    public List<ExtSub> ExtSubs { get; set; } = [];
    public List<IntSub> IntSubs { get; set; } = [];
    public (string Kind, int Value) DefaultSub { get; set; } = ("", -1);
    public List<SubItem> SubItems { get; set; } = [];
    public DonorPlan Donor { get; set; } = new();
    public bool NeedsAudioTable { get; set; }
    public string RuleApplied { get; set; } = "";
    public bool Selected { get; set; } = true;
    public MuxResult? Result { get; set; }

    public List<int> KeepAudio => [.. Audio.Where(a => a.Keep).Select(a => a.Id)];

    public List<IntSub> KeepSubs => [.. IntSubs.Where(s => s.Keep)];

    public string StatusText => Status switch
    {
        "ok" => "sẵn sàng",
        "need_choice" => "Cần chọn",
        "skip" => "bỏ qua",
        "exists" => "đã có trong Output",
        "fail" => "lỗi",
        _ => Status,
    };
}

/// <summary><c>mux.MuxReport</c>.</summary>
public sealed class MuxReport
{
    public int Ok { get; set; }
    public int Warning { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }

    /// <summary>Còn "Cần chọn".</summary>
    public int Pending { get; set; }

    public bool Stopped { get; set; }
    public string? LogPath { get; set; }
}

/// <summary>Hàm cấp module của <c>core/mux.py</c> (không cần phiên).</summary>
public static partial class Mux
{
    public static readonly IReadOnlyList<string> MuxExtsDefault = [".mkv", ".mp4", ".m4v", ".avi", ".ts"];

    [GeneratedRegex("""[\s"&()\[\]{}^=;!'+,`~]""")]
    private static partial Regex NeedQuoteRe();

    /// <summary><c>_q</c>: đặt ngoặc kép cho tham số khi in lệnh ("Xem lệnh").</summary>
    public static string Q(string a) => a.Length == 0 || NeedQuoteRe().IsMatch(a) ? $"\"{a}\"" : a;

    [GeneratedRegex("forced", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForcedRe();

    /// <summary>
    /// <c>external_subs</c> — BƯỚC 3: <c>&lt;tên phim&gt;.&lt;mã&gt;.srt|ass|ssa|sup|vtt</c> cạnh phim, mỗi ngôn ngữ chọn theo
    /// <c>SubExtPriority</c>; không hậu tố = <c>DefaultExternalLang</c>. Thứ tự: theo <c>SubDefaultPriority</c> rồi các ngôn ngữ khác.
    /// TĐ-25: hậu tố đọc theo bảng ngôn ngữ chung.
    /// </summary>
    public static List<ExtSub> ExternalSubs(string video, Config cfg)
    {
        var prio = cfg.List("SubExtPriority").Select(x => x.ToLowerInvariant()).ToList();
        var keepLangs = cfg.List("SubKeepLangs").Select(x => x.ToLowerInvariant()).ToList();
        var b = Subprep.Stem(video);
        var byLang = new OrderedDictionary<string, ExtSub>(StringComparer.Ordinal);
        List<FileSystemInfo> entries;
        try
        {
            entries = [.. new DirectoryInfo(Subprep.Parent(video)).EnumerateFileSystemInfos().OrderBy(e => e.Name.ToLowerInvariant(), StringComparer.Ordinal)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        foreach (var e in entries)
        {
            if ((e.Attributes & FileAttributes.Directory) != 0 || !File.Exists(e.FullName))
                continue;
            var p = Py.PathJoin(Subprep.Parent(video), e.Name);
            var ext = Subprep.Suffix(p).ToLowerInvariant();
            var stem = Subprep.Stem(p);
            if (!prio.Contains(ext) || stem.Length < b.Length || stem[..b.Length].ToLowerInvariant() != b.ToLowerInvariant())
                continue;
            var suffix = stem[b.Length..];
            if (ForcedRe().IsMatch(suffix) && cfg.Bool("RemoveForcedSubs"))
                continue;
            var code = Py.StripWs(suffix).Length == 0 ? cfg.Str("DefaultExternalLang").ToLowerInvariant() : Naming.LangOfSuffix(suffix);
            if (code.Length == 0 || !keepLangs.Contains(code))
                continue;
            var rank = prio.IndexOf(ext);
            if (!byLang.TryGetValue(code, out var cur) || rank < cur.Rank)
                byLang[code] = new ExtSub(p, code, rank);
        }
        var pri = cfg.List("SubDefaultPriority").Select(x => x.ToLowerInvariant()).ToList();
        return [.. pri.Where(byLang.ContainsKey).Select(c => byLang[c]), .. byLang.Where(kv => !pri.Contains(kv.Key)).Select(kv => kv.Value)];
    }

    /// <summary><c>internal_subs</c> — BƯỚC 4: giữ ngôn ngữ trong <c>SubKeepLangs</c>; có sub ngoài ngôn ngữ X → xoá sub trong X; xoá
    /// Forced, phụ đề dạng ảnh, commentary, English SDH nếu đã có English thường.</summary>
    public static List<IntSub> InternalSubs(JDict? j, Config cfg, IReadOnlyCollection<string> extLangs)
    {
        var keepLangs = cfg.List("SubKeepLangs").Select(x => x.ToLowerInvariant()).ToList();
        var outp = new List<IntSub>();
        foreach (var t in Probe.Tracks(j, "subtitles"))
        {
            var p = t.GetDict("properties");
            outp.Add(new IntSub(Py.ToInt(t.Get("id", 0L)), Probe.TrackLang(t), Probe.IsSdh(p), Probe.IsForced(p),
                                Probe.IsImageSub(p.Get("codec_id") as string), Probe.IsCommentary(p), p.GetStr("codec_id"), p.GetStr("track_name")));
        }
        var hasCleanEng = outp.Any(s => s.Code == "en" && !s.Sdh && !s.Forced);
        foreach (var s in outp)
        {
            if (!keepLangs.Contains(s.Code))
                (s.Keep, s.Reason) = (false, "ngoài danh sách ngôn ngữ");
            else if (extLangs.Contains(s.Code))
                (s.Keep, s.Reason) = (false, "đã có sub ngoài thay thế");
            else if (s.Forced && cfg.Bool("RemoveForcedSubs"))
                (s.Keep, s.Reason) = (false, "Forced");
            else if (cfg.Bool("RemoveImageSubs") && s.Image)
                (s.Keep, s.Reason) = (false, "phụ đề dạng ảnh");
            else if (cfg.Bool("RemoveCommentarySubs") && s.Commentary)
                (s.Keep, s.Reason) = (false, "Commentary");
            else if (s.Code == "en" && s.Sdh && hasCleanEng)
                (s.Keep, s.Reason) = (false, "SDH (đã có English thường)");
            else if (s.Sdh && !hasCleanEng)
                s.Reason = "vớt vát (không có bản thường)";
        }
        return outp;
    }

    /// <summary><c>sub_auto_plan</c> = <c>Get-SubAutoPlan</c>: các ô của bảng chọn phụ đề cho MỘT file + quyết định tự động.</summary>
    public static List<SubItem> SubAutoPlan(string video, JDict? j, Config cfg)
    {
        var items = new List<SubItem>();
        var pri = cfg.List("SubDefaultPriority").Select(x => x.ToLowerInvariant()).ToList();
        var exts = ExternalSubs(video, cfg);
        foreach (var e in exts)
        {
            items.Add(new SubItem($"E|{e.Code}", "ext", -1, e.Code, true,
                                  $"NGOÀI\n{e.Label} ({Subprep.Suffix(e.Path).TrimStart('.').ToUpperInvariant()})",
                                  $"File ngoài: {Subprep.Name(e.Path)}", pri.Contains(e.Code) ? pri.IndexOf(e.Code) : 50));
        }
        List<string> extLangs = cfg.Bool("ExternalSubOverridesInternal") ? [.. exts.Select(e => e.Code)] : [];
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in InternalSubs(j, cfg, extLangs))
        {
            var label = Naming.LangName(s.Code) is { Length: > 0 } n ? n : "Không rõ";
            label += s.Sdh ? " SDH" : "";
            label += s.Forced ? " Forced" : "";
            label += s.Image ? " [ảnh]" : "";
            label += s.Commentary ? " [bình luận]" : "";
            var k = $"I|{label}";
            if (seen.TryGetValue(k, out var cnt)) // hai track cung nhan trong mot file -> cot rieng
            {
                seen[k] = cnt + 1;
                (k, label) = ($"{k} #{cnt + 1}", $"{label} #{cnt + 1}");
            }
            else
            {
                seen[k] = 1;
            }
            var nm = s.TrackName.Length > 0 ? $"  '{s.TrackName}'" : "";
            items.Add(new SubItem(k, "int", s.Id, s.Code, s.Keep, $"TRONG MKV\n{label}",
                                  $"Track #{s.Id.ToString(CultureInfo.InvariantCulture)}  {s.CodecId}{nm}  → tự động: {s.Reason}", 100));
        }
        return items;
    }

    /// <summary><c>pick_default_sub</c> — BƯỚC 5: Default theo <c>SubDefaultPriority</c> (vi &gt; en &gt; cs): sub ngoài trước, rồi sub
    /// trong không SDH, rồi sub trong bất kỳ. Trả (<c>ext</c>, chỉ số) | (<c>int</c>, ID) | ("", -1).</summary>
    public static (string Kind, int Value) PickDefaultSub(IReadOnlyList<ExtSub> exts, IEnumerable<IntSub> ints, IEnumerable<string> priority)
    {
        var kept = ints.Where(s => s.Keep).ToList();
        foreach (var code in priority.Select(x => x.ToLowerInvariant()))
        {
            for (var i = 0; i < exts.Count; i++)
            {
                if (exts[i].Code == code)
                    return ("ext", i);
            }
            var cand = kept.Where(s => s.Code == code && !s.Sdh).ToList();
            if (cand.Count == 0)
                cand = kept.Where(s => s.Code == code).ToList();
            if (cand.Count > 0)
                return ("int", cand[0].Id);
        }
        return ("", -1);
    }

    private static readonly Dictionary<string, string> TagAudio = new(StringComparer.Ordinal)
    {
        ["Dolby Digital"] = "AC3", ["E-AC-3"] = "EAC3", ["DTS-HD MA"] = "DTS-HD", ["DTS-HD HRA"] = "DTS-HD",
    };

    [GeneratedRegex("atmos", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AtmosRe();

    [GeneratedRegex(@"\batmos\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AtmosWordRe();

    /// <summary><c>codec_tag</c> = <c>Get-CodecTag</c>: <c>2160p DV HDR HEVC TrueHD7.1 Atmos</c>. Thẻ CHỈ gắn vào tên file sau
    /// <c> - </c>, không bao giờ vào tên thư mục.</summary>
    public static string CodecTag(JDict? j, JDict? videoStream, string origName, JDict? defAudio, IReadOnlyCollection<string> parts)
    {
        var outp = new List<string>();
        var vt = Probe.Tracks(j, "video").FirstOrDefault();
        if (vt is not null)
        {
            var vp = vt.GetDict("properties");
            if (parts.Contains("res") && Probe.ResolutionName(vp.Get("pixel_dimensions")) is { Length: > 0 } r)
                outp.Add(r);
            if (parts.Contains("hdr") && Probe.DynamicRange(videoStream, origName) is { Length: > 0 } d)
                outp.Add(d);
            if (parts.Contains("vcodec") && Probe.VideoCodecName(vp.Get("codec_id") as string, vt.GetStrOrNull("codec") ?? vp.GetStrOrNull("codec")) is { Length: > 0 } vc)
                outp.Add(vc);
        }
        if (parts.Contains("acodec") && defAudio is not null)
        {
            var ap = defAudio.GetDict("properties");
            var ac = Probe.AudioCodecName(defAudio);
            ac = TagAudio.GetValueOrDefault(ac, ac);
            var ch = Probe.ChannelName(ap.Get("audio_channels"));
            var atmos = AtmosRe().IsMatch($"{ap.GetStr("track_name")} {defAudio.GetStr("codec")}") || AtmosWordRe().IsMatch(origName) ? " Atmos" : "";
            if (ac.Length > 0)
                outp.Add(Py.StripWs($"{ac}{ch}{atmos}"));
        }
        return outp.Count > 0 ? Naming.SafeFilename(string.Join(" ", outp)) : "";
    }

    /// <summary>
    /// <c>used_sources</c>: file nguồn của các file đã mux THÀNH CÔNG (kết quả có trên đĩa): MKV gốc, phụ đề ngoài, file nguồn audio
    /// ghép. KHÔNG đề xuất: file vẫn cần cho dòng chưa mux / mux lỗi, file nằm trong <c>Output</c>, chính file kết quả, file không còn
    /// trên đĩa. Thứ tự: phim gốc → phụ đề → nguồn audio ghép.
    /// </summary>
    public static List<UsedFile> UsedSources(IEnumerable<MuxPlan> plans, string? outDir = null)
    {
        static string Key(string p) => Py.NormCase(Py.AbsPath(p));
        var pending = new HashSet<string>(StringComparer.Ordinal);
        var outs = new HashSet<string>(StringComparer.Ordinal);
        var used = new OrderedDictionary<string, UsedFile>(StringComparer.Ordinal);
        foreach (var p in plans)
        {
            var files = new List<(string F, string Kind)> { (p.Src, "video") };
            files.AddRange(p.ExtSubs.Select(e => (e.Path, "sub")));
            files.AddRange(p.Donor.Tracks.Select(t => (t.Source, "donor")));
            var r = p.Result;
            if (r is { Ok: true, Out: { } o } && File.Exists(o))
            {
                outs.Add(Key(o));
                foreach (var (f, kind) in files)
                {
                    var k = Key(f);
                    if (!used.TryGetValue(k, out var u))
                    {
                        u = new UsedFile(f, kind);
                        used[k] = u;
                    }
                    if (!u.UsedFor.Contains(Subprep.Name(o)))
                        u.UsedFor.Add(Subprep.Name(o));
                }
            }
            else
            {
                foreach (var (f, _) in files)
                    pending.Add(Key(f));
            }
        }
        var order = new Dictionary<string, int>(StringComparer.Ordinal) { ["video"] = 0, ["sub"] = 1, ["donor"] = 2 };
        return [.. used.Where(kv => !pending.Contains(kv.Key) && !outs.Contains(kv.Key) && File.Exists(kv.Value.Path)
                                    && !(!string.IsNullOrEmpty(outDir) && Tools.IsUnder(kv.Value.Path, outDir)))
                       .Select(kv => kv.Value)
                       .OrderBy(u => order.GetValueOrDefault(u.Kind, 9)).ThenBy(u => u.Path.ToLowerInvariant(), StringComparer.Ordinal)];
    }
}

/// <summary>
/// Bước ③ — Mux (thay MuxTool_v17.bat; port <c>mux.MuxSession</c>). docs/SPEC.md mục 4.3, 4.4; §6.2, 6.7–6.10. Mẫu "lập kế hoạch →
/// thực thi" (§4):
/// <list type="bullet">
/// <item><see cref="DbStatus"/> — tình trạng TMDB / TVDB.</item>
/// <item><see cref="PlanAll"/>(db) — <c>db</c> BẮT BUỘC chọn: <c>tmdb</c> | <c>tvdb</c> | <c>none</c>. Mỗi file một <see cref="MuxPlan"/>.
/// KHÔNG ghi gì, KHÔNG tạo thư mục (TĐ-21).</item>
/// <item>Sửa kế hoạch: <see cref="SetAudioChoice"/>, <see cref="KeepAllAudio"/>, <see cref="SetSubChoice"/>, <see cref="ChooseName"/> /
/// <see cref="KeepOriginalName"/>, <see cref="SetDonorOffset"/> → gọi lại <see cref="PlanFile"/> cho dòng đó.</item>
/// <item><see cref="PreviewCommands"/> — nút "Xem lệnh": đủ lệnh ffmpeg + mkvmerge, không ghi gì.</item>
/// <item><see cref="Execute"/> — mã thoát 0 = OK, 1 = OK có cảnh báo, ≥ 2 = lỗi → xoá file dở dang. Ghi ra tên tạm rồi mới đổi tên.
/// Nhật ký hoàn tác <c>_muxtool_logs\mux_*.csv</c>.</item>
/// </list>
/// </summary>
public sealed partial class MuxSession : IDisposable
{
    /// <param name="toolPaths">(cho test) mkvmerge / mkvextract / ffmpeg / ffprobe; bỏ trống = tự tìm.</param>
    /// <param name="runner">(cho test) thay <see cref="Tools.Run"/> (mkvmerge / ffmpeg / mkvextract giả).</param>
    /// <param name="mkvmergeVersion">(cho test) bỏ qua việc chạy <c>mkvmerge --version</c>.</param>
    public MuxSession(string targetDir, Config cfg, IReadOnlyDictionary<string, string>? secrets, Job? job = null, Http? http = null,
                      Action<double>? sleep = null, ToolPaths? toolPaths = null, ToolRunner? runner = null, ToolVersion? mkvmergeVersion = null)
    {
        Td = targetDir;
        Cfg = cfg;
        Job = job ?? new Job();
        secrets ??= new Dictionary<string, string>();
        Runner = runner ?? ((a, j, t) => Tools.Run(a, j, t));
        if (toolPaths is not null)
        {
            (Mkvmerge, Mkvextract, Ffmpeg, Ffprobe) = (toolPaths.Mkvmerge, toolPaths.Mkvextract, toolPaths.Ffmpeg, toolPaths.Ffprobe);
        }
        else
        {
            var paths = Tools.FindAllTools(cfg, Td);
            (Mkvmerge, Mkvextract, Ffmpeg, Ffprobe) = (paths["mkvmerge"], paths["mkvextract"], paths["ffmpeg"], paths["ffprobe"]);
        }
        Temp = Directory.CreateTempSubdirectory("muxstudio_mux_").FullName;
        Ver = mkvmergeVersion ?? (Mkvmerge is not null ? Tools.MkvmergeVersionOf(Runner([Mkvmerge, "--version"], Job, TimeSpan.FromSeconds(30)))
                                                       : new ToolVersion(false, 0, ""));
        var tk = secrets.GetValueOrDefault("TmdbApiKey", "");
        Tmdb = tk.Length > 0 ? new TmdbClient(tk, cfg.Str("TmdbLang"), cfg.Int("TmdbRetry"), http, sleep, Job) : null;
        var vk = secrets.GetValueOrDefault("TvdbApiKey", "");
        Tvdb = vk.Length > 0
            ? new TvdbClient(vk, secrets.GetValueOrDefault("TvdbPin", ""), cfg.Str("TvdbLang"), cfg.Str("TvdbSeasonType"), cfg.Int("TmdbRetry"), http, sleep, Job)
            : null;
        DonorPool = cfg.Bool("UseDonorAudio") ? Donor.DonorFiles(Td, cfg.Str("DonorFolder")) : [];
        TrackTimes = new TrackTimes(Mkvextract, Py.PathJoin(Temp, "subs"), Job, Py.Str(cfg["PgsTimingText"]), Runner);
        Log = new RunLog(Td, cfg.Str("MuxLogDir"), "mux", cfg.Int("KeepUndoRuns"));
    }

    public string Td { get; }
    public Config Cfg { get; }
    public Job Job { get; private set; }
    public ToolRunner Runner { get; set; }
    public string? Mkvmerge { get; set; }
    public string? Mkvextract { get; set; }
    public string? Ffmpeg { get; set; }
    public string? Ffprobe { get; set; }
    public string Temp { get; }
    public Dictionary<string, JDict?> JsonCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<(string Path, string Kind), List<JDict>> FfCache { get; } = [];
    public Dictionary<string, object?> NameCache { get; } = new(StringComparer.Ordinal);

    private ToolVersion _ver = new(false, 0, "");

    /// <summary>Phiên bản mkvmerge (đặt lại → tên cờ <see cref="FDef"/>/<see cref="FFor"/> theo bản đó).</summary>
    public ToolVersion Ver
    {
        get => _ver;
        set
        {
            _ver = value;
            (FDef, FFor) = Tools.MkvmergeFlagNames(value.Major);
        }
    }

    public string FDef { get; private set; } = "--default-track";
    public string FFor { get; private set; } = "--forced-track";

    /// <summary><c>_ui_lang</c>: <c>null</c> = chưa hỏi mkvmerge.</summary>
    public List<string>? UiLang { get; set; }

    public TmdbClient? Tmdb { get; }
    public TvdbClient? Tvdb { get; }

    /// <summary>Database đã chọn cho lượt này.</summary>
    public string Db { get; set; } = "";

    public AudioRule? AudioRule { get; set; }
    public Dictionary<string, AudioInput> AudioInputs { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, SubChoice> SubChoices { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> DonorOffsets { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, OutputName> NameOverrides { get; } = new(StringComparer.Ordinal);
    public bool MultiEnAuto { get; set; }
    public List<string> DonorPool { get; set; }
    public TrackTimes TrackTimes { get; set; }
    public RunLog Log { get; }

    /// <summary>(cho test) thay <see cref="Undo.Trash"/>.</summary>
    public Func<string, bool, bool> Trash { get; set; } = Undo.Trash;

    /// <summary><c>set_job</c>: giao diện — phiên sống qua nhiều lần bấm nút, mỗi lần một <see cref="Core.Job"/> mới.</summary>
    public void SetJob(Job job)
    {
        Job = job;
        TrackTimes.Job = job;
        if (Tmdb is not null)
            Tmdb.Job = job;
        if (Tvdb is not null)
            Tvdb.Job = job;
    }

    // ------------------------------------------------------------------ chung
    public string OutDir => Py.PathJoin(Td, "Output");

    public IReadOnlyDictionary<string, INameDb?> Dbs => new Dictionary<string, INameDb?>(StringComparer.Ordinal) { ["tmdb"] = Tmdb, ["tvdb"] = Tvdb };

    private static string Key(string p) => Py.NormCase(Py.AbsPath(p));

    public List<string> ExcludedDirs()
    {
        var c = Cfg;
        List<string> names = ["Output", c.Str("DonorFolder"), Tools.LegacyPoolFolder, c.Str("SubFolderName"), c.Str("ExtractFolder"),
                              c.Str("SubFetchLogDir"), c.Str("SubPrepLogDir"), c.Str("MuxLogDir")];
        return [.. names.Where(n => n.Length > 0).Select(n => Py.PathJoin(Td, n))];
    }

    /// <summary><c>scan</c>: quét file phim (cả thư mục con nếu bật); bỏ qua <c>Output</c> và thư mục audio nguồn (§5.3).</summary>
    public List<string> Scan()
    {
        var exts = Cfg.List("VideoExts");
        if (exts.Count == 0)
            exts = [.. Mux.MuxExtsDefault];
        var files = Tools.WalkFiles(Td, Cfg.Bool("ScanSubFolders"), exts, ExcludedDirs());
        if (DonorPool.Count > 0)
        {
            Job.Info(string.Create(CultureInfo.InvariantCulture, $"Ghép audio: {DonorPool.Count} file nguồn trong '{Cfg.Str("DonorFolder")}' → lấy ")
                     + string.Join(", ", Cfg.List("DonorLangs").Select(Naming.LangName)));
            if (string.IsNullOrEmpty(Mkvextract))
                Job.Warn("  [!] Thiếu mkvextract → không đo được độ lệch bằng phụ đề.");
            if (string.IsNullOrEmpty(Ffmpeg))
                Job.Warn("  [!] Thiếu ffmpeg → file nguồn khác FPS sẽ bị bỏ qua.");
        }
        return files;
    }

    /// <summary><c>db_status</c>: cạnh mỗi lựa chọn database: <i>sẵn sàng</i> / <i>chưa điền key</i> / <i>key bị từ chối</i> / <i>đăng
    /// nhập lỗi</i>.</summary>
    public OrderedDictionary<string, ConnStatus> DbStatus() => new(StringComparer.Ordinal)
    {
        ["tmdb"] = Tmdb?.Check() ?? new ConnStatus(null, "TMDB: chưa điền key"),
        ["tvdb"] = Tvdb?.Check() ?? new ConnStatus(null, "TVDB: chưa điền key"),
        ["none"] = new ConnStatus(true, "Không đổi tên — giữ tên file gốc"),
    };

    [GeneratedRegex(@"^\s*en_US\b", RegexOptions.Multiline)]
    private static partial Regex EnUsRe();

    /// <summary><c>ui_lang_args</c>: <c>--ui-language en_US</c> nếu bản mkvmerge có (log tiếng Anh dễ đọc).</summary>
    public List<string> UiLangArgs()
    {
        if (UiLang is null)
        {
            UiLang = [];
            if (!string.IsNullOrEmpty(Mkvmerge))
            {
                var r = Runner([Mkvmerge, "--ui-language", "list"], Job, TimeSpan.FromSeconds(30));
                if (r.Code == 0 && EnUsRe().IsMatch(r.Output))
                    UiLang = ["--ui-language", "en_US"];
            }
        }
        return [.. UiLang];
    }

    private JDict? JsonOf(string p) => Probe.MkvJson(p, Mkvmerge, Job, JsonCache);

    // ------------------------------------------------------------------ lap ke hoach

    /// <summary><c>plan_all</c>.</summary>
    public List<MuxPlan> PlanAll(string? db, IEnumerable<string>? files = null)
    {
        if (db is not ("tmdb" or "tvdb" or "none"))
            throw new ArgumentException("Phải chọn database đặt tên: tmdb, tvdb hoặc none (không có mặc định).", nameof(db));
        Db = db;
        var list = files?.ToList() ?? Scan();
        if (list.Count == 0)
        {
            Job.Error($"[!] Không tìm thấy file video nào trong: {Td}");
            return [];
        }
        if (string.IsNullOrEmpty(Mkvmerge))
        {
            Job.Error("[LỖI] Không tìm thấy mkvmerge. Chọn thư mục MKVToolNix trong Cài đặt.");
            return [];
        }
        if (!Ver.Ok)
        {
            Job.Error($"[LỖI] Tìm thấy mkvmerge nhưng KHÔNG chạy được: {Mkvmerge} — thường do thiếu DLL hoặc "
                      + "Windows chặn file tải từ Internet (chuột phải › Properties › Unblock).");
            return [];
        }
        if (string.IsNullOrEmpty(Ffprobe))
            Job.Warn("[!] Không tìm thấy ffprobe → bitrate chỉ là ƯỚC LƯỢNG theo codec/số kênh.");
        Job.Info(string.Create(CultureInfo.InvariantCulture, $"Tìm thấy {list.Count} file. Đặt tên theo: {(db != "none" ? db.ToUpperInvariant() : "giữ tên gốc")}"));
        var plans = new List<MuxPlan>();
        for (var i = 0; i < list.Count; i++)
        {
            Job.Check();
            Job.Progress(i + 1, list.Count, Subprep.Name(list[i]));
            plans.Add(PlanFile(list[i]));
        }
        int N(string k) => plans.Count(p => p.Status == k);
        Job.Head(string.Create(CultureInfo.InvariantCulture,
            $"KẾ HOẠCH: {N("ok")} sẵn sàng | {N("need_choice")} cần chọn | {N("skip") + N("exists")} bỏ qua | {N("fail")} lỗi"));
        return plans;
    }

    /// <summary><c>plan_file</c>.</summary>
    public MuxPlan PlanFile(string f)
    {
        var c = Cfg;
        var rel = Tools.IsUnder(f, Td) ? Path.GetRelativePath(Td, f) : Subprep.Name(f);
        var k = Key(f);
        var name = NameOverrides.GetValueOrDefault(k)
                   ?? Naming.ResolveOutputName(Subprep.Stem(f), Db.Length > 0 ? Db : "none", Dbs, c, NameCache);
        var plan = new MuxPlan(f, rel, name);
        Job.Head($"=== {rel}");
        if (name.Note.Length > 0 && (name.Status != "keep" || Db != "none"))
            Job.Log($"  [T] {name.Note}", name.Status == "ok" ? "ok" : "warn");
        if (name.EpisodeNote.Length > 0)
            Job.Info($"  [i] {name.EpisodeNote}");
        if (name.Status == "need_choice")
        {
            plan.Status = "need_choice";
            plan.Reasons.Add($"chọn tên: {name.Note}");
        }
        plan.OutBase = name.Base;
        var outDir = name.RelDir.Length > 0 ? Py.PathJoin(OutDir, name.RelDir) : OutDir;
        plan.OutPath = Py.PathJoin(outDir, $"{name.Base}.mkv");
        if (c.Bool("SkipIfOutputExists") && Already(plan, f) is { } hit)
        {
            plan.Status = "exists";
            plan.Reasons.Add($"đã có trong Output: {Subprep.Name(hit)}");
            Job.Dim($"  [>] Đã có trong Output: {Subprep.Name(hit)} → bỏ qua.");
            return plan;
        }
        var j = JsonOf(f);
        plan.Json = j;
        if (j is null)
        {
            plan.Status = "fail";
            plan.Reasons.Add("không đọc được cấu trúc file (hỏng hoặc không hỗ trợ)");
            Job.Error("  [!] Không đọc được cấu trúc file (hỏng hoặc không hỗ trợ).");
            return plan;
        }
        // BUOC 1: video
        foreach (var t in Probe.Tracks(j, "video"))
        {
            var id = Py.ToInt(t.Get("id", 0L));
            plan.VideoIds.Add(id);
            if (Naming.LangName(Probe.TrackLang(t)) is { Length: > 0 } nm)
                plan.VideoNames[id] = nm;
        }
        // BUOC 2: audio
        PlanAudio(plan, f, j);
        if (plan.Status == "skip")
            return plan;
        // BUOC 3-5: phu de
        PlanSubs(plan, f, j);
        // BUOC 5b: the codec
        if (c.Bool("AddCodecTag"))
        {
            if (name.IsEpisode && !c.Bool("CodecTagForTv"))
            {
                plan.Notes.Add("phim bộ → không gắn thẻ codec");
            }
            else
            {
                var vs = Probe.FfprobeStreams(f, Ffprobe, "v:0", Job, FfCache);
                var defa = Probe.Tracks(j, "audio").FirstOrDefault(t => Py.ToInt(t.Get("id", -1L)) == plan.DefaultAudio);
                var tag = Mux.CodecTag(j, vs.Count > 0 ? vs[0] : null, Subprep.Stem(f), defa, c.List("CodecTagParts"));
                if (tag.Length > 0)
                {
                    plan.CodecTag = tag;
                    plan.OutBase = $"{name.Base} - {tag}";
                    plan.OutPath = Py.PathJoin(outDir, $"{plan.OutBase}.mkv");
                    Job.Info($"  [#] Thẻ codec: {tag}");
                }
            }
        }
        // BUOC 5c: audio ghep
        var have = plan.Audio.Where(a => a.Keep).Select(a => plan.LangOverride.GetValueOrDefault(a.Id, a.Code)).ToList();
        plan.Donor = Donor.PlanDonor(f, j, have, DonorPool, c, JsonOf, TrackTimes, !string.IsNullOrEmpty(Ffmpeg),
                                     DonorOffsets.TryGetValue(k, out var off) ? off : null, Job);
        if (plan.Donor.Tracks.Count > 0)
        {
            var tm = plan.Donor.Timing;
            Job.Info($"  [~] Ghép audio từ: {(plan.Donor.Donor is { } dn ? Subprep.Name(dn) : "")}");
            if (tm is not null)
            {
                var ms = plan.Donor.Tracks[0].OffsetMs;
                Job.Log(string.Create(CultureInfo.InvariantCulture, $"      Lệch: {(ms >= 0 ? "+" : "")}{ms} ms | {tm.How}"), tm.Sure ? "dim" : "warn");
                if (plan.Donor.Tracks[0].Stretch)
                    Job.Warn($"      Khác FPS: {tm.Name} → sẽ đổi tốc độ audio (encode lại)");
            }
        }
        else if (plan.Donor.Note.Length > 0)
        {
            Job.Dim($"  [-] Ghép audio: {plan.Donor.Note}");
        }
        return plan;
    }

    private static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    /// <summary><c>_already</c>: <c>SkipIfOutputExists</c> — đã có (tên mới, tên gốc của lần trước, hoặc bản có thẻ codec <c>&lt;tên&gt; - …</c>).</summary>
    private string? Already(MuxPlan plan, string f)
    {
        var op = plan.OutPath!;
        if (Exists(op))
            return op;
        var legacy = Py.PathJoin(OutDir, $"{Subprep.Stem(f)}.mkv");
        if (Exists(legacy))
            return legacy;
        var d = Subprep.Parent(op);
        if (Cfg.Bool("AddCodecTag") && Directory.Exists(d))
        {
            var pre = $"{plan.OutBase} - ".ToLowerInvariant();
            foreach (var e in new DirectoryInfo(d).EnumerateFileSystemInfos().OrderBy(e => e.Name.ToLowerInvariant(), StringComparer.Ordinal))
            {
                var low = e.Name.ToLowerInvariant();
                if ((e.Attributes & FileAttributes.Directory) == 0 && File.Exists(e.FullName) && low.EndsWith(".mkv", StringComparison.Ordinal)
                    && low.StartsWith(pre, StringComparison.Ordinal))
                    return Py.PathJoin(d, e.Name);
            }
        }
        return null;
    }

    private void PlanAudio(MuxPlan plan, string f, JDict j)
    {
        var c = Cfg;
        var tr = Probe.Tracks(j, "audio");
        if (tr.Count == 0)
        {
            plan.Notes.Add("không có audio track nào");
            Job.Warn("  [!] File không có audio track nào.");
            return;
        }
        var ff = Probe.FfprobeBitrates(Probe.FfprobeStreams(f, Ffprobe, "a", Job, FfCache));
        var allt = new List<AudioTrack>();
        foreach (var t in tr)
        {
            var p = t.GetDict("properties");
            var (br, src) = Probe.AudioBitrate(t, ff);
            var a = new AudioTrack(Py.ToInt(t.Get("id", 0L)), Probe.TrackLang(t), Probe.AudioCodecName(t), Probe.ChannelName(p.Get("audio_channels")),
                                   br, src, p.GetStr("track_name"));
            if (c.Bool("RemoveCommentaryAudio") && Probe.IsCommentary(p))
                (a.Keep, a.Reason) = (false, "Commentary");
            else if (c.Bool("RemoveDescriptiveAudio") && Probe.IsDescriptive(p))
                (a.Keep, a.Reason) = (false, "Audio Description");
            allt.Add(a);
        }
        plan.Audio = allt;
        var info = allt.Where(a => a.Keep).ToList();
        if (info.Count == 0)
        {
            Job.Warn("  [!] Tất cả audio đều bị nhận diện là bình luận → giữ nguyên toàn bộ cho an toàn.");
            foreach (var a in allt)
                (a.Keep, a.Reason) = (true, "");
            info = [.. allt];
        }
        foreach (var a in allt.Where(a => !a.Keep))
            Job.Dim($"  [-] Audio  #{a.Id.ToString(CultureInfo.InvariantCulture)}: XOÁ - {a.Reason}" + (a.TrackName.Length > 0 ? $" ('{a.TrackName}')" : ""));
        plan.NeedsAudioTable = c.Bool("AskWhichAudio") && info.Count >= c.Int("AskAudioMinTracks");
        var inp = AudioInputs.GetValueOrDefault(Key(f));
        if (inp is not null)
        {
            var keep = info.Where(a => !inp.DropIds.Contains(a.Id)).ToList();
            if (keep.Count == 0)
            {
                Job.Warn("  [!] Không thể xoá HẾT audio → giữ nguyên tất cả.");
                keep = info;
            }
            foreach (var a in info.Where(a => !keep.Contains(a)))
                (a.Keep, a.Reason) = (false, "theo lựa chọn của bạn");
            info = keep;
        }
        else if (AudioRule is not null)
        {
            var keep2 = AudioRule.Apply(info);
            if (keep2 is null)
            {
                Job.Warn("  [!] Quy tắc đã nhớ sẽ xoá hết audio của file này → bỏ qua quy tắc, giữ tất cả.");
            }
            else
            {
                plan.RuleApplied = AudioRule.Describe();
                Job.Dim($"  [i] Áp dụng quy tắc đã nhớ: {plan.RuleApplied}");
                foreach (var a in info.Where(a => !keep2.Contains(a)))
                    (a.Keep, a.Reason) = (false, "theo quy tắc");
                info = keep2;
            }
        }
        // und
        var und = info.Where(a => a.Code == "und").ToList();
        if (und.Count > 0)
        {
            if (info.Count == 1)
            {
                info[0].Code = "en";
                plan.LangOverride[info[0].Id] = "en";
                Job.Warn(string.Create(CultureInfo.InvariantCulture, $"  [*] Audio #{info[0].Id}: ngôn ngữ 'und' + chỉ có 1 track → đặt thành 'en'"));
            }
            else if (c.Bool("SkipOnAmbiguousUnd"))
            {
                plan.Status = "skip";
                plan.Reasons.Add(string.Create(CultureInfo.InvariantCulture, $"có {info.Count} audio, trong đó {und.Count} track không rõ ngôn ngữ ('und') — ")
                                 + "hãy gán ngôn ngữ bằng MKVToolNix GUI rồi chạy lại");
                Job.Warn($"  [!] {plan.Reasons[^1]} → BỎ QUA file này.");
                return;
            }
        }
        // >= 2 audio tieng Anh (Q3 / TD-23)
        var en = info.Where(a => a.Code == "en").ToList();
        var keepAll = (inp is not null && inp.KeepAllDefaultHighest) || MultiEnAuto;
        var userChoice = inp is not null && (inp.DropIds.Count > 0 || inp.KeepAllDefaultHighest);
        if (en.Count > 1 && c.Bool("SkipOnMultipleEnAudio") && !(keepAll || userChoice))
        {
            plan.Status = "need_choice";
            plan.Reasons.Add(string.Create(CultureInfo.InvariantCulture, $"có {en.Count} audio tiếng Anh (#")
                             + string.Join(", #", en.Select(a => a.Id.ToString(CultureInfo.InvariantCulture))) + ") — tick "
                             + "track muốn xoá trong bảng audio, hoặc bấm 'Giữ cả hai, Default = bitrate cao nhất'");
            Job.Warn($"  [!] {plan.Reasons[^1]}");
        }
        foreach (var a in info)
            Job.Ok($"  [+] Audio  #{a.Id.ToString(CultureInfo.InvariantCulture)}: GIỮ - {a.Label}  {Probe.FormatKbps(a.Bitrate)}");
        if (en.Count == 1)
            plan.DefaultAudio = en[0].Id;
        else if (en.Count > 1)
            plan.DefaultAudio = en.OrderBy(a => -a.Bitrate).First().Id;
        else if (info.Count > 0)
            plan.DefaultAudio = info[0].Id;
        foreach (var a in allt)
            a.Default = a.Id == plan.DefaultAudio;
    }

    private void PlanSubs(MuxPlan plan, string f, JDict j)
    {
        var c = Cfg;
        var exts = Mux.ExternalSubs(f, c);
        List<string> extLangs = c.Bool("ExternalSubOverridesInternal") ? [.. exts.Select(e => e.Code)] : [];
        if (extLangs.Count > 0)
            Job.Info("  [~] Có sub ngoài (" + string.Join(", ", extLangs.Select(Naming.LangName)) + ") → thay thế toàn bộ sub trong MKV cùng ngôn ngữ.");
        var ints = Mux.InternalSubs(j, c, extLangs);
        plan.SubItems = Mux.SubAutoPlan(f, j, c);
        if (SubChoices.GetValueOrDefault(Key(f)) is { } ch) // bang chon phu de theo tap
        {
            exts = exts.Where(e => ch.Ext.Contains(e.Code)).ToList();
            foreach (var s in ints)
            {
                s.Keep = ch.IntIds.Contains(s.Id);
                s.Reason = s.Keep ? "theo bảng chọn" : "bỏ theo bảng chọn";
            }
            var shown = exts.Select(e => $"{e.Label} (ngoài)").Concat(ints.Where(s => s.Keep).Select(s => $"{s.Label} (#{s.Id.ToString(CultureInfo.InvariantCulture)})")).ToList();
            Job.Info("  [#] THEO BẢNG CHỌN → giữ: " + (shown.Count > 0 ? string.Join(", ", shown) : "không giữ phụ đề nào"));
        }
        foreach (var s in ints)
        {
            var id = s.Id.ToString(CultureInfo.InvariantCulture);
            if (s.Keep)
                Job.Ok($"  [+] Sub    #{id} : GIỮ - {s.Label}" + (s.Reason is not ("giữ" or "theo bảng chọn") ? $" ({s.Reason})" : ""));
            else
                Job.Dim($"  [-] Sub    #{id} : XOÁ - {(Naming.LangName(s.Code) is { Length: > 0 } n ? n : s.Code)} ({s.Reason})");
        }
        (plan.ExtSubs, plan.IntSubs) = (exts, ints);
        plan.DefaultSub = Mux.PickDefaultSub(exts, ints, c.List("SubDefaultPriority"));
        var (kind, val) = plan.DefaultSub;
        foreach (var s in ints)
            s.Default = kind == "int" && s.Id == val;
        for (var i = 0; i < exts.Count; i++)
        {
            var e = exts[i];
            Job.Info($"  [+] Sub ngoài: {e.Label} ({Subprep.Suffix(e.Path).TrimStart('.').ToUpperInvariant()})"
                     + (kind == "ext" && i == val ? " [*Default]" : ""));
        }
    }

    // ------------------------------------------------------------------ sua ke hoach

    /// <summary><c>set_audio_choice</c>: bảng audio của một file — tick track muốn XOÁ. <paramref name="applyToRest"/> (ô "Áp dụng cho mọi
    /// file còn lại", mặc định theo <c>RememberAudioChoice</c>) → dựng quy tắc "nhớ cái GIỮ" cho các file sau.</summary>
    public AudioRule? SetAudioChoice(MuxPlan plan, IEnumerable<int> dropIds, bool? applyToRest = null)
    {
        var drop = dropIds.ToHashSet();
        AudioInputs[Key(plan.Src)] = new AudioInput([.. drop]);
        var mode = Py.Str(Cfg["RememberAudioChoice"]);
        var apply = applyToRest ?? mode is "ask" or "always";
        if (mode == "never")
            apply = false;
        AudioRule? rule = null;
        if (apply && drop.Count > 0)
        {
            var info = plan.Audio.Where(a => a.Reason is not ("Commentary" or "Audio Description")).ToList();
            var kept = info.Where(a => !drop.Contains(a.Id)).ToList();
            var dropped = info.Where(a => drop.Contains(a.Id)).ToList();
            if (kept.Count > 0)
            {
                rule = AudioRule.FromChoice(kept, dropped);
                AudioRule = rule;
                Job.Info($"  [i] Đã nhớ: {rule.Describe()}");
            }
        }
        return rule;
    }

    /// <summary><c>keep_all_audio</c>: Q3 — "Giữ cả hai, Default = bitrate cao nhất" cho một file (<paramref name="plan"/>) hoặc cho mọi file (<c>null</c>).</summary>
    public void KeepAllAudio(MuxPlan? plan = null)
    {
        if (plan is null)
            MultiEnAuto = true;
        else
            AudioInputs[Key(plan.Src)] = new AudioInput([], true);
    }

    public void SetSubChoice(MuxPlan plan, IEnumerable<string> extCodes, IEnumerable<int> intIds) =>
        SubChoices[Key(plan.Src)] = new SubChoice(extCodes, intIds);

    /// <summary>Nút "Trả về mặc định" của bảng chọn phụ đề.</summary>
    public void ResetSubChoices() => SubChoices.Clear();

    public void SetDonorOffset(MuxPlan plan, double? ms)
    {
        var k = Key(plan.Src);
        if (ms is null)
            DonorOffsets.Remove(k);
        else
            DonorOffsets[k] = ms.Value;
    }

    /// <summary><c>choose_name</c>: dòng "Cần chọn" — bạn chọn một kết quả database. Mọi file cùng khoá (cùng series) dùng luôn lựa chọn.</summary>
    public void ChooseName(MuxPlan plan, long candId)
    {
        if (Dbs.GetValueOrDefault(plan.Name.Db) is not { } db)
            return;
        var outp = Naming.ChooseCandidate(db, Subprep.Stem(plan.Src), candId, plan.Name, Cfg, NameCache);
        NameOverrides.Remove(Key(plan.Src));
        if (outp.Status != "ok")
            NameOverrides[Key(plan.Src)] = outp;
    }

    /// <summary><c>search_name</c>: "Tìm lại với tên…" — tìm lại trên database đã chọn cho lượt này; dòng thành "Cần chọn" với kết quả mới
    /// (gọi <see cref="PlanFile"/> lại để thấy). Chưa chọn database / database không dùng được → <c>null</c>.</summary>
    public OutputName? SearchName(MuxPlan plan, string title, int? year = null)
    {
        var db = Dbs.GetValueOrDefault(Db);
        if (!Naming.DbUsable(db))
            return null;
        var outp = Naming.SearchCandidates(db!, Subprep.Stem(plan.Src), plan.Name, title, year);
        NameOverrides[Key(plan.Src)] = outp;
        return outp;
    }

    public void KeepOriginalName(MuxPlan plan) =>
        NameOverrides[Key(plan.Src)] = Naming.KeepName(Subprep.Stem(plan.Src), "Giữ tên file gốc (bạn chọn).", plan.Name.Kind);

    /// <summary><c>keep_original_names</c>: bấm "Bắt đầu" khi còn dòng "Cần chọn" TÊN → (bạn đồng ý) giữ tên gốc cho các dòng đó.</summary>
    public List<MuxPlan> KeepOriginalNames(IEnumerable<MuxPlan> plans)
    {
        var outp = new List<MuxPlan>();
        foreach (var p0 in plans)
        {
            var p = p0;
            if (p.Name.Status == "need_choice")
            {
                KeepOriginalName(p);
                p = PlanFile(p.Src);
            }
            outp.Add(p);
        }
        return outp;
    }

    /// <summary><c>set_name</c>: bạn sửa tay tên mới trên bảng tổng quan.</summary>
    public void SetName(MuxPlan plan, string newBase)
    {
        var b = Naming.SafeFilename(newBase);
        if (b.Length > 0)
            NameOverrides[Key(plan.Src)] = new OutputName(b, plan.Name.RelDir, plan.Name.Db, "ok", "Tên do bạn sửa.", plan.Name.Kind);
    }

    public List<MuxPlan> Replan(IEnumerable<MuxPlan> plans) => [.. plans.Select(p => PlanFile(p.Src))];

    // ------------------------------------------------------------------ dung lenh mkvmerge (SPEC 4.4)

    /// <summary><c>build_args</c>: danh sách tham số mkvmerge (không gồm đường dẫn mkvmerge) — đúng 8 quy tắc ở SPEC 4.4.</summary>
    public List<string> BuildArgs(MuxPlan plan, string? outPath = null)
    {
        var c = Cfg;
        var j = plan.Json;
        var outp = outPath ?? plan.OutPath!;
        var (fDef, fFor) = (FDef, FFor);
        var major = Ver.Major;
        static string I(int x) => x.ToString(CultureInfo.InvariantCulture);
        var audioAll = Probe.Tracks(j, "audio");
        var subAll = Probe.Tracks(j, "subtitles");
        var keepAudio = plan.KeepAudio;
        var keepSubs = plan.KeepSubs;
        var donorTracks = plan.Donor.Tracks;
        var donorDefault = c.Bool("DonorSetDefault") && donorTracks.Count > 0;
        var cmd = new List<string> { "-o", outp };
        if (c.Bool("SetTitleFromName"))
            cmd.AddRange(["--title", plan.OutBase]);
        if (audioAll.Count > 0 && keepAudio.Count > 0 && keepAudio.Count < audioAll.Count)
            cmd.AddRange(["--audio-tracks", string.Join(",", keepAudio.Select(I))]);
        if (subAll.Count > 0)
        {
            if (keepSubs.Count == 0)
                cmd.Add("--no-subtitles");
            else if (keepSubs.Count < subAll.Count)
                cmd.AddRange(["--subtitle-tracks", string.Join(",", keepSubs.Select(s => I(s.Id)))]);
        }
        foreach (var (tid, code) in plan.LangOverride)
            cmd.AddRange(["--language", $"{I(tid)}:{Naming.LangForMux(code, major)}"]);
        var names = new OrderedDictionary<int, string>(plan.VideoNames);
        foreach (var a in plan.Audio.Where(a => a.Keep && a.Label.Length > 0))
            names[a.Id] = a.Label;
        foreach (var s in keepSubs.Where(s => s.Label.Length > 0))
            names[s.Id] = s.Label;
        foreach (var tid in plan.VideoIds.Concat(keepAudio).Concat(keepSubs.Select(s => s.Id)))
        {
            if (names.TryGetValue(tid, out var nm) && nm.Length > 0)
                cmd.AddRange(["--track-name", $"{I(tid)}:{nm}"]);
        }
        foreach (var tid in keepAudio)
            cmd.AddRange([fDef, $"{I(tid)}:{(tid == plan.DefaultAudio && !donorDefault ? 1 : 0)}"]);
        var (dk, dv) = plan.DefaultSub;
        foreach (var s in keepSubs)
            cmd.AddRange([fDef, $"{I(s.Id)}:{(dk == "int" && s.Id == dv ? 1 : 0)}", fFor, $"{I(s.Id)}:{(s.Forced ? 1 : 0)}"]);
        cmd.Add(plan.Src);
        for (var i = 0; i < plan.ExtSubs.Count; i++)
        {
            var e = plan.ExtSubs[i];
            var cs = TextEncoding.MkvmergeCharset(e.Path, e.Code);
            if (cs.Length > 0)
                cmd.AddRange(["--sub-charset", $"0:{cs}"]);
            cmd.AddRange(["--language", $"0:{Naming.LangForMux(e.Code, major)}", "--track-name", $"0:{e.Label}",
                          fDef, $"0:{(dk == "ext" && i == dv ? 1 : 0)}", fFor, "0:0", e.Path]);
        }
        var donorBase = 1 + plan.ExtSubs.Count;
        for (var k = 0; k < donorTracks.Count; k++)
        {
            var dt = donorTracks[k];
            var tid = I(dt.InputTid);
            cmd.AddRange(["--no-video", "--no-subtitles", "--no-chapters", "--no-attachments", "--no-global-tags",
                          "--audio-tracks", tid, "--language", $"{tid}:{Naming.LangForMux(dt.Code, major)}",
                          "--track-name", $"{tid}:{dt.Name}", fDef, $"{tid}:{(donorDefault && k == 0 ? 1 : 0)}", fFor, $"{tid}:0"]);
            if (dt.OffsetMs != 0)
                cmd.AddRange(["--sync", $"{tid}:{I(dt.OffsetMs)}"]);
            cmd.Add(dt.InputPath);
        }
        if (c.Bool("UseTrackOrder"))
        {
            var order = plan.VideoIds.Select(v => $"0:{I(v)}").ToList();
            var donorOrder = donorTracks.Select((dt, k) => $"{I(donorBase + k)}:{I(dt.InputTid)}").ToList();
            if (donorDefault)
                order.AddRange(donorOrder);
            if (plan.DefaultAudio >= 0 && keepAudio.Contains(plan.DefaultAudio))
                order.Add($"0:{I(plan.DefaultAudio)}");
            order.AddRange(keepAudio.Where(a => a != plan.DefaultAudio).Select(a => $"0:{I(a)}"));
            if (!donorDefault)
                order.AddRange(donorOrder);
            if (dk == "ext")
                order.Add($"{I(dv + 1)}:0");
            else if (dk == "int")
                order.Add($"0:{I(dv)}");
            foreach (var code in c.List("SubDefaultPriority").Select(x => x.ToLowerInvariant()))
            {
                for (var i = 0; i < plan.ExtSubs.Count; i++)
                {
                    if (plan.ExtSubs[i].Code == code && !(dk == "ext" && i == dv))
                        order.Add($"{I(i + 1)}:0");
                }
                foreach (var s in keepSubs)
                {
                    if (s.Code == code && !(dk == "int" && s.Id == dv))
                        order.Add($"0:{I(s.Id)}");
                }
            }
            foreach (var s in keepSubs)
            {
                if (!order.Contains($"0:{I(s.Id)}"))
                    order.Add($"0:{I(s.Id)}");
            }
            if (order.Count > 0)
                cmd.InsertRange(2, ["--track-order", string.Join(",", order)]);
        }
        return cmd;
    }

    /// <summary><c>preview_commands</c>: nút "Xem lệnh" — ffmpeg (đổi tốc độ audio ghép) + mkvmerge; đường dẫn tạm ghi rõ, không ghi file nào.</summary>
    public List<string> PreviewCommands(MuxPlan plan)
    {
        if (plan.Status is not ("ok" or "need_choice") || plan.Json is null)
            return [];
        var lines = new List<string>();
        var tmpdir = Py.Join("<thư mục tạm>", "");
        var saved = plan.Donor.Tracks.Select(dt => (dt, dt.Prepared)).ToList();
        try
        {
            for (var n = 0; n < plan.Donor.Tracks.Count; n++)
            {
                var dt = plan.Donor.Tracks[n];
                if (dt.Stretch)
                {
                    var fake = Py.PurePath($"{tmpdir}a_{n.ToString(CultureInfo.InvariantCulture)}.mka");
                    lines.Add(string.Join(" ", dt.FfmpegArgs(Ffmpeg ?? "ffmpeg", fake).Select(Mux.Q)));
                    dt.Prepared = fake;
                }
            }
            List<string> argv = [Mkvmerge ?? "mkvmerge", .. UiLangArgs(), .. BuildArgs(plan)];
            lines.Add(string.Join(" ", argv.Select(Mux.Q)));
        }
        finally
        {
            foreach (var (dt, p) in saved)
                dt.Prepared = p;
        }
        return lines;
    }

    // ------------------------------------------------------------------ thuc thi
    private void EnsureDir(string d)
    {
        if (Exists(d))
            return;
        var missing = new List<string>();
        var p = d;
        while (!Exists(p))
        {
            missing.Add(p);
            var parent = Path.GetDirectoryName(p);
            if (string.IsNullOrEmpty(parent) || parent == p)
                break;
            p = parent;
        }
        Directory.CreateDirectory(d);
        for (var i = missing.Count - 1; i >= 0; i--)
            Log.Add(Undo.Mkdir, "", missing[i]);
    }

    /// <summary><c>execute</c>.</summary>
    public MuxReport Execute(IReadOnlyList<MuxPlan> plans)
    {
        var rep = new MuxReport();
        try
        {
            for (var n = 0; n < plans.Count; n++)
            {
                var p = plans[n];
                if (Job.ShouldStop())
                {
                    rep.Stopped = true;
                    break;
                }
                Job.Progress(n + 1, plans.Count, Subprep.Name(p.Src));
                if (!p.Selected)
                {
                    rep.Skipped++;
                    continue;
                }
                if (p.Status == "need_choice")
                {
                    rep.Pending++;
                    Log.Add("Skip", p.Src, "", string.Join("; ", p.Reasons), "Cần chọn");
                    continue;
                }
                if (p.Status != "ok")
                {
                    if (p.Status == "fail")
                        rep.Failed++;
                    else
                        rep.Skipped++;
                    Log.Add(p.Status != "fail" ? "Skip" : "Fail", p.Src, "", string.Join("; ", p.Reasons), p.Status);
                    continue;
                }
                var res = MuxOne(p);
                p.Result = res;
                if (res.Ok)
                {
                    rep.Ok++;
                    rep.Warning += res.Warning ? 1 : 0;
                }
                else
                {
                    rep.Failed++;
                }
            }
        }
        catch (OperationCanceledException)
        {
            rep.Stopped = true;
            Job.Warn("[!] Đã dừng ngay — file đang mux dở đã bị xoá.");
        }
        finally
        {
            try
            {
                Directory.Delete(Py.PathJoin(Temp, "donor"), recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
            rep.LogPath = Log.Save();
        }
        Job.Head(string.Create(CultureInfo.InvariantCulture, $"TỔNG KẾT: Thành công: {rep.Ok}")
                 + (rep.Warning > 0 ? string.Create(CultureInfo.InvariantCulture, $" ({rep.Warning} có cảnh báo)") : "")
                 + string.Create(CultureInfo.InvariantCulture, $" | Bỏ qua: {rep.Skipped} | Cần chọn: {rep.Pending} | Lỗi: {rep.Failed}"));
        if (rep.LogPath is not null)
            Job.Info($"Nhật ký (để hoàn tác): {Cfg.Str("MuxLogDir")}\\{Subprep.Name(rep.LogPath)}");
        return rep;
    }

    /// <summary><c>trash_sources</c>: đưa file nguồn đã dùng vào Thùng rác (tắt <c>DeleteToRecycleBin</c> → xoá hẳn), ghi <c>Delete</c> vào
    /// nhật ký của lượt mux này.</summary>
    public TrashReport TrashSources(IEnumerable<object> files)
    {
        var rb = Cfg.Bool("DeleteToRecycleBin");
        var rep = new TrashReport();
        foreach (var f in files)
        {
            var path = f is UsedFile u ? u.Path : (string)f;
            if (!File.Exists(path))
                continue;
            if (Trash(path, rb))
            {
                rep.Done.Add(path);
                Log.Add(Undo.Delete, path, "", "file nguồn đã mux xong" + (rb ? " → Thùng rác" : " → XOÁ HẲN"));
                Job.Ok($"  [🗑] {Subprep.Name(path)}");
            }
            else
            {
                rep.Failed.Add(path);
                Job.Error($"  [!] Không xoá được: {path}");
            }
        }
        if (rep.Done.Count > 0)
            Log.Save();
        Job.Head(string.Create(CultureInfo.InvariantCulture, $"DỌN FILE NGUỒN: {rep.Done.Count} file ") + (rb ? "vào Thùng rác" : "đã XOÁ HẲN")
                 + (rep.Failed.Count > 0 ? string.Create(CultureInfo.InvariantCulture, $" | {rep.Failed.Count} lỗi") : ""));
        return rep;
    }

    [GeneratedRegex("warning", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WarningRe();

    private static void TryDelete(string p)
    {
        try
        {
            if (File.Exists(p))
                File.Delete(p);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private MuxResult MuxOne(MuxPlan p)
    {
        var outPath = p.OutPath!;
        var res = new MuxResult();
        Job.Head($"=== {p.Rel}");
        // 1. audio ghep khac FPS -> ffmpeg ra thu muc tam
        var keepTracks = new List<DonorTrack>();
        foreach (var dt in p.Donor.Tracks)
        {
            if (dt.Stretch && !Donor.Prepare(dt, Ffmpeg ?? "ffmpeg", Py.PathJoin(Temp, "donor"), Job, Runner))
                continue;
            keepTracks.Add(dt);
        }
        p.Donor.Tracks = keepTracks;
        // 2. thu muc dich (tao luc thuc thi, co nhat ky)
        EnsureDir(Subprep.Parent(outPath));
        var tmp = Py.PathJoin(Subprep.Parent(outPath), Subprep.Stem(outPath) + ".muxstudio-tmp.mkv");
        TryDelete(tmp);
        List<string> argv = [Mkvmerge ?? "mkvmerge", .. UiLangArgs(), .. BuildArgs(p, tmp)];
        RunResult r;
        try
        {
            r = Runner(argv, Job, null);
        }
        catch (OperationCanceledException)
        {
            TryDelete(tmp);
            throw;
        }
        res.Code = r.Code;
        res.Output = Py.SplitLines(r.Output);
        if (r.Code is 0 or 1 && File.Exists(tmp))
        {
            if (Exists(outPath))
            {
                if (Trash(outPath, Cfg.Bool("DeleteToRecycleBin")))
                {
                    Log.Add(Undo.Delete, outPath, "", "thay bằng kết quả mux mới");
                }
                else
                {
                    TryDelete(tmp);
                    Job.Error($"  [!] Không dọn được file cũ: {Subprep.Name(outPath)}");
                    Log.Add("Fail", p.Src, outPath, "không dọn được file cũ", "FAIL");
                    return res;
                }
            }
            File.Move(tmp, outPath, overwrite: true);
            (res.Ok, res.Out, res.Warning) = (true, outPath, r.Code == 1);
            var dinfo = p.Donor.Tracks.Count > 0
                ? " ghép=" + string.Join(",", p.Donor.Tracks.Select(d => string.Create(CultureInfo.InvariantCulture, $"{d.Code}@{d.OffsetMs}ms")))
                : "";
            var detail = $"audio giữ={string.Join(",", p.KeepAudio.Select(x => x.ToString(CultureInfo.InvariantCulture)))} "
                         + $"sub giữ={string.Join(",", p.KeepSubs.Select(s => s.Id.ToString(CultureInfo.InvariantCulture)))}{dinfo}";
            if (r.Code == 1)
            {
                Job.Warn($"  → XONG (có cảnh báo): {outPath}");
                foreach (var ln in res.Output.Where(x => WarningRe().IsMatch(x)).Take(5))
                    Job.Dim($"     {ln}");
            }
            else
            {
                Job.Ok($"  → THÀNH CÔNG. Đã lưu: {outPath}");
            }
            Log.Add(Undo.Create, p.Src, outPath, detail, r.Code == 1 ? "OK-Warning" : "OK");
        }
        else
        {
            TryDelete(tmp); // >= 2: xoa file dau ra do dang
            Job.Error(string.Create(CultureInfo.InvariantCulture, $"  [!] THẤT BẠI (mã thoát {r.Code}):"));
            foreach (var ln in res.Output.TakeLast(8))
                Job.Dim($"     {ln}");
            Log.Add("Fail", p.Src, outPath, string.Join(" | ", res.Output.TakeLast(3)), "FAIL");
        }
        foreach (var dt in p.Donor.Tracks)
        {
            if (dt.Prepared is not null)
            {
                TryDelete(dt.Prepared);
                dt.Prepared = null;
            }
        }
        return res;
    }

    // ------------------------------------------------------------------ dong

    /// <summary><c>close</c>: xoá thư mục tạm.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(Temp, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
