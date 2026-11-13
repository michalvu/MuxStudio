using MuxStudio.Core.Tests.Support;
using static MuxStudio.Core.Tests.Support.MkvFake;
using static MuxStudio.Core.Tests.Support.PyData;
using JDict = System.Collections.Generic.OrderedDictionary<string, object?>;

namespace MuxStudio.Core.Tests;

/// <summary>Ghép audio từ file phim khác (§6.9): tìm file nguồn, đo độ lệch bằng phụ đề chung, độ lệch tay, bộ lọc đổi tốc độ, lệnh
/// mkvmerge (<c>--sync</c>, thứ tự track). Port <c>tests/test_donor.py</c> (bài <c>probe_ref_sub_tracks</c> đã ở <c>ProbeTests</c>).</summary>
[Collection(GlobalStateCollection.Name)] // bang ngon ngu
public sealed class DonorTests : IDisposable
{
    private const double Pal = 25 / 23.976;
    private readonly TempDir _tmp = new();
    private readonly List<MuxLib> _libs = [];
    private string? _movieDir;

    private string MovieDir => _movieDir ??= _tmp.MovieDir();

    public void Dispose()
    {
        foreach (var l in _libs)
            l.Dispose();
        _tmp.Dispose();
    }

    [Fact]
    public void Find_donor_file_episode_and_movie()
    {
        List<string> pool = [.. new[] { 642, 643, 644 }.Select(n => $"/n/One.Piece.S16E{n}.CZ.mkv"), "/n/Dune.2021.CZ.mkv"];
        Assert.Equal("One.Piece.S16E643.CZ.mkv", Path.GetFileName(Donor.FindDonorFile("[Judas] One Piece - 0643 [1080p]", pool)));
        Assert.Null(Donor.FindDonorFile("One Piece - 0645", pool));
        Assert.Equal("Dune.2021.CZ.mkv", Path.GetFileName(Donor.FindDonorFile("Dune.2021.2160p", pool)));
        Assert.Null(Donor.FindDonorFile("Dune.1984.1080p", pool)); // khac nam
        Assert.Equal("Ban.Sec.mkv", Path.GetFileName(Donor.FindDonorFile("Phim.Gi.Do.2020", ["/n/Ban.Sec.mkv"]))); // 1 file nguon phim le
        // S02E05 khong ghep voi "- 05" (so nho, season khac 1)
        Assert.Null(Donor.FindDonorFile("Show - 05", ["/n/Show.S02E05.mkv"]));
    }

    [Fact]
    public void Manual_offset_keys()
    {
        var offs = D(("S01E03", 1200L), ("tap #643", -500L), ("Phim.Le.2020", 300L), ("77", 40L));
        Assert.Equal(1200, Donor.ManualOffset("Show.S01E03.1080p", offs));
        Assert.Equal(-500, Donor.ManualOffset("[Judas] One Piece - 0643", offs));
        Assert.Equal(300, Donor.ManualOffset("phim.le.2020", offs));
        Assert.Equal(40, Donor.ManualOffset("Anime - 077", offs));
        Assert.Null(Donor.ManualOffset("Show.S01E04", offs));
    }

    public static IEnumerable<object[]> StretchCases()
    {
        foreach (var sr in new[] { 48000, 44100 })
            foreach (var scale in new[] { Pal, 1 / Pal, 25.0 / 24, 24.0 / 25, 24 / 23.976, 25 * 1001 / 24000.0, 1.0123456789 })
                yield return [sr, scale];
    }

    [Theory]
    [MemberData(nameof(StretchCases))]
    public void Stretch_filter_math_exact(int sr, double scale)
    {
        var flt = Donor.StretchFilter(sr, scale);
        var rates = flt.Split(',').Where(p => p.StartsWith("asetrate=", StringComparison.Ordinal)).Select(p => p.Split('=')[1]).ToList();
        Assert.NotEmpty(rates);
        Assert.All(rates, r => Assert.True(r.All(char.IsAsciiDigit)));
        Assert.True(Math.Abs(Donor.StretchFactor(flt, sr) / scale - 1) <= 1e-9);
        if (flt.Contains("atempo=", StringComparison.Ordinal))
        {
            var t = flt.Split("atempo=")[1];
            Assert.DoesNotContain(",", t, StringComparison.Ordinal);
            Assert.Contains(".", t, StringComparison.Ordinal);
        }
        if (scale != 1.0123456789)
            Assert.DoesNotContain("atempo", flt, StringComparison.Ordinal); // ty le FPS that -> khong can atempo
    }

    [Fact]
    public void Stretch_filter_pal_exact_numbers()
    {
        Assert.Equal("aresample=46875,asetrate=44955,aresample=48000", Donor.StretchFilter(48000, Pal));
        Assert.Equal("aresample=47952,asetrate=50000,aresample=48000", Donor.StretchFilter(48000, 1 / Pal));
    }

    [Fact]
    public void Fraction_like_python()
    {
        // Fraction(1.1) = 2476979795053773/2251799813685248; limit_denominator(100000) cua 25/23.976 = 3125/2997
        Assert.Equal(new Fraction(2476979795053773, 2251799813685248), Fraction.FromDouble(1.1));
        Assert.Equal(new Fraction(3125, 2997), Fraction.FromDouble(25 / 23.976).LimitDenominator(100000));
        Assert.Equal(new Fraction(355, 113), Fraction.FromDouble(Math.PI).LimitDenominator(1000));
        Assert.Equal(new Fraction(-1, 3), Fraction.FromDouble(-1 / 3.0).LimitDenominator(100));
    }

    private static TrackTimes TimesFn(Dictionary<(string, int), List<double>> table) =>
        new TrackTimesFn((f, trk) => table.GetValueOrDefault((Path.GetFileName(f), trk.Id)));

    [Fact]
    public void Measure_timing_by_common_subtitle_pal()
    {
        var refT = Synth.MakeRefTimes(400, seed: 3);
        var donorT = refT.Select(r => (r + 1.5 * Pal) / Pal).ToList();
        var mineJ = J(V(), A(1), S(2, "eng"));
        var donorJ = J(V(fps: 25), A(1, "cze"), S(3, "eng"));
        var tm = Donor.MeasureTiming("/m/Mine.mkv", mineJ, "/m/Donor.mkv", donorJ,
                                     TimesFn(new() { [("Mine.mkv", 2)] = refT, [("Donor.mkv", 3)] = donorT }), extRefs: []);
        Assert.True(tm.Sure);
        Assert.Equal(Pal, tm.Scale, 9);
        Assert.True(tm.Votes >= 20);
        Assert.True(Math.Abs(tm.Offset - -1.5 * Pal) <= 0.05);
        Assert.Contains("sub English trong MKV", tm.How, StringComparison.Ordinal);
    }

    [Fact]
    public void Measure_timing_uses_external_en_srt_and_falls_back_to_duration()
    {
        var refT = Synth.MakeRefTimes(400, seed: 4);
        var ext = _tmp["Mine.en.srt"];
        File.WriteAllText(ext, Synth.SrtText(refT), Py.Utf8NoBom);
        var mineJ = JDur(3000, V(), A(1));
        var donorJ = JDur(3000.4, V(), A(1, "cze"), S(3, "eng"));
        var mine = _tmp["Mine.mkv"];
        var tm = Donor.MeasureTiming(mine, mineJ, "/d/Donor.mkv", donorJ,
                                     TimesFn(new() { [("Donor.mkv", 3)] = [.. refT.Select(r => r - 2)] }), extRefs: [("en", ext)]);
        Assert.True(tm.Sure);
        Assert.True(Math.Abs(tm.Offset - 2.0) <= 0.05);
        Assert.Contains("sub English ngoài", tm.How, StringComparison.Ordinal);
        // khong co phu de chung -> chi "khop" khi thoi luong chenh <= 1 s
        var tm2 = Donor.MeasureTiming(mine, mineJ, "/d/Donor.mkv", JDur(3000.4, V(), A(1, "cze")), TimesFn([]), extRefs: []);
        Assert.False(tm2.Sure);
        Assert.True(tm2.Close);
        Assert.StartsWith("ƯỚC TÍNH", tm2.How, StringComparison.Ordinal);
        var tm3 = Donor.MeasureTiming(mine, mineJ, "/d/Donor.mkv", JDur(2990, V(), A(1, "cze")), TimesFn([]), extRefs: []);
        Assert.False(tm3.Close);
        Assert.StartsWith("không đo được", tm3.How, StringComparison.Ordinal);
    }

    private MuxLib DonorLib((string K, object? V)[]? cfg = null, bool ffmpeg = true, MuxLib? share = null)
    {
        var lib = new MuxLib(MovieDir, [("UseDonorAudio", true), .. cfg ?? []], shareJson: share);
        lib.S.Ffmpeg = ffmpeg ? "ffmpeg" : null;
        _libs.Add(lib);
        return lib;
    }

    private static string AddDonor(MuxLib lib, string stem, JDict j)
    {
        var p = Path.Combine(lib.Root, "audio_nguon", stem + ".mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, []);
        lib.S.JsonCache[Py.NormCase(Py.AbsPath(p))] = j;
        lib.S.DonorPool = Donor.DonorFiles(lib.Root, "audio_nguon");
        return p;
    }

    [Fact]
    public void Plan_donor_same_fps_sync_and_args()
    {
        var lib = DonorLib();
        var refT = Synth.MakeRefTimes(400, seed: 5);
        var mine = lib.Add("Show.S01E03.1080p", J(V(), A(1, "eng"), S(2, "eng")));
        var dn = AddDonor(lib, "Show.S01E03.CZ", J(V(), A(1, "cze", ch: 2, bps: 192000), A(2, "cze", ch: 6, bps: 448000),
                                                  A(3, "cze", name: "Komentář režiséra Commentary"), S(4, "eng")));
        lib.S.TrackTimes = TimesFn(new() { [(Path.GetFileName(mine), 2)] = refT, [(Path.GetFileName(dn), 4)] = [.. refT.Select(r => r + 0.75)] });
        var plan = lib.Plan(mine);
        var dt = Assert.Single(plan.Donor.Tracks);
        Assert.Equal((2, "cs", -750, false, "Czech E-AC-3 5.1"), (dt.Tid, dt.Code, dt.OffsetMs, dt.Stretch, dt.Name));
        var a = lib.S.BuildArgs(plan);
        var i = a.IndexOf("--no-video");
        Assert.Equal(["--no-video", "--no-subtitles", "--no-chapters", "--no-attachments", "--no-global-tags",
                      "--audio-tracks", "2", "--language", "2:cs", "--track-name", "2:Czech E-AC-3 5.1",
                      "--default-track-flag", "2:0", "--forced-display-flag", "2:0", "--sync", "2:-750", dn], a[i..]);
        Assert.Equal("0:0,0:1,1:2,0:2", a[a.IndexOf("--track-order") + 1]);
    }

    [Fact]
    public void Donor_set_default_and_skip_if_present()
    {
        var lib = DonorLib([("DonorSetDefault", true)]);
        var refT = Synth.MakeRefTimes(400, seed: 6);
        var mine = lib.Add("Phim.A.2020", J(V(), A(1, "eng"), S(2, "eng")));
        var dn = AddDonor(lib, "Phim.A.2020.CZ", J(V(), A(1, "cze"), S(2, "eng")));
        lib.S.TrackTimes = TimesFn(new() { [(Path.GetFileName(mine), 2)] = refT, [(Path.GetFileName(dn), 2)] = refT });
        var a = lib.S.BuildArgs(lib.Plan(mine));
        Assert.Equal("0:0,1:1,0:1,0:2", a[a.IndexOf("--track-order") + 1]);
        Assert.Contains("1:0", a[a.IndexOf("--audio-tracks")..]); // audio cua ban khong con Default
        Assert.Equal("1:0", a[a.IndexOf("--default-track-flag") + 1]);
        // phim da co tieng Sec -> khong lay them
        var p2 = lib.Plan(lib.Add("Phim.B.2020", J(V(), A(1, "eng"), A(2, "cze"))));
        Assert.Empty(p2.Donor.Tracks);
        Assert.Contains("đã có Czech", p2.Donor.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Donor_pal_needs_stretch_and_ffmpeg()
    {
        var lib = DonorLib();
        var refT = Synth.MakeRefTimes(400, seed: 7);
        var mine = lib.Add("Phim.C.2020", J(V(), A(1, "eng"), S(2, "eng")));
        var dn = AddDonor(lib, "Phim.C.2020.CZ", J(V(fps: 25), A(1, "cze", ch: 8), S(2, "eng")));
        lib.S.TrackTimes = TimesFn(new() { [(Path.GetFileName(mine), 2)] = refT, [(Path.GetFileName(dn), 2)] = [.. refT.Select(r => r / Pal)] });
        var plan = lib.Plan(mine);
        var dt = Assert.Single(plan.Donor.Tracks);
        Assert.True(dt.Stretch);
        Assert.Equal((6, "640k", "Czech E-AC-3 5.1"), (dt.OutChannels, dt.Bitrate, dt.Name));
        Assert.Equal("aresample=46875,asetrate=44955,aresample=48000", dt.Filter);
        var cmds = lib.S.PreviewCommands(plan);
        Assert.StartsWith($"ffmpeg -y -v error -i \"{dn}\" -map 0:a:0", cmds[0], StringComparison.Ordinal);
        Assert.Contains("-c:a eac3 -b:a 640k", cmds[0], StringComparison.Ordinal);
        Assert.Contains("a_0.mka", cmds[1], StringComparison.Ordinal);
        Assert.Contains("--audio-tracks 0 ", cmds[1], StringComparison.Ordinal); // mkvmerge doc .mka tam, track 0
        var lib2 = DonorLib(ffmpeg: false, share: lib);
        lib2.S.TrackTimes = lib.S.TrackTimes;
        lib2.S.DonorPool = lib.S.DonorPool;
        Assert.Empty(lib2.Plan(mine).Donor.Tracks);
    }

    [Fact]
    public void Donor_not_measured_is_skipped_unless_manual()
    {
        var lib = DonorLib();
        var mine = lib.Add("Phim.D.2020", JDur(6000, V(), A(1, "eng")));
        AddDonor(lib, "Phim.D.2020.CZ", JDur(6100, V(), A(1, "cze")));
        lib.S.TrackTimes = TimesFn([]);
        var plan = lib.Plan(mine);
        Assert.Empty(plan.Donor.Tracks);
        Assert.Contains("KHÔNG ghép", plan.Donor.Note, StringComparison.Ordinal);
        lib.S.SetDonorOffset(plan, 1500); // sua tay trong bang
        plan = lib.Plan(mine);
        Assert.Equal(1500, plan.Donor.Tracks[0].OffsetMs);
        Assert.Equal("chỉnh tay (trong bảng)", plan.Donor.Timing!.How);
    }

    [Fact]
    public void Config_default_donor_settings()
    {
        var c = new Config();
        Assert.Equal(["cs"], c.List("DonorLangs"));
        Assert.Equal(20, c.Int("DonorMinVotes"));
        Assert.Equal(1.0, c.Double("DonorMaxDurationDiff"));
    }
}
