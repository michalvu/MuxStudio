using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MuxStudio.Core;
using MuxStudio.Core.Ui;
using Wpf.Ui.Controls;

namespace MuxStudio.App;

/// <summary>
/// Phần WPF của <c>MuxStudio.exe --kiem-tra [báo cáo.txt]</c> (port <c>_chk_gui_libs</c>, <c>_chk_icon</c>, <c>_chk_captcha_image</c>,
/// <c>_chk_window</c> của gui/app.py). Phần còn lại + báo cáo: <see cref="SelfCheck"/> (lõi). Không mở cửa sổ nào trên màn hình.
/// </summary>
internal static class SelfCheckWpf
{
    /// <summary>Chữ có dấu tiếng Việt (thường + hoa) — phông giao diện phải có đủ.</summary>
    private const string VietChars = "ăâđêôơưàảãáạằẳẵắặầẩẫấậèẻẽéẹềểễếệìỉĩíịòỏõóọồổỗốộờởỡớợùủũúụừửữứựỳỷỹýỵĂÂĐÊÔƠƯẠẶẬỆỘỢỰ";

    /// <summary>Lỗi giao diện chưa bắt trong lúc tự kiểm tra (<c>App.OnUiError</c> gom lại thay vì hiện hộp thoại).</summary>
    public static List<Exception> UiErrors { get; } = [];

    /// <summary>Chạy cả lượt, ghi báo cáo. Mã thoát 0 = đạt, 1 = có lỗi.</summary>
    public static int Run(string? reportPath)
    {
        I18n.SetLanguage("vi"); // bao cao (va dong [LOI] cua khung log) giu tieng Viet nhu ban Python
        ThemeService.Init();
        var ui = new SelfCheckUi(GuiLibs, Icon, CaptchaImage, Window);
        return SelfCheck.Execute(reportPath, App.BuildInfo(), ui);
    }

    /// <summary><c>_chk_gui_libs</c>: thư viện giao diện + từ điển kiểu (Fluent của WPF-UI, bảng màu Sáng / Tối, kiểu bảng của MuxStudio).</summary>
    private static CheckResult GuiLibs()
    {
        var res = Application.Current.Resources;
        string[] keys = ["ApplicationBackgroundBrush", "DefaultDataGridRowStyle", "WbGrid", "WbCell", "MonoFont", "LogErrorBrush"];
        var miss = keys.Where(k => res[k] is null).ToList();
        if (miss.Count > 0)
            throw new InvalidOperationException("thiếu tài nguyên giao diện: " + string.Join(", ", miss));
        foreach (var name in new[] { "Light", "Dark" })
        {
            var d = new ResourceDictionary { Source = new Uri($"pack://application:,,,/MuxStudio;component/Themes/{name}.xaml", UriKind.Absolute) };
            if (d["LogErrorBrush"] is null)
                throw new InvalidOperationException($"bảng màu {name}.xaml thiếu màu log");
        }
        return $"WPF {Ver(typeof(Window))}, WPF-UI {Ver(typeof(FluentWindow))} (đủ kiểu Fluent + bảng màu Sáng / Tối)";
    }

    /// <summary>Số bản thật của thư viện (<c>10.0.10</c>; số phiên bản assembly của WPF luôn là <c>10.0.0</c>).</summary>
    private static string Ver(Type t)
    {
        var info = t.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var num = string.Concat((info ?? "").TakeWhile(c => char.IsAsciiDigit(c) || c == '.')).TrimEnd('.'); // bo "-servicing.…+commit"
        if (num.Length > 0)
            return num;
        return t.Assembly.GetName().Version?.ToString(3) ?? "?";
    }

    /// <summary><c>_chk_icon</c>: icon nhúng trong chương trình đọc được, đủ các cỡ.</summary>
    private static CheckResult Icon()
    {
        var sizes = AppIcon.Frames(AppIcon.IcoUri).Select(f => f.PixelWidth).Distinct().Order().ToList();
        var png = AppIcon.Frames(AppIcon.PngUri)[0].PixelWidth;
        if (sizes.Count == 0)
            return CheckResult.Warn("muxstudio.ico không có ảnh nào");
        return $"muxstudio.ico ({sizes.Count} cỡ {sizes[0]}–{sizes[^1]} px), muxstudio.png ({png} px)";
    }

    /// <summary><c>_chk_captcha_image</c>: ảnh CAPTCHA của Titulky.com là JPEG — tự tạo một ảnh JPEG nhỏ rồi đọc lại bằng đúng hàm của
    /// hộp thoại CAPTCHA.</summary>
    private static CheckResult CaptchaImage()
    {
        var px = new byte[40 * 10 * 3];
        for (var i = 0; i < px.Length; i += 3)
            px[i + 2] = 200; // Bgr24: do
        var enc = new JpegBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(40, 10, 96, 96, PixelFormats.Bgr24, null, px, 40 * 3)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        var bmp = Dlg.DecodeImage(ms.ToArray());
        if (bmp is null || bmp.PixelWidth != 40 || bmp.PixelHeight != 10)
            return CheckResult.Warn("không đọc được ảnh JPEG — ảnh CAPTCHA của Titulky sẽ mở bằng trình xem ảnh của máy");
        return "WPF đọc được JPEG (bộ giải mã của Windows)";
    }

    /// <summary>
    /// <c>_chk_window</c>: dựng thử cửa sổ chính NGOÀI màn hình (không lấy focus, không hiện trên thanh tác vụ), cấu hình tạm — không đụng
    /// cấu hình của bạn — đủ 5 thẻ, phông có đủ chữ tiếng Việt, icon; rồi đóng.
    /// </summary>
    private static void Window(List<CheckItem> items)
    {
        const string env = "MUXSTUDIO_CONFIG_DIR";
        var old = Environment.GetEnvironmentVariable(env);
        var tmp = Directory.CreateTempSubdirectory("muxstudio_kiem_tra_").FullName;
        Environment.SetEnvironmentVariable(env, tmp);
        MainWindow? w = null;
        try
        {
            var sw = Stopwatch.StartNew();
            w = new MainWindow(new Config(), appearance: "light") { SelfTest = true };
            var rendered = false;
            w.ContentRendered += (_, _) => rendered = true;
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = -32000;
            w.Top = -32000;
            w.ShowActivated = false;
            w.ShowInTaskbar = false;
            w.Show();
            Pump(() => rendered && !w.Runner.Busy, TimeSpan.FromSeconds(30));
            var errs = w.LogView.Lines.Where(l => l.Level == "error" && l.Text.StartsWith("[LỖI", StringComparison.Ordinal))
                        .Select(l => l.Text)
                        .Concat(UiErrors.Select(x => $"[LỖI giao diện] {x.GetType().Name}: {x.Message}"))
                        .ToList();
            var tabs = w.Views.Count();
            if (errs.Count > 0)
            {
                var s = string.Join(" | ", errs);
                items.Add(new CheckItem("Cửa sổ chính", "fail", s.Length > 300 ? s[..300] : s));
            }
            else if (!rendered || tabs != 5 || w.Title != AppInfo.Title)
            {
                items.Add(new CheckItem("Cửa sổ chính", "fail", $"thẻ: {tabs}, tiêu đề: \"{w.Title}\", đã vẽ: {(rendered ? "có" : "không")}"));
            }
            else
            {
                var secs = sw.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
                items.Add(new CheckItem("Cửa sổ chính", "ok", $"dựng được {tabs} thẻ trong {secs} s, tiêu đề \"{AppInfo.Title}\""));
            }
            items.Add(FontItem(w));
            items.Add(w.Icon is not null
                ? new CheckItem("Icon cửa sổ", "ok", "đã đặt")
                : new CheckItem("Icon cửa sổ", "warn", "không đặt được (vẫn chạy bình thường)"));
        }
        catch (Exception e)
        {
            items.Add(new CheckItem("Cửa sổ chính", "fail", $"{e.GetType().Name}: {e.Message}"));
        }
        finally
        {
            try
            {
                w?.CloseQuiet();
            }
            catch (Exception e) when (e is InvalidOperationException or IOException)
            {
                // dong cua so thu that bai: bo qua (tien trinh sap thoat)
            }
            Environment.SetEnvironmentVariable(env, old);
            try
            {
                Directory.Delete(tmp, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // con file dang mo: bo qua
            }
        }
    }

    /// <summary>Chạy vòng lặp giao diện tới khi <paramref name="done"/> (hoặc hết giờ) — cửa sổ thử cần được vẽ và làm xong việc lúc mở.</summary>
    private static void Pump(Func<bool> done, TimeSpan max)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.Elapsed < max)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            if (!done())
                Thread.Sleep(15);
        }
    }

    /// <summary>"Phông chữ tiếng Việt": phông đầu tiên CÓ trên máy của mỗi danh sách (giao diện; bảng + log) phải có đủ chữ có dấu.</summary>
    private static CheckItem FontItem(Window w)
    {
        var ok = new List<string>();
        var bad = new List<string>();
        foreach (var (role, fam) in new[] { ("giao diện", w.FontFamily), ("bảng, log", (FontFamily)w.FindResource("MonoFont")) })
        {
            var (name, gt) = Installed(fam);
            if (gt is null)
            {
                bad.Add($"không có phông {fam.Source}");
                continue;
            }
            var miss = VietChars.Where(c => !gt.CharacterToGlyphMap.ContainsKey(c)).ToList();
            if (miss.Count > 0)
                bad.Add($"{name} thiếu {miss.Count} chữ có dấu ({string.Concat(miss.Take(8))})");
            else
                ok.Add($"{name} ({role})");
        }
        if (bad.Count > 0)
            return new CheckItem("Phông chữ tiếng Việt", "warn", string.Join("; ", bad) + " — chữ có dấu có thể xấu");
        return new CheckItem("Phông chữ tiếng Việt", "ok", string.Join(", ", ok) + " — đủ chữ có dấu");
    }

    /// <summary>Phông đầu tiên có trên máy trong danh sách dự phòng (<c>Cascadia Mono, JetBrains Mono, Consolas</c>).</summary>
    private static (string Name, GlyphTypeface? Glyphs) Installed(FontFamily fam)
    {
        var have = Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in fam.Source.Split(','))
        {
            var name = raw.Trim();
            if (!have.Contains(name))
                continue;
            if (new Typeface(new FontFamily(name), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal).TryGetGlyphTypeface(out var gt))
                return (name, gt);
        }
        return ("", null);
    }
}
