using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MuxStudio.Core;
using MuxStudio.Core.Ui;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;

namespace MuxStudio.App;

/// <summary>
/// Điểm vào (port <c>main</c> của gui/app.py): <c>MuxStudio.exe [thư mục phim]</c>; <c>MuxStudio.exe --kiem-tra [báo cáo.txt]</c> = tự kiểm
/// tra bản đóng gói (thư viện, Credential Manager, Thùng rác, chứng chỉ HTTPS, dựng thử cửa sổ…), ghi báo cáo, mã thoát 0 = đạt.
/// Bản .exe không có cửa sổ console: lỗi khởi động / lỗi làm chương trình phải đóng → <c>%APPDATA%\MuxStudio\loi_khoi_dong.txt</c>.
/// </summary>
public partial class App : Application
{
    private bool _fatalShown;
    private bool _selfCheck;

    /// <summary>Đang chạy bản phát hành một file (<c>build.bat</c>), không phải bản dựng từ mã nguồn.</summary>
    public static bool IsFrozen => AppInfo.IsFrozen;

    /// <summary><c>build_info</c>: một dòng — MuxStudio, bản .exe / mã nguồn, .NET, Windows, WPF-UI (dòng thứ hai khung log, báo cáo, file lỗi).</summary>
    public static string BuildInfo() =>
        AppInfo.BuildInfo(IsFrozen, $"WPF-UI {typeof(FluentWindow).Assembly.GetName().Version?.ToString(3)}");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUiError;
        AppDomain.CurrentDomain.UnhandledException += OnFatalError;
        TaskScheduler.UnobservedTaskException += OnUnobservedTask;
        if (SelfCheck.IsSelfCheck(e.Args, out var report))
        {
            // khong co cua so chinh: dong cua so dung thu khong duoc lam thoat chuong trinh truoc khi ghi xong bao cao
            _selfCheck = true;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dispatcher.BeginInvoke(() => Shutdown(SelfCheckWpf.Run(report)));
            return;
        }
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
            ShowFatal(ex, "Không khởi động được");
            Shutdown(1);
        }
    }

    /// <summary>Lỗi giao diện chưa bắt (= <c>report_callback_exception</c>): đã mở cửa sổ → ghi vào khung log, chương trình chạy tiếp;
    /// chưa mở xong (đang khởi động) → như lỗi khởi động.</summary>
    private void OnUiError(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (_selfCheck) // tu kiem tra: khong hoi gi (build.bat dang cho) - loi vao bao cao
        {
            SelfCheckWpf.UiErrors.Add(e.Exception);
            return;
        }
        if (MainWindow is MainWindow w && w.IsLoaded)
        {
            w.LogTrace(e.Exception);
            return;
        }
        ShowFatal(e.Exception, "Không khởi động được");
        Shutdown(1);
    }

    /// <summary>Lỗi ở luồng phụ ngoài <see cref="Runner"/>: .NET buộc chương trình đóng — ghi file + báo trước khi đóng.</summary>
    private void OnFatalError(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            ShowFatal(ex, "MuxStudio gặp lỗi và phải đóng");
    }

    /// <summary>Task lỗi mà không ai chờ (không làm chương trình đóng): ghi vào khung log.</summary>
    private void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        var ex = e.Exception.InnerExceptions.Count == 1 ? e.Exception.InnerExceptions[0] : e.Exception;
        Dispatcher.BeginInvoke(() =>
        {
            if (MainWindow is MainWindow w && w.IsLoaded)
                w.LogTrace(ex);
        });
    }

    /// <summary>Ghi <c>loi_khoi_dong.txt</c> rồi báo bằng hộp thoại (một lần).</summary>
    private void ShowFatal(Exception ex, string what)
    {
        var p = WriteStartupError(ex.ToString());
        if (_fatalShown || _selfCheck)
            return;
        _fatalShown = true;
        MessageBox.Show($"{what}:\n\n{ex.Message}\n\nChi tiết: {p}", AppInfo.Title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary><c>write_startup_error</c>: lỗi khởi động → <c>%APPDATA%\MuxStudio\loi_khoi_dong.txt</c>.</summary>
    public static string WriteStartupError(string tb) => AppInfo.WriteStartupError(tb, BuildInfo());
}
