// Tiến trình con cho test C# — thay "python -c ..." và tests/fake_alass.py của bộ test Python.
//
//   MuxStudio.TestChild print <chữ…>     in chữ (UTF-8, như mkvmerge / ffprobe) + "\n"
//   MuxStudio.TestChild args <…>         in từng tham số nhận được, mỗi tham số một dòng (UTF-8)
//   MuxStudio.TestChild hex <hex>        ghi nguyên các byte ra stdout
//   MuxStudio.TestChild stderr <chữ…>    in chữ ra stderr
//   MuxStudio.TestChild exit <mã>        thoát với mã cho trước
//   MuxStudio.TestChild sleep <giây>     ngủ
//   MuxStudio.TestChild tree <giây> <file>  mở một tiến trình cháu (sleep), ghi PID cháu vào <file>, rồi ngủ
//   MuxStudio.TestChild alass [tham số…]   alass GIẢ: "--help" in trợ giúp như tests/fake_alass.py (DEFAULT_HELP)
//   MuxStudio.TestChild -J <file>        mkvmerge -J GIẢ: JSON 1 track video 23.976 fps, 5400 s, "file_name" = <file>;
//                                        tên có "canh-bao" → mã 1, có "hong" → in rác
//   MuxStudio.TestChild -v quiet … <file>  ffprobe GIẢ: {"streams": [{"index": 1, "codec_name": "aac",
//                                        "tags": {"title": <file>, "args": [tham số nhận được]}}]}
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

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
        if (rest.Contains("--help"))
        {
            Write(stdout, FakeAlass.DefaultHelp + "\n");
            return 0;
        }
        Write(stderr, "alass giả: chưa hỗ trợ đồng bộ (GĐ3)\n");
        return 1;
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

internal static class FakeAlass
{
    /// <summary>= <c>DEFAULT_HELP</c> của tests/fake_alass.py.</summary>
    public const string DefaultHelp = """
        alass 2.0.0 (giả)
        USAGE: alass [OPTIONS] <REFERENCE_FILE> <INCORRECT_SUB_FILE> <OUTPUT_FILE>
            --no-split               Do not try to split
            --disable-fps-guessing   Do not guess framerate
            --split-penalty <n>      Penalty

        """;
}
