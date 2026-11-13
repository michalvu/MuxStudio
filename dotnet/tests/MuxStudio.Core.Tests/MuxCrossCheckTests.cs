using System.Collections;
using System.Text;
using MuxStudio.Core.Tests.Support;
using static MuxStudio.Core.Tests.Support.PyData;
using JDict = System.Collections.Generic.OrderedDictionary<string, object?>;

namespace MuxStudio.Core.Tests;

/// <summary>
/// Bước ③ so với bản Python (thay <c>test_golden_mux.py</c> — bản Python đã được đối chiếu với MuxTool_v17.bat): dữ liệu do
/// <c>tools/gen_dotnet_mux.py</c> xuất (<c>Data/python_mux_cases.json</c>). Mỗi ca dựng lại CÙNG thư mục phim giả (JSON <c>mkvmerge -J</c>,
/// phụ đề ngoài thật trên đĩa, cấu hình, bản mkvmerge, thao tác sửa kế hoạch) rồi so TỪNG trường của kế hoạch, TỪNG dòng nhật ký, TỪNG
/// tham số mkvmerge / ffmpeg ("Mẹo đối chiếu" của PORT_DOTNET.md: tham số giống nhau thì file đầu ra giống nhau).
/// </summary>
[Collection(GlobalStateCollection.Name)] // bang ngon ngu
public sealed class MuxCrossCheckTests : IDisposable
{
    private const string File_ = "python_mux_cases.json";
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private static List<object?> Cases(string group) => (List<object?>)PyCases.Load(File_)[group]!;

    private static readonly Dictionary<string, string> Text = new(StringComparer.Ordinal)
    {
        ["utf-8"] = "Xin chào – tiếng Việt", ["cp1258"] = "Đây là tiên", ["cp1250"] = "Příliš žluťoučký", ["utf-8-sig"] = "Hello",
    };

    private static Encoding Enc(string name)
    {
        TextEncoding.EnsureCodePages();
        return name switch
        {
            "utf-8" => Py.Utf8NoBom,
            "utf-8-sig" => new UTF8Encoding(true),
            _ => Encoding.GetEncoding(int.Parse(name[2..], System.Globalization.CultureInfo.InvariantCulture)),
        };
    }

    private static Config CfgOf(JDict over)
    {
        var kv = new List<KeyValuePair<string, object?>> { new("UseDonorAudio", false) };
        foreach (var (k, v) in over)
        {
            kv.RemoveAll(x => x.Key == k);
            kv.Add(new(k, v));
        }
        return new Config(kv);
    }

    private static MuxSession Session(string root, Config cfg, int ver, string? ffmpeg = null) =>
        new(root, cfg, new Dictionary<string, string>(), new Job(), new FakeHttp().Client, _ => { }, new ToolPaths(Mkvmerge: "mkvmerge-gia", Ffmpeg: ffmpeg),
            mkvmergeVersion: new ToolVersion(true, ver, $"mkvmerge v{ver}.0")) { UiLang = [] };

    private static object? Rel(object? x, string root) => x switch
    {
        string s => s.Replace(root, "<ROOT>", StringComparison.Ordinal),
        JDict d => new JDict(d.Select(kv => new KeyValuePair<string, object?>(kv.Key, Rel(kv.Value, root))), StringComparer.Ordinal),
        IList l => l.Cast<object?>().Select(v => Rel(v, root)).ToList(),
        _ => x,
    };

    /// <summary>Cùng dạng với <c>plan_out</c> của <c>tools/gen_dotnet_mux.py</c>.</summary>
    private static JDict PlanOut(MuxSession s, MuxPlan plan, int linesFrom, string root)
    {
        var dn = plan.Donor;
        var tm = dn.Timing;
        var o = D(
            ("status", plan.Status), ("reasons", L([.. plan.Reasons])), ("notes", L([.. plan.Notes])), ("out_base", plan.OutBase),
            ("out_path", plan.OutPath), ("codec_tag", plan.CodecTag),
            ("audio", L([.. plan.Audio.Select(a => (object?)L(a.Id, a.Code, a.Keep, a.Reason, a.Default, a.Label, a.Describe()))])),
            ("default_audio", plan.DefaultAudio), ("lang_override", L([.. plan.LangOverride.Select(kv => (object?)L(kv.Key, kv.Value))])),
            ("ext_subs", L([.. plan.ExtSubs.Select(e => (object?)L(Path.GetFileName(e.Path), e.Code, e.Rank))])),
            ("int_subs", L([.. plan.IntSubs.Select(x => (object?)L(x.Id, x.Code, x.Keep, x.Reason, x.Default, x.Label))])),
            ("default_sub", L(plan.DefaultSub.Kind, plan.DefaultSub.Value)),
            ("sub_items", L([.. plan.SubItems.Select(i => (object?)L(i.Key, i.Kind, i.Id, i.Code, i.AutoKeep, i.Header, i.Tip, i.Order))])),
            ("needs_audio_table", plan.NeedsAudioTable), ("rule_applied", plan.RuleApplied),
            ("donor", D(("note", dn.Note), ("manual", dn.Manual), ("summary", dn.Summary), ("donor", dn.Donor is null ? null : Path.GetFileName(dn.Donor)),
                        ("timing", tm is null ? null : L(tm.Scale, tm.Offset, tm.Votes, tm.Name, tm.How, tm.Sure, tm.Close)),
                        ("tracks", L([.. dn.Tracks.Select(t => (object?)L(t.Tid, t.AIndex, t.Code, t.Name, t.OffsetMs, t.Stretch, t.Filter, t.OutChannels, t.Bitrate))])))),
            ("args", plan.Json is not null && plan.OutPath is not null ? L([.. s.BuildArgs(plan)]) : null),
            ("preview", L([.. s.PreviewCommands(plan)])),
            ("log", L([.. s.Job.Lines.Skip(linesFrom).Select(l => (object?)L(l.Level, l.Message))])));
        return (JDict)Rel(o, root)!;
    }

    private static void Compare(List<string> bad, string label, JDict py, JDict cs)
    {
        foreach (var (k, v) in py)
        {
            var got = cs.Get(k);
            if (k == "donor" && v is JDict pd && got is JDict cd)
            {
                Compare(bad, label + ".donor", pd, cd);
                continue;
            }
            if (!PyCases.Same(v, got) && bad.Count < 12)
                bad.Add($"{label}.{k}\n    Python: {PyCases.Show(v)}\n    C#:     {PyCases.Show(got)}");
        }
    }

    [Fact]
    public void Plans_same_as_python()
    {
        var bad = new List<string>();
        var cases = Cases("plans");
        for (var n = 0; n < cases.Count; n++)
        {
            var c = (JDict)cases[n]!;
            var d = _tmp[$"[P{n}] Thư mục phim"];
            Directory.CreateDirectory(d);
            var stem = (string)c["stem"]!;
            var p = Path.Combine(d, stem + ".mkv");
            File.WriteAllBytes(p, [0x1a, 0x45, 0xdf, 0xa3]);
            foreach (var e in ((List<object?>)c["exts"]!).Cast<List<object?>>())
            {
                var enc = (string)e[1]!;
                File.WriteAllBytes(Path.Combine(d, (string)e[0]!), Enc(enc).GetPreamble().Concat(Enc(enc).GetBytes(
                    $"1\r\n00:00:01,000 --> 00:00:02,000\r\n{Text.GetValueOrDefault(enc, "x")}\r\n\r\n")).ToArray());
            }
            using var s = Session(d, CfgOf((JDict)c["cfg"]!), (int)(long)c["ver"]!);
            s.JsonCache[Py.NormCase(Py.AbsPath(p))] = (JDict)c["json"]!;
            s.Db = "none";
            var plan = s.PlanFile(p);
            foreach (var a in ((List<object?>)c["actions"]!).Cast<List<object?>>())
            {
                switch ((string)a[0]!)
                {
                    case "keep_all_audio":
                        s.KeepAllAudio(plan);
                        break;
                    case "keep_all_audio_all":
                        s.KeepAllAudio(null);
                        break;
                    case "set_audio_choice":
                        s.SetAudioChoice(plan, ((List<object?>)a[1]!).Select(x => (int)(long)x!), applyToRest: false);
                        break;
                    case "set_sub_choice":
                        s.SetSubChoice(plan, ((List<object?>)a[1]!).Cast<string>(), ((List<object?>)a[2]!).Select(x => (int)(long)x!));
                        break;
                }
            }
            var k = s.Job.Lines.Count;
            plan = s.PlanFile(p);
            Compare(bad, $"plans[{n}] {stem}", (JDict)c["out"]!, PlanOut(s, plan, k, d));
        }
        Assert.True(bad.Count == 0, $"{bad.Count}+ chỗ khác Python (trên {cases.Count} kế hoạch):\n" + string.Join("\n", bad));
    }

    [Fact]
    public void Donor_plans_same_as_python()
    {
        var bad = new List<string>();
        var cases = Cases("donor");
        for (var n = 0; n < cases.Count; n++)
        {
            var c = (JDict)cases[n]!;
            var d = _tmp[$"[D{n}] Ghép audio"];
            Directory.CreateDirectory(Path.Combine(d, "audio_nguon"));
            var stem = (string)c["stem"]!;
            var p = Path.Combine(d, stem + ".mkv");
            File.WriteAllBytes(p, [0x1a, 0x45, 0xdf, 0xa3]);
            var dp = Path.Combine(d, "audio_nguon", (string)c["dstem"]! + ".mkv");
            File.WriteAllBytes(dp, []);
            using var s = Session(d, CfgOf((JDict)c["cfg"]!), 82, c["ffmpeg"] as string);
            s.JsonCache[Py.NormCase(Py.AbsPath(p))] = (JDict)c["json"]!;
            s.JsonCache[Py.NormCase(Py.AbsPath(dp))] = (JDict)c["djson"]!;
            s.DonorPool = Donor.DonorFiles(d, "audio_nguon");
            var times = (JDict)c["times"]!;
            s.TrackTimes = new TrackTimesFn((f, trk) => times.Get($"{Path.GetFileName(f)}|{trk.Id}") is List<object?> l
                ? [.. l.Select(x => Py.AsDouble(x!))] : null);
            s.Db = "none";
            if (c["override"] is { } ov)
                s.DonorOffsets[Py.NormCase(Py.AbsPath(p))] = Py.AsDouble(ov);
            var k = s.Job.Lines.Count;
            var plan = s.PlanFile(p);
            Compare(bad, $"donor[{n}] {stem}", (JDict)c["out"]!, PlanOut(s, plan, k, d));
        }
        Assert.True(bad.Count == 0, $"{bad.Count}+ chỗ khác Python (trên {cases.Count} ca ghép audio):\n" + string.Join("\n", bad));
    }

    [Fact]
    public void Stretch_find_donor_manual_offset_codec_tag_same_as_python()
    {
        PyCases.Check("stretch", x =>
        {
            var a = (List<object?>)x!;
            var sr = (int)(long)a[0]!;
            var f = Donor.StretchFilter(sr, Py.AsDouble(a[1]!));
            return L(f, Donor.StretchFactor(f, sr));
        }, File_);
        PyCases.Check("find_donor", x =>
        {
            var a = (List<object?>)x!;
            var pool = ((List<object?>)a[1]!).Select(n => Py.PathJoin(@"C:\n", (string)n!)).ToList();
            return Donor.FindDonorFile((string)a[0]!, pool) is { } hit ? Path.GetFileName(hit) : null;
        }, File_);
        var offs = D(("S01E03", 1200L), ("tap #643", -500L), ("Phim.Le.2020", "300"), ("77", 40.5), (" s02e01 ", "1,5"));
        PyCases.Check("manual_offset", x => Donor.ManualOffset((string)x!, offs), File_);
        PyCases.Check("codec_tag", x =>
        {
            var a = (List<object?>)x!;
            var j = (JDict)a[0]!;
            return Mux.CodecTag(j, a[1] as JDict, (string)a[2]!, (JDict)((List<object?>)j["tracks"]!)[1]!, ((List<object?>)a[3]!).Cast<string>().ToList());
        }, File_);
    }
}
