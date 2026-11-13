using System.Text;
using JDict = System.Collections.Generic.OrderedDictionary<string, object?>;

namespace MuxStudio.Core.Tests.Support;

/// <summary>Thư mục phim giả cho bước ③ — port class <c>Lib</c> của <c>tests/test_mux.py</c>: JSON <c>mkvmerge -J</c> nạp sẵn vào bộ nhớ
/// đệm (không cần mkvmerge thật), bản mkvmerge giả (<c>mkvmerge_version</c> bị thay), <c>_ui_lang = []</c>.</summary>
public sealed class MuxLib : IDisposable
{
    public MuxLib(string root, (string K, object? V)[]? cfg = null, Dictionary<string, string>? secrets = null, FakeHttp? http = null, int ver = 82,
                  MuxLib? shareJson = null)
    {
        Root = root;
        var kv = new List<(string K, object? V)> { ("UseDonorAudio", false) };
        foreach (var x in cfg ?? [])
        {
            kv.RemoveAll(y => y.K == x.K);
            kv.Add(x);
        }
        Cfg = NamingDbTests.Cfg([.. kv]);
        Http = http ?? new FakeHttp();
        S = new MuxSession(root, Cfg, secrets ?? [], new Job(), Http.Client, new Sleeper().Sleep,
                           new ToolPaths(Mkvmerge: "mkvmerge-gia"), mkvmergeVersion: new ToolVersion(true, ver, $"mkvmerge v{ver}.0"))
        {
            UiLang = [],
        };
        if (shareJson is not null)
            S.JsonCache = shareJson.S.JsonCache;
    }

    public string Root { get; }
    public Config Cfg { get; }
    public FakeHttp Http { get; }
    public MuxSession S { get; }

    public string Add(string stem, JDict j, string rel = "")
    {
        var d = rel.Length > 0 ? Path.Combine(Root, rel) : Root;
        Directory.CreateDirectory(d);
        var p = Path.Combine(d, stem + ".mkv");
        File.WriteAllBytes(p, [0x1a, 0x45, 0xdf, 0xa3]);
        S.JsonCache[Py.NormCase(Py.AbsPath(p))] = j;
        return p;
    }

    public MuxPlan Plan(string p, string db = "none")
    {
        S.Db = db;
        return S.PlanFile(p);
    }

    public static List<int> AudioKept(MuxPlan plan) => [.. plan.Audio.Where(a => a.Keep).Select(a => a.Id)];

    public static string WriteSub(string p, int? codepage = null, string text = "Xin chào")
    {
        TextEncoding.EnsureCodePages();
        var enc = codepage is { } cp ? Encoding.GetEncoding(cp) : Py.Utf8NoBom;
        File.WriteAllBytes(p, enc.GetBytes($"1\r\n00:00:01,000 --> 00:00:02,000\r\n{text}\r\n\r\n"));
        return p;
    }

    /// <summary><c>fake_run</c>: thay <c>tools.run</c> — ghi file đầu ra <c>-o</c> rồi trả mã thoát lần lượt theo <paramref name="codes"/>.</summary>
    public static ToolRunner FakeRun(IEnumerable<long> codes, List<string> made)
    {
        var seq = new Queue<long>(codes);
        return (args, _, _) =>
        {
            var a = args.ToList();
            var i = a.IndexOf("-o");
            if (i >= 0)
            {
                var outp = a[i + 1];
                File.WriteAllBytes(outp, Enumerable.Repeat((byte)'x', 100).ToArray());
                made.Add(outp);
                var code = seq.Dequeue();
                return new RunResult(code, code == 1 ? "Warning: something" : "", code >= 2 ? "Error: boom" : "");
            }
            return new RunResult(0, "", "");
        };
    }

    public void Dispose() => S.Dispose();
}
