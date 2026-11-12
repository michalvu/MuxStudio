namespace MuxStudio.Core.Tests.Support;

/// <summary>
/// Tiến trình con cho test (project <c>tests/MuxStudio.TestChild</c>, được chép cạnh DLL test) — thay
/// <c>[sys.executable, "-c", "..."]</c> và <c>tests/fake_alass.py</c> của bộ test Python.
/// </summary>
public static class Child
{
    public static string Exe { get; } = Path.Combine(AppContext.BaseDirectory, "MuxStudio.TestChild.exe");

    /// <summary>Lệnh đầy đủ: <c>[exe, mode, args...]</c> (xem chú thích đầu <c>tests/MuxStudio.TestChild/Program.cs</c>).</summary>
    public static List<string> Cmd(string mode, params string[] args) => [Exe, mode, .. args];
}

/// <summary>Dựng giá trị JSON kiểu Python (như <see cref="PyJson.Loads"/> trả về) cho dữ liệu giả.</summary>
public static class PyData
{
    /// <summary><c>{k: v, ...}</c> giữ thứ tự khoá. Số nguyên nên truyền <c>long</c> (như <c>PyJson.Loads</c>).</summary>
    public static OrderedDictionary<string, object?> D(params (string Key, object? Value)[] kv)
    {
        var d = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in kv)
            d[k] = v;
        return d;
    }

    /// <summary><c>[a, b, ...]</c>.</summary>
    public static List<object?> L(params object?[] items) => [.. items];

    /// <summary>Đọc một chuỗi JSON thành bảng.</summary>
    public static OrderedDictionary<string, object?> J(string json) => (OrderedDictionary<string, object?>)PyJson.Loads(json)!;
}

/// <summary>JSON <c>mkvmerge -J</c> giả lập — port <c>V A S J</c> của <c>tests/test_mux.py</c> (dùng lại ở bước ③).</summary>
public static class MkvFake
{
    public static OrderedDictionary<string, object?> V(long tid = 0, string dim = "1920x1080", string codecId = "V_MPEGH/ISO/HEVC",
                                                       string codec = "HEVC/H.265/MPEG-H", string lang = "und", double fps = 23.976) =>
        PyData.D(("id", tid), ("type", "video"), ("codec", codec),
                 ("properties", PyData.D(("codec_id", codecId), ("pixel_dimensions", dim), ("language", lang),
                                         ("default_duration", (long)Math.Round(1e9 / fps)))));

    public static OrderedDictionary<string, object?> A(long tid, string lang = "eng", string codec = "E-AC-3", string codecId = "A_EAC3",
                                                       long ch = 6, string name = "", long? bps = null, string? ietf = null,
                                                       params (string Key, object? Value)[] kw)
    {
        var p = PyData.D(("codec_id", codecId), ("language", lang), ("audio_channels", ch), ("audio_sampling_frequency", 48000L));
        if (!string.IsNullOrEmpty(ietf))
            p["language_ietf"] = ietf;
        if (name.Length > 0)
            p["track_name"] = name;
        if (bps is { } b && b != 0)
            p["tag_bps"] = b.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var (k, v) in kw)
            p[k] = v;
        return PyData.D(("id", tid), ("type", "audio"), ("codec", codec), ("properties", p));
    }

    public static OrderedDictionary<string, object?> S(long tid, string lang = "eng", string codecId = "S_TEXT/UTF8", string name = "",
                                                       bool forced = false, bool sdh = false, params (string Key, object? Value)[] kw)
    {
        var p = PyData.D(("codec_id", codecId), ("language", lang), ("forced_track", forced));
        if (name.Length > 0)
            p["track_name"] = name;
        if (sdh)
            p["flag_hearing_impaired"] = true;
        foreach (var (k, v) in kw)
            p[k] = v;
        return PyData.D(("id", tid), ("type", "subtitles"), ("codec", "SubRip/SRT"), ("properties", p));
    }

    public static OrderedDictionary<string, object?> J(params OrderedDictionary<string, object?>[] tracks) => JDur(5400.0, tracks);

    public static OrderedDictionary<string, object?> JDur(double dur, params OrderedDictionary<string, object?>[] tracks) =>
        PyData.D(("container", PyData.D(("properties", PyData.D(("duration", (long)(dur * 1e9)))))),
                 ("tracks", tracks.Cast<object?>().ToList()));
}
