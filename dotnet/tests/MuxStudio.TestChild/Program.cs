// Tiến trình con cho test C# — thay "python -c ..." và tests/fake_alass.py của bộ test Python.
//
//   MuxStudio.TestChild print <chữ…>     in chữ (UTF-8, như mkvmerge / ffprobe) + "\n"
//   MuxStudio.TestChild args <…>         in từng tham số nhận được, mỗi tham số một dòng (UTF-8)
//   MuxStudio.TestChild hex <hex>        ghi nguyên các byte ra stdout
//   MuxStudio.TestChild stderr <chữ…>    in chữ ra stderr
//   MuxStudio.TestChild exit <mã>        thoát với mã cho trước
//   MuxStudio.TestChild sleep <giây>     ngủ
//   MuxStudio.TestChild tree <giây> <file>  mở một tiến trình cháu (sleep), ghi PID cháu vào <file>, rồi ngủ
//   MuxStudio.TestChild alass [@cấu_hình.json] [tham số…] REF INC OUT
//                                        alass GIẢ = bản port của tests/fake_alass.py (xem class FakeAlass bên dưới)
//   MuxStudio.TestChild -J <file>        mkvmerge -J GIẢ: JSON 1 track video 23.976 fps, 5400 s, "file_name" = <file>;
//                                        tên có "canh-bao" → mã 1, có "hong" → in rác
//   MuxStudio.TestChild -v quiet … <file>  ffprobe GIẢ: {"streams": [{"index": 1, "codec_name": "aac",
//                                        "tags": {"title": <file>, "args": [tham số nhận được]}}]}
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var utf8 = new UTF8Encoding(false);
var stdout = Console.OpenStandardOutput();
var stderr = Console.OpenStandardError();

void Write(Stream s, string text)
{
    var b = utf8.GetBytes(text);
    s.Write(b, 0, b.Length);
    s.Flush();
}

if (args.Length == 0)
{
    Write(stderr, "thiếu lệnh\n");
    return 2;
}

var rest = args[1..];
switch (args[0])
{
    case "print":
        Write(stdout, string.Join(' ', rest) + "\n");
        return 0;
    case "args":
        foreach (var a in rest)
            Write(stdout, a + "\n");
        return 0;
    case "hex":
        var bytes = Convert.FromHexString(rest[0]);
        stdout.Write(bytes, 0, bytes.Length);
        stdout.Flush();
        return 0;
    case "stderr":
        Write(stderr, string.Join(' ', rest) + "\n");
        return 0;
    case "exit":
        return int.Parse(rest[0], CultureInfo.InvariantCulture);
    case "sleep":
        Thread.Sleep(TimeSpan.FromSeconds(double.Parse(rest[0], CultureInfo.InvariantCulture)));
        return 0;
    case "tree":
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("sleep");
        psi.ArgumentList.Add(rest[0]);
        using (var child = Process.Start(psi)!)
            File.WriteAllText(rest[1], child.Id.ToString(CultureInfo.InvariantCulture));
        Thread.Sleep(TimeSpan.FromSeconds(double.Parse(rest[0], CultureInfo.InvariantCulture)));
        return 0;
    case "alass":
        return FakeAlass.Run(rest, s => Write(stdout, s), s => Write(stderr, s));
    case "-J":
        if (rest[0].Contains("hong", StringComparison.Ordinal))
        {
            Write(stdout, "{không phải JSON");
            return 0;
        }
        Write(stdout, "{\"container\": {\"properties\": {\"duration\": 5400000000000}}, \"file_name\": " + JsonSerializer.Serialize(rest[0])
                      + ", \"tracks\": [{\"id\": 0, \"type\": \"video\", \"properties\": {\"default_duration\": 41708333}}]}\n");
        return rest[0].Contains("canh-bao", StringComparison.Ordinal) ? 1 : 0;
    case "-v":
        Write(stdout, "{\"streams\": [{\"index\": 1, \"codec_name\": \"aac\", \"tags\": {\"title\": " + JsonSerializer.Serialize(args[^1])
                      + ", \"args\": " + JsonSerializer.Serialize(args) + "}}]}\n");
        return 0;
    default:
        Write(stderr, $"lệnh lạ: {args[0]}\n");
        return 2;
}

/// <summary>
/// alass GIẢ cho test bước ② (port <c>tests/fake_alass.py</c>). Gọi giống alass thật: <c>alass [tham số...] REF INC OUT</c>.
/// Cấu hình: tham số đầu <c>@file.json</c> (hoặc biến môi trường <c>FAKE_ALASS</c> = đường dẫn file JSON như bản Python):
/// <list type="bullet">
/// <item><c>log</c>: file JSONL — mỗi lần gọi ghi <c>{flags, ref, inc, out, wav}</c> (<c>wav</c> = kênh/tần số/bit nếu REF là WAV)</item>
/// <item><c>help</c>: chữ in ra cho <c>--help</c></item>
/// <item><c>offset</c>: số giây cộng vào mọi mốc (mặc định 0)</item>
/// <item><c>fail</c>: danh sách luật <c>{"ref": "wav"|"video"|"sub"|"any", "flags": true|false}</c>; khớp luật nào → mã 1</item>
/// <item><c>zero_first</c>: dồn N dòng đầu về 00:00:00 (giả lập lỗi alass)</item>
/// <item><c>sleep</c>: (chỉ bản C#) ngủ N giây trước khi ghi kết quả — thử "Dừng ngay"</item>
/// </list>
/// </summary>
internal static partial class FakeAlass
{
    public static int Run(string[] argv, Action<string> stdout, Action<string> stderr)
    {
        JsonElement conf = default;
        var confPath = Environment.GetEnvironmentVariable("FAKE_ALASS");
        if (argv.Length > 0 && argv[0].StartsWith('@'))
        {
            confPath = argv[0][1..];
            argv = argv[1..];
        }
        if (!string.IsNullOrEmpty(confPath))
            conf = JsonDocument.Parse(File.ReadAllText(confPath, Encoding.UTF8)).RootElement;
        string? Str(string k) => conf.ValueKind == JsonValueKind.Object && conf.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
        double Num(string k) => conf.ValueKind == JsonValueKind.Object && conf.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : 0.0;

        if (argv.Contains("--help"))
        {
            stdout((Str("help") ?? DefaultHelp) + "\n");
            return 0;
        }
        var flags = argv[..^3];
        var (refPath, inc, outPath) = (argv[^3], argv[^2], argv[^1]);
        object? wav = null;
        if (refPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            wav = WavInfo(refPath);
        if (Str("log") is { } log)
        {
            var rec = new Dictionary<string, object?> { ["flags"] = flags, ["ref"] = refPath, ["inc"] = inc, ["out"] = outPath, ["wav"] = wav };
            File.AppendAllText(log, JsonSerializer.Serialize(rec) + "\n", new UTF8Encoding(false));
        }
        var low = refPath.ToLowerInvariant();
        var kind = low.EndsWith(".wav", StringComparison.Ordinal) ? "wav"
            : low.EndsWith(".mkv", StringComparison.Ordinal) || low.EndsWith(".mp4", StringComparison.Ordinal) ? "video" : "sub";
        if (conf.ValueKind == JsonValueKind.Object && conf.TryGetProperty("fail", out var fail))
        {
            foreach (var rule in fail.EnumerateArray())
            {
                var r = rule.TryGetProperty("ref", out var rv) ? rv.GetString() : "any";
                var f = rule.TryGetProperty("flags", out var fv) ? fv.GetBoolean() : flags.Length > 0;
                if ((r == "any" || r == kind) && f == flags.Length > 0)
                {
                    stderr("error: missing field `codec_long_name`\n");
                    return 1;
                }
            }
        }
        if (Num("sleep") > 0) // (chỉ bản C#) giả lập alass chạy lâu để thử "Dừng ngay"
            Thread.Sleep(TimeSpan.FromSeconds(Num("sleep")));
        var off = Num("offset");
        var zero = (int)Num("zero_first");
        var txt = new UTF8Encoding(false, false).GetString(File.ReadAllBytes(inc));
        var il = inc.ToLowerInvariant();
        if (il.EndsWith(".ass", StringComparison.Ordinal) || il.EndsWith(".ssa", StringComparison.Ordinal))
        {
            txt = AssRe().Replace(txt, m =>
            {
                static double T(Match m, int i) => int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) * 3600
                    + int.Parse(m.Groups[i + 1].Value, CultureInfo.InvariantCulture) * 60
                    + int.Parse(m.Groups[i + 2].Value, CultureInfo.InvariantCulture)
                    + int.Parse(m.Groups[i + 3].Value, CultureInfo.InvariantCulture) / 100.0;
                return $"{m.Groups[1].Value}{AssStamp(T(m, 2) + off)},{AssStamp(T(m, 6) + off)},";
            });
        }
        else
        {
            var n = 0;
            txt = SrtRe().Replace(txt, m =>
            {
                static double T(Match m, int i) => int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) * 3600
                    + int.Parse(m.Groups[i + 1].Value, CultureInfo.InvariantCulture) * 60
                    + int.Parse(m.Groups[i + 2].Value, CultureInfo.InvariantCulture)
                    + int.Parse(m.Groups[i + 3].Value, CultureInfo.InvariantCulture) / 1000.0;
                n++;
                var (a, b) = (T(m, 1) + off, T(m, 5) + off);
                if (n <= zero)
                    (a, b) = (0.0, 0.01);
                return $"{Stamp(a)} --> {Stamp(b)}";
            });
        }
        File.WriteAllBytes(outPath, new UTF8Encoding(false).GetBytes(txt));
        return 0;
    }

    private static Dictionary<string, int> WavInfo(string path)
    {
        var b = File.ReadAllBytes(path);
        var pos = 12; // "RIFF" size "WAVE"
        while (pos + 8 <= b.Length)
        {
            var id = Encoding.ASCII.GetString(b, pos, 4);
            var size = BitConverter.ToInt32(b, pos + 4);
            if (id == "fmt ")
            {
                return new Dictionary<string, int>
                {
                    ["channels"] = BitConverter.ToInt16(b, pos + 10),
                    ["rate"] = BitConverter.ToInt32(b, pos + 12),
                    ["bits"] = BitConverter.ToInt16(b, pos + 22),
                };
            }
            pos += 8 + size + (size & 1);
        }
        throw new InvalidDataException("không có khối fmt");
    }

    private static string Stamp(double sec)
    {
        var ms = (long)Math.Round(Math.Max(0.0, sec) * 1000);
        var h = ms / 3_600_000;
        ms %= 3_600_000;
        var m = ms / 60_000;
        ms %= 60_000;
        var s = ms / 1000;
        ms %= 1000;
        return string.Create(CultureInfo.InvariantCulture, $"{h:00}:{m:00}:{s:00},{ms:000}");
    }

    private static string AssStamp(double sec)
    {
        var cs = (long)Math.Round(Math.Max(0.0, sec) * 100);
        var h = cs / 360_000;
        cs %= 360_000;
        var m = cs / 6000;
        cs %= 6000;
        var s = cs / 100;
        cs %= 100;
        return string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}.{cs:00}");
    }

    [GeneratedRegex(@"^(Dialogue:\s*[^,]*,)(\d+):(\d{2}):(\d{2})\.(\d{2}),(\d+):(\d{2}):(\d{2})\.(\d{2}),", RegexOptions.Multiline)]
    private static partial Regex AssRe();

    [GeneratedRegex(@"(\d{2}):(\d{2}):(\d{2}),(\d{3}) --> (\d{2}):(\d{2}):(\d{2}),(\d{3})")]
    private static partial Regex SrtRe();

    /// <summary>= <c>DEFAULT_HELP</c> của tests/fake_alass.py.</summary>
    public const string DefaultHelp = """
        alass 2.0.0 (giả)
        USAGE: alass [OPTIONS] <REFERENCE_FILE> <INCORRECT_SUB_FILE> <OUTPUT_FILE>
            --no-split               Do not try to split
            --disable-fps-guessing   Do not guess framerate
            --split-penalty <n>      Penalty

        """;
}
