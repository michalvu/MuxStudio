using System.Text;

namespace MuxStudio.Core.Tests.Support;

/// <summary>alass GIẢ (port class <c>FakeAlass</c> của <c>tests/test_subprep.py</c>): exe con chế độ <c>alass</c>, cấu hình qua
/// <c>@file.json</c> (không đụng biến môi trường), ghi lại mỗi lần gọi vào file JSONL.</summary>
public sealed class FakeAlass : IDisposable
{
    private readonly TempDir _dir = new();

    public FakeAlass()
    {
        Conf = _dir["fake_alass.json"];
        LogPath = _dir["fake_alass.jsonl"];
        Set();
    }

    public string Conf { get; }
    public string LogPath { get; }

    /// <summary>Lệnh alass (= <c>[sys.executable, fake_alass.py]</c>).</summary>
    public List<string> Cmd => Child.Cmd("alass", "@" + Conf);

    /// <summary><c>set(**kw)</c>: <c>help</c>, <c>offset</c>, <c>fail</c> (danh sách <c>D(("ref", …), ("flags", …))</c>), <c>zero_first</c>.</summary>
    public void Set(params (string Key, object? Value)[] kw)
    {
        var d = PyData.D(kw);
        d.TryAdd("log", LogPath);
        File.WriteAllText(Conf, PyJson.Dumps(d), Py.Utf8NoBom);
    }

    public List<OrderedDictionary<string, object?>> Calls() =>
        File.Exists(LogPath)
            ? [.. File.ReadAllText(LogPath, Encoding.UTF8).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(PyData.J)]
            : [];

    public static List<string> Flags(OrderedDictionary<string, object?> call) => [.. ((List<object?>)call["flags"]!).Cast<string>()];

    public void Dispose() => _dir.Dispose();
}

/// <summary>Hàm dựng dữ liệu của <c>tests/test_subprep.py</c>.</summary>
public static class SubprepData
{
    public static readonly string[] Show = ["Show.S01E01.1080p.WEB-DL", "Show.S01E02.1080p.WEB-DL", "Show.S01E03.1080p.WEB-DL"];

    public static List<SrtBlock> Blocks(IEnumerable<double> times, double dur = 1.0) =>
        [.. times.Select((t, i) => new SrtBlock(t, t + dur, "", $"dòng {i}"))];

    /// <summary><c>write_srt</c>: <paramref name="enc"/> = bảng mã (vd 1258) hoặc <c>null</c> = UTF-8.</summary>
    public static string WriteSrt(string p, IReadOnlyList<double> times, double dur = 1.0, int? enc = null, IReadOnlyList<string>? texts = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        TextEncoding.EnsureCodePages();
        var e = enc is { } cp ? Encoding.GetEncoding(cp) : Py.Utf8NoBom;
        File.WriteAllBytes(p, e.GetBytes(Synth.SrtText(times, dur, texts)));
        return p;
    }

    /// <summary><c>recap_sub</c>: phụ đề có <paramref name="nRecap"/> dòng "tóm tắt tập trước" ở đầu; phần thật trễ <paramref name="delay"/> giây.</summary>
    public static List<double> RecapSub(IReadOnlyList<double> @ref, int nRecap = 30, double delay = 65.0) =>
        [.. Enumerable.Range(0, nRecap).Select(i => 1.0 + 2 * i), .. @ref.Select(r => r + delay)];

    /// <summary><c>ass_text</c> (tests/test_subfetch.py).</summary>
    public static string AssText(IEnumerable<double> times)
    {
        var lines = new List<string> { "[Script Info]", "ScriptType: v4.00+", "", "[Events]",
                                       "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text" };
        static string F(double v, string fmt) => v.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);
        foreach (var t in times)
        {
            var h = Math.Floor(t / 3600);
            var rem = t - h * 3600;
            var m = Math.Floor(rem / 60);
            var s = rem - m * 60;
            // f"{int(h)}:{int(m):02d}:{s:05.2f}" — làm tròn như Python (Py.Fixed)
            lines.Add($"Dialogue: 0,{F(h, "0")}:{F(m, "00")}:{Py.Fixed(s, 2).PadLeft(5, '0')},{F(h, "0")}:{F(m, "00")}:{Py.Fixed(s + 1, 2).PadLeft(5, '0')},Default,,0,0,0,,x");
        }
        return string.Join("\n", lines) + "\n";
    }

    /// <summary><c>tree</c>: mọi file dưới <paramref name="root"/> (đường dẫn tương đối, dấu /), sắp xếp.</summary>
    public static List<string> Tree(string root) =>
        [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')).Order(StringComparer.Ordinal)];

    public static PrepSession Sess(string root, FakeAlass fa, IEnumerable<KeyValuePair<string, object?>>? cfg = null, ToolPaths? paths = null) =>
        new(root, new Config(cfg), new Job(), (paths ?? new ToolPaths()) with { Alass = paths?.Alass ?? fa.Cmd });
}

/// <summary><c>Lib</c>: 3 tập phim bộ (file phim rỗng) + phụ đề gốc trong srt_goc + mồi nhử ở srt_pool / audio_nguon / Output.</summary>
public sealed class SubprepLib : IDisposable
{
    private readonly TempDir _tmp = new();

    public SubprepLib()
    {
        Root = _tmp.MovieDir();
        for (var i = 0; i < SubprepData.Show.Length; i++)
        {
            var s = SubprepData.Show[i];
            Ref[s] = Synth.MakeRefTimes(400, seed: i + 1);
            File.WriteAllBytes(P($"{s}.mkv"), []);
            SubprepData.WriteSrt(P("srt_goc", $"{s}.srt"), Ref[s]);
        }
        foreach (var d in new[] { P("srt_pool", "vi"), P("audio_nguon"), P("Output", "Show (2019) [tmdbid-1]", "Season 01") })
            Directory.CreateDirectory(d);
        SubprepData.WriteSrt(P("srt_pool", "vi", $"{SubprepData.Show[0]}.vi.srt"), Ref[SubprepData.Show[1]]);
        File.WriteAllBytes(P("audio_nguon", "Show.S01E01.720p.CZ.DUB.mkv"), []);
        File.WriteAllBytes(P("audio_nguon", "Show.S01E02.720p.CZ.DUB.mkv"), []);
        File.WriteAllBytes(P("Output", "Show (2019) [tmdbid-1]", "Season 01", "Show - S01E01.mkv"), []);
    }

    public string Root { get; }
    public Dictionary<string, List<double>> Ref { get; } = new(StringComparer.Ordinal);
    public string Can => P("srt_can_sync");

    public string P(params string[] parts) => Path.Combine([Root, .. parts]);

    public string C(params string[] parts) => Path.Combine([Can, .. parts]);

    public void Dispose() => _tmp.Dispose();
}
