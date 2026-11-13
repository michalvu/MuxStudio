namespace MuxStudio.Core.Ui;

/// <summary>Một phần mềm / dịch vụ của bên khác mà MuxStudio dùng (port <c>Credit</c> của gui/about_view.py).</summary>
public sealed record Credit(string Name, string Kind, string Owner, string License, string Url, string Use);

/// <summary>
/// Thẻ "Giới thiệu": tác giả, phiên bản, bản quyền, ghi nhận phần mềm / dịch vụ của bên khác (port gui/about_view.py).
/// Khác bản Python CÓ CHỦ Ý: nhóm "Thư viện đi kèm" liệt kê thư viện của bản .NET (.NET, WPF, WPF-UI, CommunityToolkit.Mvvm) — bản .NET
/// không còn đi kèm Python, Tcl/Tk, CustomTkinter, Requests, keyring, Send2Trash, Pillow, PyInstaller… Công cụ ngoài và dịch vụ giữ nguyên.
/// </summary>
public static class AboutUi
{
    public const string Author = "Vũ Hoàng Anh";
    public const int CopyrightYear = 2026;
    public static readonly string Copyright = $"© {CopyrightYear} {Author}";

    /// <summary>Nhóm: thư viện nằm TRONG bản .exe | công cụ ngoài MuxStudio chỉ GỌI (không đi kèm) | dịch vụ mạng.</summary>
    public const string Bundled = "bundled";
    public const string Tool = "tool";
    public const string Service = "service";

    public static readonly IReadOnlyDictionary<string, string> KindTitles = new OrderedDictionary<string, string>(StringComparer.Ordinal)
    {
        [Bundled] = "Thư viện đi kèm trong bản MuxStudio.exe",
        [Tool] = "Công cụ ngoài (không đi kèm — bạn tự tải; MuxStudio chỉ gọi chúng)",
        [Service] = "Dịch vụ dữ liệu trên mạng",
    };

    public static readonly IReadOnlyList<Credit> Credits =
    [
        new(".NET", Bundled, ".NET Foundation và các tác giả .NET", "MIT", "https://dotnet.microsoft.com", "Ngôn ngữ và bộ chạy của chương trình"),
        new("WPF (Windows Presentation Foundation)", Bundled, ".NET Foundation và các tác giả WPF", "MIT", "https://github.com/dotnet/wpf",
            "Nền giao diện cửa sổ"),
        new("WPF-UI", Bundled, "Leszek Pomianowski và các tác giả WPF-UI", "MIT", "https://wpfui.lepo.co", "Bộ nút, khung, thẻ của giao diện"),
        new("CommunityToolkit.Mvvm", Bundled, ".NET Foundation và các tác giả Windows Community Toolkit", "MIT",
            "https://github.com/CommunityToolkit/dotnet", "Thư viện phụ của giao diện"),
        new("MKVToolNix (mkvmerge, mkvextract)", Tool, "Moritz Bunkus", "GPL-2.0", "https://mkvtoolnix.download",
            "Đọc track, trích phụ đề gốc, tạo file MKV"),
        new("FFmpeg (ffmpeg, ffprobe)", Tool, "Các tác giả FFmpeg", "LGPL-2.1-or-later / GPL-2.0-or-later (tuỳ bản dựng)", "https://ffmpeg.org",
            "Trích audio làm mốc, đọc bitrate / HDR, đổi tốc độ audio ghép"),
        new("alass", Tool, "kaegi", "GPL-3.0", "https://github.com/kaegi/alass", "Đồng bộ thời gian phụ đề"),
        new("TMDB", Service, "The Movie Database (TMDB)", "Điều khoản sử dụng API của TMDB", "https://www.themoviedb.org", "Tên phim, tên tập, IMDb ID"),
        new("TheTVDB", Service, "TheTVDB.com (TV Time, Inc.)", "Điều khoản sử dụng API của TheTVDB", "https://thetvdb.com",
            "Tên phim bộ, tên tập, số tập tuyệt đối"),
        new("OpenSubtitles.com", Service, "OpenSubtitles", "Điều khoản sử dụng API của OpenSubtitles", "https://www.opensubtitles.com", "Nguồn phụ đề"),
        new("SubSource", Service, "SubSource", "Điều khoản sử dụng API của SubSource", "https://subsource.net", "Nguồn phụ đề"),
        new("Titulky.com", Service, "Titulky.com", "Điều khoản sử dụng của Titulky.com", "https://www.titulky.com", "Nguồn phụ đề tiếng Séc"),
    ];

    /// <summary>Câu ghi nhận mà TMDB và TheTVDB yêu cầu chương trình dùng API của họ phải hiển thị (giữ nguyên tiếng Anh).</summary>
    public static readonly IReadOnlyList<string> Attributions =
    [
        "This product uses the TMDB API but is not endorsed or certified by TMDB.",
        "Metadata provided by TheTVDB. Please consider adding missing information or subscribing.",
    ];

    public static string OwnershipNote =>
        $"MuxStudio do {Author} viết. Các phần mềm, thư viện và dịch vụ liệt kê dưới đây KHÔNG thuộc MuxStudio: bản quyền của chúng thuộc về "
        + "tác giả / tổ chức tương ứng, và chúng được dùng theo giấy phép ghi ở cột \"Giấy phép\". MuxStudio không sửa đổi các công cụ ngoài, "
        + "chỉ gọi chúng như chương trình riêng.";

    public static List<Credit> ByKind(string kind) => [.. Credits.Where(c => c.Kind == kind)];

    /// <summary><c>about_lines</c>: nội dung trang Giới thiệu dạng chữ thuần (cũng dùng cho nút "Chép nội dung").</summary>
    public static List<string> AboutLines(string? version = null)
    {
        var outp = new List<string>
        {
            $"MuxStudio {version ?? AppInfo.Version}", $"Tác giả: {Author}", $"{Copyright}. Giữ mọi quyền với phần mã của MuxStudio.", "",
            OwnershipNote, "",
        };
        foreach (var kind in new[] { Bundled, Tool, Service })
        {
            outp.Add(KindTitles[kind] + ":");
            outp.AddRange(ByKind(kind).Select(c => $"  • {c.Name} — {c.Owner} — {c.License} — {c.Url}"));
            outp.Add("");
        }
        outp.AddRange(Attributions);
        return outp;
    }
}
