using System.Globalization;
using MuxStudio.Core.Tests.Support;
using static MuxStudio.Core.Tests.Support.MkvFake;
using static MuxStudio.Core.Tests.Support.PyData;
using JDict = System.Collections.Generic.OrderedDictionary<string, object?>;

namespace MuxStudio.Core.Tests;

/// <summary>
/// Đọc thông tin file phim (<c>Probe.cs</c>). Port phần <c>probe</c> của <c>tests/test_tools_probe.py</c>, các test probe của
/// <c>tests/test_mux.py</c> (tên codec, kênh, bitrate, độ phân giải, HDR) và <c>test_probe_ref_sub_tracks_ranking</c>
/// (<c>tests/test_donor.py</c>), cộng đối chiếu với kết quả Python (<c>Data/python_probe_cases.json</c>).
/// </summary>
[Collection(GlobalStateCollection.Name)] // Naming.LangMatch đọc bảng ngôn ngữ dùng chung
public class ProbeTests
{
    private static JDict MkvJson(IEnumerable<(string Codec, string Lang, string Ietf, string Name, JDict Extra)>? subs = null,
                                 long fpsDd = 41708333, long dur = 5_400_000_000_000)
    {
        var tr = new List<object?> { D(("id", 0L), ("type", "video"), ("properties", D(("default_duration", fpsDd), ("pixel_dimensions", "1920x1080")))) };
        var i = 1L;
        foreach (var (codec, lang, ietf, name, extra) in subs ?? [])
        {
            var p = D(("codec_id", codec), ("language", lang), ("language_ietf", ietf), ("track_name", name));
            foreach (var (k, v) in extra)
                p[k] = v;
            tr.Add(D(("id", i++), ("type", "subtitles"), ("properties", p)));
        }
        return D(("tracks", tr), ("container", D(("properties", D(("duration", dur))))));
    }

    private static readonly JDict None = D();

    // ---------------------------------------------------------------- test_tools_probe.py
    [Fact]
    public void Video_fps_and_duration()
    {
        var j = MkvJson();
        Assert.Equal(23.976, Probe.VideoFps(j));
        Assert.Equal(23.976, Probe.VideoFpsExact(j)!.Value, 1e-3);
        Assert.Equal(5400.0, Probe.Duration(j));
        Assert.Null(Probe.VideoFps(D(("tracks", L()))));
        Assert.Equal(0.0, Probe.Duration(null));
    }

    [Fact]
    public void Pick_ref_track_language_priority_and_text_first()
    {
        var j = MkvJson([
            ("S_HDMV/PGS", "eng", "en", "", None),
            ("S_TEXT/UTF8", "cze", "cs", "", None),
            ("S_TEXT/UTF8", "eng", "en", "English SDH", None),
            ("S_TEXT/UTF8", "eng", "en", "Forced", None),
            ("S_VOBSUB", "eng", "en", "", None),
        ]);
        var t = Probe.PickRefSubTrack(j, ["en", "vi", "cs"])!;
        Assert.Equal((3, true), (t.Id, t.Sdh)); // en SDH (10*0+5) thang cs (10*2) — van ban truoc PGS
        var t2 = Probe.PickRefSubTrack(j, ["cs", "en"])!;
        Assert.Equal(2, t2.Id); // TD-04: 'cze cs' nay khop 'cs'
        Assert.Equal("track #3 (văn bản, eng en) [SDH — không có bản thường]", t.Describe());
    }

    [Fact]
    public void Pick_ref_track_pgs_only_when_nothing_else()
    {
        var j = MkvJson([("S_HDMV/PGS", "eng", "en", "", None), ("S_TEXT/UTF8", "eng", "en", "", D(("forced_track", true)))]);
        var t = Probe.PickRefSubTrack(j, ["en"])!;
        Assert.True(t.IsPgs);
        Assert.Equal(".sup", t.Ext);
        Assert.Null(Probe.PickRefSubTrack(j, ["en"], allowPgs: false));
    }

    [Fact]
    public void Track_flags()
    {
        Assert.True(Probe.IsForced(D(("track_name", "Forced"))) && Probe.IsForced(D(("flag_forced", true))));
        Assert.True(Probe.IsSdh(D(("track_name", "English (SDH)"))) && Probe.IsSdh(D(("flag_hearing_impaired", true))));
        Assert.False(Probe.IsSdh(D(("track_name", "Sdhx"))));
        Assert.True(Probe.IsCommentary(D(("track_name", "Director's Commentary"))) && Probe.IsCommentary(D(("flag_commentary", true))));
        Assert.True(Probe.IsImageSub("S_HDMV/PGS") && !Probe.IsImageSub("S_TEXT/UTF8"));
        Assert.Equal(".vtt", Probe.SubExtByCodec("S_TEXT/WEBVTT"));
        Assert.Null(Probe.SubExtByCodec("S_VOBSUB"));
        Assert.False(Probe.IsForced(D(("forced_track", 1L)))); // "is True": 1 không phải True
    }

    [Fact]
    public void Mkv_json_reads_runs_caches_and_rejects_bad_output()
    {
        using var tmp = new TempDir();
        var movie = tmp.MovieDir();
        var path = Path.Combine(movie, "[YTS.MX] a.mkv");
        Assert.Null(Probe.MkvJson(path, null));
        var fake = Child.Exe; // "mkvmerge -J" / "ffprobe" giả (xem tests/MuxStudio.TestChild/Program.cs)
        var cache = new Dictionary<string, JDict?>();
        var j = Probe.MkvJson(path, fake, cache: cache)!;
        Assert.Equal(path, j["file_name"]); // đường dẫn có [ ] + tiếng Việt đi nguyên vẹn qua tham số và UTF-8
        Assert.Equal(23.976, Probe.VideoFps(j));
        Assert.Equal(5400.0, Probe.Duration(j));
        // bộ nhớ đệm theo đường dẫn tuyệt đối viết thường
        Assert.Same(j, Probe.MkvJson(path.ToUpperInvariant(), fake, cache: cache));
        Assert.Single(cache);
        Assert.NotNull(Probe.MkvJson(Path.Combine(movie, "canh-bao.mkv"), fake)); // mã 1 = có cảnh báo: vẫn đọc
        Assert.Null(Probe.MkvJson(Path.Combine(movie, "hong.mkv"), fake, cache: cache)); // JSON hỏng
        Assert.True(cache.ContainsKey(Py.NormCase(Py.AbsPath(Path.Combine(movie, "hong.mkv")))));
        Assert.Null(Probe.MkvJson(path, @"C:\khong-co\mkvmerge.exe"));
        Assert.Null(Probe.MkvJson(path, Child.Exe + "x"));

        var streams = new Dictionary<(string Path, string Kind), List<JDict>>();
        var a = Probe.FfprobeStreams(path, fake, cache: streams);
        var tags = Assert.Single(a).GetDict("tags");
        Assert.Equal(path, tags["title"]);
        Assert.Equal(L("-v", "quiet", "-print_format", "json", "-show_streams", "-select_streams", "a", path), tags["args"]);
        Assert.Same(a, Probe.FfprobeStreams(path.ToUpperInvariant(), fake, cache: streams));
        Assert.NotSame(a, Probe.FfprobeStreams(path, fake, "v", cache: streams)); // khoá gồm cả loại stream
        Assert.Equal("aac", Probe.AudioStreams(a)[0].Codec);
        Assert.Empty(Probe.FfprobeStreams(path, null));
        Assert.Empty(Probe.FfprobeStreams(path, @"C:\khong-co\ffprobe.exe"));
    }

    // ---------------------------------------------------------------- test_mux.py
    [Fact]
    public void Audio_codec_name_reads_track_level_codec()
    {
        // TĐ-33: mkvmerge ghi ``codec`` ở cấp track; không có thì dịch codec_id
        Assert.Equal("E-AC-3", Probe.AudioCodecName(A(1)));
        Assert.Equal("Dolby Digital", Probe.AudioCodecName(A(1, codec: "AC-3", codecId: "A_AC3")));
        Assert.Equal("DTS-HD MA", Probe.AudioCodecName(A(1, codec: "DTS-HD Master Audio", codecId: "A_DTS")));
        Assert.Equal("TrueHD", Probe.AudioCodecName(A(1, codec: "TrueHD Atmos", codecId: "A_TRUEHD")));
        var noCodec = D(("id", 1L), ("type", "audio"), ("properties", D(("codec_id", "A_EAC3"))));
        Assert.Equal("E-AC-3", Probe.AudioCodecName(noCodec));
        Assert.Equal("PCM", Probe.AudioCodecName(D(("properties", D(("codec_id", "A_PCM/INT/LIT"))))));
    }

    [Fact]
    public void Channel_name_and_bitrate_priority()
    {
        Assert.Equal(["1.0", "2.0", "2.1", "5.1", "6.1", "7.1", "4.0", ""],
                     new object?[] { 1L, 2L, 3L, 6L, 7L, 8L, 4L, null }.Select(Probe.ChannelName));
        var t = A(1, bps: 640000);
        Assert.Equal((640000.0, "tag"), Probe.AudioBitrate(t, new OrderedDictionary<int, double> { [1] = 999 }));
        Assert.Equal((384000.0, "ffprobe"), Probe.AudioBitrate(A(1), new OrderedDictionary<int, double> { [1] = 384000.0 }));
        var (bps, src) = Probe.AudioBitrate(A(1, codec: "TrueHD", ch: 8));
        Assert.Equal(4000 * 1000 * 8 / 6.0, bps, 1e-6);
        Assert.Equal("ước lượng", src);
        var br = Probe.FfprobeBitrates([D(("index", 1L), ("bit_rate", "448000")), D(("index", 2L), ("tags", D(("BPS-eng", "96000"))))]);
        Assert.Equal([new(1, 448000.0), new(2, 96000.0)], br.ToList());
    }

    [Fact]
    public void Video_resolution_hdr_names()
    {
        Assert.Equal("2160p", Probe.ResolutionName("3840x2160"));
        Assert.Equal("720p", Probe.ResolutionName("1920x800"));
        Assert.Equal("HEVC", Probe.VideoCodecName("V_MPEGH/ISO/HEVC"));
        Assert.Equal("AV1", Probe.VideoCodecName("V_AV1"));
        Assert.Equal("DV HDR", Probe.DynamicRange(J("""{"color_transfer": "smpte2084", "side_data_list": [{"side_data_type": "DOVI configuration record"}]}"""), "x"));
        Assert.Equal("HDR10+", Probe.DynamicRange(J("""{"color_transfer": "smpte2084", "side_data_list": [{"side_data_type": "HDR Dynamic Metadata SMPTE2094-40 (HDR10+)"}]}"""), "x"));
        Assert.Equal("DV HDR", Probe.DynamicRange(null, "Movie.2160p.DV.HDR10.HEVC"));
        Assert.Equal("HLG", Probe.DynamicRange(null, "Movie.2160p.HLG"));
        Assert.Equal("", Probe.DynamicRange(null, "Movie.1080p"));
        Assert.Equal("640 kbps", Probe.FormatKbps(640000));
        Assert.Equal("1,235 kbps", Probe.FormatKbps(1234567.89));
    }

    // ---------------------------------------------------------------- test_donor.py
    [Fact]
    public void Probe_ref_sub_tracks_ranking()
    {
        var j = MkvFake.J(V(), S(1, "eng", codecId: "S_HDMV/PGS"), S(2, "eng", name: "SDH"), S(3, "eng"), S(4, "eng", forced: true));
        Assert.Equal([(3, 0), (2, 1), (1, 2)], Probe.RefSubTracks(j).Select(t => (t.Id, t.Rank)));
        Assert.Equal("en", Probe.TrackLang(S(1, "eng")));
    }

    [Fact]
    public void Audio_streams_describe()
    {
        var s = Probe.AudioStreams([J("""{"codec_name": "eac3", "channels": 6, "tags": {"language": "eng", "title": "Surround"}, "disposition": {"default": 1}}"""),
                                    J("""{"codec_name": "aac"}""")]);
        Assert.Equal("[0] eng eac3 6ch - Surround (Default)", s[0].Describe());
        Assert.Equal(new AudioStream(1, "und", "aac", 0, "", false), s[1]);
        Assert.Equal(new TrackSummary(1, 0, 0, 23.976, 5400.0), Probe.SummarizeTracks(MkvJson()));
    }

    // ---------------------------------------------------------------- doi chieu voi Python
    private static JDict? Dict(object? x) => x as JDict;

    private static List<object?> Pair(object? x) => (List<object?>)x!;

    [Fact]
    public void Track_flags_and_sub_codecs_same_as_python()
    {
        PyCases.Check("track_flags", x =>
        {
            var p = Dict(x)!;
            return L(Probe.IsForced(p), Probe.IsSdh(p), Probe.IsCommentary(p), Probe.IsDescriptive(p));
        });
        PyCases.Check("sub_codec", x =>
            L(Probe.IsImageSub((string?)x), Probe.SubExtByCodec((string?)x), Probe.SubExtByCodec((string?)x, false)));
    }

    [Fact]
    public void Audio_same_as_python()
    {
        PyCases.Check("audio", x =>
        {
            var a = Pair(x);
            var ff = new OrderedDictionary<int, double>();
            foreach (var kv in ((List<object?>)a[1]!).Cast<List<object?>>())
                ff[(int)(long)kv[0]!] = Py.AsDouble(kv[1]!);
            var (bps, src) = Probe.AudioBitrate(Dict(a[0])!, ff);
            return L(Probe.AudioCodecName(Dict(a[0])!), bps, src);
        });
        PyCases.Check("channel_name", Probe.ChannelName);
        PyCases.Check("ffprobe_bitrates", x =>
            Probe.FfprobeBitrates(Pair(x).Cast<JDict>()).Select(kv => (object?)L(kv.Key, kv.Value)).ToList());
        PyCases.Check("format_kbps", x => Probe.FormatKbps(x is null ? null : Py.AsDouble(x)));
        PyCases.Check("audio_streams", x =>
            Probe.AudioStreams(Pair(x).Cast<JDict>()).Select(s => (object?)L(s.Index, s.Lang, s.Codec, s.Channels, s.Title, s.Default, s.Describe())).ToList());
    }

    [Fact]
    public void Video_same_as_python()
    {
        PyCases.Check("resolution", Probe.ResolutionName);
        PyCases.Check("video_codec", x => Probe.VideoCodecName((string?)Pair(x)[0], (string?)Pair(x)[1]));
        PyCases.Check("dynamic_range", x => Probe.DynamicRange(Dict(Pair(x)[0]), (string)Pair(x)[1]!));
        PyCases.Check("fps", x => L(Probe.VideoFps(Dict(x)), Probe.VideoFpsExact(Dict(x)), Probe.Duration(Dict(x))));
        PyCases.Check("summary", x =>
        {
            var s = Probe.SummarizeTracks(Dict(x));
            return L(s.Video, s.Audio, s.Subtitles, s.Fps, s.Duration);
        });
    }

    [Fact]
    public void Ref_sub_track_choice_same_as_python()
    {
        PyCases.Check("pick_ref", x =>
        {
            var a = Pair(x);
            var t = Probe.PickRefSubTrack(Dict(a[0]), ((List<object?>)a[1]!).Cast<string>().ToList(), (bool)a[2]!);
            return t is null ? null : L(t.Id, t.Ext, t.Sdh, t.Lang, t.Name, t.IsPgs, t.Describe());
        });
        PyCases.Check("ref_sub_tracks", x =>
            Probe.RefSubTracks(Dict(x)).Select(s => (object?)L(s.Id, s.Code, s.Ext, s.Rank)).ToList());
        PyCases.Check("track_lang", x => Probe.TrackLang(Dict(x)!));
    }

    [Fact]
    public void Numbers_are_culture_invariant()
    {
        // máy dùng locale tiếng Séc (dấu phẩy thập phân, dấu cách nhóm nghìn)
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("cs-CZ");
            Assert.Equal("1,235 kbps", Probe.FormatKbps(1234567.89));
            Assert.Equal(23.976, Probe.VideoFps(MkvJson()));
            Assert.Equal("[2] cze aac 2ch", new AudioStream(2, "cze", "aac", 2, "", false).Describe());
        }
        finally
        {
            CultureInfo.CurrentCulture = old;
        }
    }
}
