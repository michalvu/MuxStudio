using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using MuxStudio.Core;
using MuxStudio.Core.Ui;

namespace MuxStudio.App;

/// <summary>Điểm vào (port <c>main</c> của gui/app.py): <c>MuxStudio.exe [thư mục phim]</c>.</summary>
public partial class App : Application
{
    /// <summary>Đang chạy bản phát hành một file (<c>dotnet publish -p:PublishSingleFile=true</c>), không phải từ mã nguồn.</summary>
    public static bool IsFrozen => string.IsNullOrEmpty(typeof(App).Assembly.Location);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUiError;
        try
        {
            var cfg = ConfigSchema.LoadConfig();
            // ngon ngu giao dien (vi / en) - dat TRUOC khi tao cua so; co hieu luc khi mo lai chuong trinh
            I18n.SetLanguage(cfg["UiLanguage"]);
            ThemeService.Init();
            var folder = e.Args.Length > 0 && Directory.Exists(e.Args[0]) ? e.Args[0] : null;
            var w = new MainWindow(cfg, folder);
            MainWindow = w;
            w.Show();
        }
        catch (Exception ex) // khong co cua so console: ghi loi ra file + bao bang hop thoai
        {
            var p = WriteStartupError(ex.ToString());
            MessageBox.Show($"Không khởi động được:\n\n{ex.Message}\n\nChi tiết: {p}", AppInfo.Title, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>Lỗi giao diện chưa bắt (= <c>report_callback_exception</c>): ghi vào khung log, chương trình chạy tiếp.</summary>
    private void OnUiError(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (MainWindow is MainWindow w && w.IsLoaded)
        {
            w.LogTrace(e.Exception);
            e.Handled = true;
        }
    }

    /// <summary><c>write_startup_error</c>: lỗi khởi động → <c>%APPDATA%\MuxStudio\loi_khoi_dong.txt</c>.</summary>
    public static string WriteStartupError(string tb)
    {
        var p = Path.Combine(ConfigSchema.AppConfigDir(), "loi_khoi_dong.txt");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, $"{AppInfo.BuildInfo(IsFrozen, "")}\nLúc: {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}\n\n{tb}",
                              new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // khong ghi duoc: chi bao bang hop thoai
        }
        return p;
    }
}
