using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace MuxStudio.Core.Ui;

// ==========================================================================
//  TỰ KIỂM TRA BẢN ĐÓNG GÓI  (MuxStudio.exe --kiem-tra [báo cáo.txt]) — port phần "TỰ KIỂM TRA" của gui/app.py.
//  Phần của giao diện (WPF, dựng thử cửa sổ) do MuxStudio.App truyền vào qua SelfCheckUi.
// ==========================================================================

/// <summary>Một dòng của báo cáo tự kiểm tra (port <c>CheckItem</c>). <c>Status</c>: <c>ok</c> | <c>warn</c> (chạy được, nên xem) |
/// <c>fail</c> (hỏng).</summary>
public sealed record CheckItem(string Name, string Status, string Detail = "")
{
    public string Line => SelfCheck.Tag.GetValueOrDefault(Status, "[?]    ") + Name + (Detail.Length > 0 ? ": " + Detail : "");
}

/// <summary>Kết quả một hàm kiểm tra (= giá trị trả về của hàm <c>_chk_*</c>): chuỗi = đạt; <see cref="Warn"/> = chạy được, nên xem.</summary>
public readonly record struct CheckResult(string Status, string Detail)
{
    public static CheckResult Ok(string detail) => new("ok", detail);

    public static CheckResult Warn(string detail) => new("warn", detail);

    public static implicit operator CheckResult(string detail) => Ok(detail);
}

/// <summary>Các mục kiểm tra cần WPF — <c>MuxStudio.App</c> truyền vào. Test của lõi bỏ trống (= <c>with_window=False</c>, không có giao diện).</summary>
public sealed record SelfCheckUi(
    Func<CheckResult> GuiLibs,
    Func<CheckResult> Icon,
    Func<CheckResult> CaptchaImage,
    Action<List<CheckItem>> Window);

public static class SelfCheck
{
    /// <summary><c>SELF_CHECK_ARGS</c>.</summary>
    public static readonly IReadOnlyList<string> Args = ["--kiem-tra", "--self-check"];

    /// <summary><c>CHECK_TAG</c>.</summary>
    public static readonly IReadOnlyDictionary<string, string> Tag = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ok"] = "[OK]   ", ["warn"] = "[!]    ", ["fail"] = "[LỖI]  ",
    };

    /// <summary>Báo cáo mặc định: <c>%APPDATA%\MuxStudio\kiem_tra.txt</c>.</summary>
    public static string DefaultReportPath() => Path.Combine(ConfigSchema.AppConfigDir(), "kiem_tra.txt");

    /// <summary>Phần đầu của <c>main</c>: <c>MuxStudio.exe --kiem-tra [báo cáo.txt]</c> → true + đường dẫn báo cáo (<c>null</c> = mặc định).</summary>
    public static bool IsSelfCheck(IReadOnlyList<string> argv, out string? reportPath)
    {
        reportPath = null;
        if (argv.Count == 0 || !Args.Contains(argv[0], StringComparer.Ordinal))
            return false;
        reportPath = argv.Count > 1 ? argv[1] : null;
        return true;
    }

    /// <summary><c>_run_check</c>: <paramref name="fn"/> trả chi tiết (= đạt) hoặc <see cref="CheckResult.Warn"/>; ném lỗi = <c>fail</c>
    /// (<c>warn</c> nếu không quan trọng).</summary>
    public static void Run(List<CheckItem> items, string name, Func<CheckResult> fn, bool critical = true)
    {
        CheckResult r;
        try
        {
            r = fn();
        }
        catch (Exception e)
        {
            items.Add(new CheckItem(name, critical ? "fail" : "warn", $"{e.GetType().Name}: {e.Message}"));
            return;
        }
        items.Add(new CheckItem(name, r.Status ?? "ok", r.Detail ?? ""));
    }

    // ------------------------------------------------------------------ từng mục

    /// <summary><c>_chk_core</c>: bản một file phải mang đủ lõi, bảng dịch tiếng Anh (tài nguyên nhúng) và bảng mã cũ.</summary>
    public static CheckResult CheckCore()
    {
        var n = I18n.EnEntries.Count;
        if (n == 0)
            throw new InvalidOperationException("bảng dịch tiếng Anh rỗng (thiếu tài nguyên i18n_en.json)");
        TextEncoding.EnsureCodePages();
        int[] cps = [1250, 1258, 932];
        foreach (var cp in cps)
            _ = Encoding.GetEncoding(cp);
        return $"MuxStudio.Core {AppInfo.Version}, bảng dịch tiếng Anh {n} câu, bảng mã cũ (cp1250, cp1258, cp932)";
    }

    /// <summary><c>_chk_keyring</c>: đọc thử một mục KHÔNG có (không ghi gì).</summary>
    public static CheckResult CheckSecrets(ISecretBackend backend)
    {
        var name = backend.GetType().Name;
        _ = backend.GetPassword(Secrets.Service + "-kiem-tra", "kiem-tra");
        if (backend is not WinVaultBackend)
            return CheckResult.Warn($"{name} — đọc được, nhưng KHÔNG phải Windows Credential Manager");
        return $"{name} (Windows Credential Manager) — đọc thử được";
    }

    /// <summary><c>_chk_trash</c>: hàm Thùng rác của Windows có mặt (không xoá gì).</summary>
    public static CheckResult CheckTrash()
    {
        if (!NativeLibrary.TryLoad("shell32.dll", out var h))
            throw new DllNotFoundException("không nạp được shell32.dll");
        try
        {
            if (!NativeLibrary.TryGetExport(h, "SHFileOperationW", out _))
                throw new EntryPointNotFoundException("shell32.dll không có SHFileOperationW");
        }
        finally
        {
            NativeLibrary.Free(h);
        }
        return "SHFileOperationW (shell32.dll) — không xoá gì khi kiểm tra";
    }

    /// <summary><c>_chk_https</c>: .NET dùng kho chứng chỉ của Windows (không có file chứng chỉ riêng như <c>certifi</c>).</summary>
    public static CheckResult CheckHttps()
    {
        int n;
        using (var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine))
        {
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            var certs = store.Certificates;
            n = certs.Count;
            foreach (var c in certs)
                c.Dispose();
        }
        if (n == 0)
            throw new InvalidOperationException("kho chứng chỉ gốc của Windows trống");
        using (new Http())
        {
            // tao thu HttpClient (khong goi mang)
        }
        return $"HttpClient (.NET {Environment.Version.ToString(3)}), kho chứng chỉ gốc của Windows: {n} chứng chỉ (không gọi mạng khi kiểm tra)";
    }

    /// <summary><c>_chk_config_dir</c>: ghi thử rồi xoá một file tạm.</summary>
    public static CheckResult CheckConfigDir()
    {
        var d = ConfigSchema.AppConfigDir();
        Directory.CreateDirectory(d);
        var tmp = Path.Combine(d, "kiem_tra_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp");
        using (new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
        {
        }
        File.Delete(tmp);
        return $"ghi được — {d}";
    }

    /// <summary><c>_chk_subprocess</c>: chạy được tiến trình con, không nháy cửa sổ đen.</summary>
    public static CheckResult CheckSubprocess()
    {
        var r = Tools.Run(["cmd", "/c", "ver"], timeout: TimeSpan.FromSeconds(20));
        if (r.Code != 0)
            throw new InvalidOperationException($"mã thoát {r.Code}: {Head(r.Output, 120)}");
        var lines = Py.SplitLines(r.Output);
        return "ẩn cửa sổ đen (CreateNoWindow) — " + (lines.Count > 0 ? Head(lines[0], 70) : "");
    }

    /// <summary><c>_chk_shell</c>.</summary>
    public static CheckResult CheckShell()
    {
        var desk = Tools.DesktopDir();
        if (string.IsNullOrEmpty(desk) || !Directory.Exists(desk))
            return CheckResult.Warn($"không thấy Desktop ({desk})");
        return $"Desktop: {desk}";
    }

    /// <summary><c>_chk_tools</c>: công cụ ngoài theo Cài đặt hiện tại (thiếu công cụ bắt buộc = <c>warn</c>).</summary>
    public static CheckResult CheckTools(Config cfg)
    {
        var found = Tools.FindAllTools(cfg);
        var parts = new List<string>();
        var miss = new List<string>();
        foreach (var n in Tools.ToolNames)
        {
            var info = Tools.GetToolInfo(n, found.TryGetValue(n, out var p) ? p : null);
            parts.Add($"{n} {(info.Ok && info.Version.Length > 0 ? "✓ " + info.Version : info.Ok ? "✓" : "✗")}");
            if (!info.Ok && Tools.ToolRoles.TryGetValue(n, out var role) && role.Required)
                miss.Add(Tools.ExeName(n));
        }
        var text = string.Join(" · ", parts);
        if (miss.Count > 0)
            return CheckResult.Warn($"{text} — chưa có {string.Join(", ", miss)}: chọn trong Cài đặt › Chung › Công cụ ngoài");
        return text;
    }

    private static string Head(string s, int n) => s.Length <= n ? s : s[..n];

    // ------------------------------------------------------------------ cả lượt

    /// <summary>
    /// <c>self_check_items</c>: những phần hay hỏng riêng ở bản <c>.exe</c> — thiếu tài nguyên, kho mật khẩu, Thùng rác, chứng chỉ HTTPS,
    /// tiến trình con không nháy cửa sổ đen, thư mục cấu hình, dựng thử cửa sổ (<paramref name="ui"/>). Không gọi mạng, không ghi gì vào
    /// cấu hình hay Credential Manager.
    /// </summary>
    public static List<CheckItem> Items(SelfCheckUi? ui = null, ISecretBackend? backend = null, Config? cfg = null)
    {
        var items = new List<CheckItem>();
        Run(items, "Thư viện lõi (core)", CheckCore);
        if (ui is not null)
            Run(items, "Giao diện (WPF + WPF-UI)", ui.GuiLibs);
        Run(items, "Windows Credential Manager", () => CheckSecrets(backend ?? Secrets.Backend));
        Run(items, "Thùng rác", CheckTrash);
        Run(items, "Kết nối HTTPS", CheckHttps);
        Run(items, "Thư mục cấu hình", CheckConfigDir);
        Run(items, "Chạy công cụ ngoài", CheckSubprocess);
        Run(items, "Mở thư mục / Desktop", CheckShell, critical: false);
        if (ui is not null)
        {
            Run(items, "File icon", ui.Icon, critical: false);
            Run(items, "Ảnh CAPTCHA (JPEG)", ui.CaptchaImage, critical: false);
        }
        Run(items, "Công cụ ngoài (theo Cài đặt hiện tại)", () => CheckTools(cfg ?? ConfigSchema.LoadConfig()), critical: false);
        ui?.Window(items);
        return items;
    }

    /// <summary><c>self_check_report</c>.</summary>
    public static string Report(IReadOnlyList<CheckItem> items, string buildInfo, string programDir, DateTime now)
    {
        var nFail = items.Count(i => i.Status == "fail");
        var nWarn = items.Count(i => i.Status == "warn");
        var lines = new List<string>
        {
            $"{AppInfo.Title} — tự kiểm tra",
            buildInfo,
            $"Thư mục chương trình: {programDir}",
            "Lúc: " + now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            "",
        };
        lines.AddRange(items.Select(i => i.Line));
        lines.Add("");
        if (nFail > 0)
            lines.Add($"KẾT QUẢ: KHÔNG ĐẠT — {nFail} mục [LỖI]" + (nWarn > 0 ? $", {nWarn} mục [!]" : "") + ". Gửi file này khi báo lỗi.");
        else
            lines.Add("KẾT QUẢ: ĐẠT — chương trình chạy được" + (nWarn > 0 ? $" ({nWarn} mục [!] nên xem)" : "") + ".");
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>
    /// <c>self_check</c>: chạy kiểm tra, ghi báo cáo (mặc định <see cref="DefaultReportPath"/>; UTF-8, xuống dòng CRLF như bản Python trên
    /// Windows). Mã thoát 0 = đạt, 1 = có lỗi.
    /// </summary>
    public static int Execute(string? reportPath, string buildInfo, SelfCheckUi? ui = null, ISecretBackend? backend = null, Config? cfg = null)
    {
        var items = Items(ui, backend, cfg);
        var text = Report(items, buildInfo, AppInfo.ProgramDir(), DateTime.Now);
        var path = string.IsNullOrEmpty(reportPath) ? DefaultReportPath() : reportPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, text.Replace("\n", "\r\n", StringComparison.Ordinal), Py.Utf8NoBom);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.Write(text);
        }
        return items.Any(i => i.Status == "fail") ? 1 : 0;
    }
}
