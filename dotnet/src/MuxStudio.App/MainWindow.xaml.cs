using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using MuxStudio.App.Views;
using MuxStudio.Core;
using MuxStudio.Core.Ui;
using Wpf.Ui.Controls;

namespace MuxStudio.App;

/// <summary>Một thẻ (① ② ③ Cài đặt Giới thiệu) — các hàm cửa sổ chính gọi tới (= <c>hasattr(v, "on_…")</c> của bản Python).</summary>
public interface IStepView
{
    FrameworkElement Root { get; }

    void UpdateButtons() { }

    void OnFolderChanged() { }

    void OnFilesChanged() { }

    void OnConfigChanged(bool secretsChanged) { }

    void OnShow() { }

    void Close() { }
}

/// <summary>
/// Cửa sổ chính MuxStudio (port <c>App</c> của gui/app.py): thư mục phim chọn MỘT lần ở đầu cửa sổ, năm thẻ theo đúng thứ tự quy trình
/// (① Tải phụ đề → ② Chuẩn bị phụ đề → ③ Mux → Cài đặt → Giới thiệu) ở cột trái, thanh tiến độ + nút Dừng, khung log màu luôn hiện ở dưới,
/// màn hình Hoàn tác chung cho ba bước. Bố cục theo bản thiết kế "Concept 3 Pro Workbench".
/// </summary>
public partial class MainWindow : FluentWindow
{
    public const string TabFetch = "① Tải phụ đề";
    public const string TabPrep = "② Chuẩn bị phụ đề";
    public const string TabMux = "③ Mux";
    public const string TabSettings = "Cài đặt";
    public const string TabAbout = "Giới thiệu";

    private static readonly string[] TabKeys = ["fetch", "prep", "mux", "settings", "about"];

    private readonly Dictionary<string, IStepView> _views = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Wpf.Ui.Controls.Button> _nav = new(StringComparer.Ordinal);
    private readonly bool _forcedAppearance;
    private bool _secretErrorShown;
    private bool _closing;
    private string _current = "fetch";

    public MainWindow(Config cfg, string? folder = null, string? appearance = null, SecretStore? store = null)
    {
        Cfg = cfg;
        SecretStore = store ?? MuxStudio.Core.Secrets.Store;
        _forcedAppearance = !string.IsNullOrEmpty(appearance);
        InitializeComponent();
        Title = AppInfo.Title;
        TitleBarCtl.Title = AppInfo.Title;
        Icon = AppIcon.Image;
        VersionBadge.Text = $"MUXSTUDIO {AppInfo.Version}";
        Redactor = new Redactor(Secrets().Values);
        Backdrop = ThemeService.Backdrop(cfg.Str("UiBackdrop"));
        WindowBackdropType = Backdrop;
        if (Backdrop == WindowBackdropType.None)
            SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        PlaceWindow();
        LogView.Folder = () => Folder;
        LogView.Redact = Redactor.Redact;
        LogView.ExpandedChanged += LogExpandedChanged;
        Runner = new Runner(Dispatcher, LogView.WriteMany, OnProgress, OnBusy, Redactor.Redact);
        // dong dau khung log: ban nao dang chay (nguoi dung bao loi kem "Luu log")
        Log("head", $"{AppInfo.Title} — chọn thư mục phim ở trên, rồi làm lần lượt ① → ② → ③.");
        Log("dim", App.BuildInfo());
        StatusText.Text = L.T("Sẵn sàng.");
        ProgPct.Text = "";

        Fetch = new FetchView(this);
        Prep = new PrepView(this);
        Mux = new MuxView(this);
        Settings = new SettingsView(this);
        About = new AboutView(this);
        foreach (var (k, v) in new (string, IStepView)[] { ("fetch", Fetch), ("prep", Prep), ("mux", Mux), ("settings", Settings), ("about", About) })
        {
            _views[k] = v;
            v.Root.Visibility = Visibility.Collapsed;
            ContentHost.Children.Add(v.Root);
        }
        _nav["fetch"] = NavFetch;
        _nav["prep"] = NavPrep;
        _nav["mux"] = NavMux;
        _nav["settings"] = NavSettings;
        _nav["about"] = NavAbout;
        ShowTab("fetch");

        ThemeService.Changed += UpdateThemeButtons;
        Loaded += (_, _) =>
        {
            ThemeService.Apply(appearance ?? cfg.Str("UiAppearance"), this, Backdrop);
            UpdateThemeButtons();
            if (Plan.Zoomed && !SelfTest)
                WindowState = WindowState.Maximized;
        };
        Closing += OnClosingWindow;
        foreach (var w in cfg.Warnings)
            Log("warn", w);
        var start = folder ?? OptStr("UiLastFolder");
        if (start.Length > 0 && Directory.Exists(start))
            SetFolder(start, quiet: true);
        UpdateState();
        UpdateViews();
        // nhu ban Python: OnShow chi chay khi BAN chuyen the (khong tu goi mang luc mo chuong trinh)
    }

    public Config Cfg { get; }

    /// <summary>Dựng thử khi tự kiểm tra (<c>--kiem-tra</c>): cửa sổ nằm ngoài màn hình nên không phóng to (= <c>root.withdraw()</c>).</summary>
    public bool SelfTest { get; init; }

    public SecretStore SecretStore { get; }

    public Redactor Redactor { get; }

    public Runner Runner { get; }

    public WindowBackdropType Backdrop { get; }

    public WindowPlan Plan { get; private set; } = new(1400, 900, 960, 560, false, "normal");

    /// <summary>compact (720p, 1080p @150%) | normal (1080p) | large (1440p+).</summary>
    public string Density => Plan.Density;

    public string? Folder { get; private set; }

    public FetchView Fetch { get; }

    public PrepView Prep { get; }

    public MuxView Mux { get; }

    public SettingsView Settings { get; }

    public AboutView About { get; }

    public IEnumerable<IStepView> Views => _views.Values;

    // ------------------------------------------------------------------ cua so
    private void PlaceWindow()
    {
        double sw = SystemParameters.PrimaryScreenWidth, sh = SystemParameters.PrimaryScreenHeight;
        Plan = WindowLayout.PlanWindow(sw, sh, OptStr("UiGeometry"), OptBool("UiZoomed"));
        var f = WindowLayout.UiFactor(Cfg.Str("UiScale"), sw, sh);
        if (Math.Abs(f - 1.0) > 0.01)
            Root.LayoutTransform = new ScaleTransform(f, f);
        Width = Plan.Width;
        Height = Plan.Height;
        MinWidth = Plan.MinWidth;
        MinHeight = Plan.MinHeight;
        var logH = Density switch { "compact" => 64, "large" => 180, _ => 120 };
        var saved = OptInt("UiLogHeight");
        LogRow.Height = new GridLength(Math.Max(LogUi.MinHeight, saved > 0 ? Math.Min(saved, LogMax()) : logH) + 44);
    }

    // khoa phu cua giao dien (UiGeometry, UiZoomed, UiLogHeight, UiLastFolder): khong nam trong bang khoa, = cfg.get(...) cua ban Python
    public string OptStr(string key) => Cfg.ContainsKey(key) && Cfg[key] is string s ? s : "";

    public bool OptBool(string key) => Cfg.ContainsKey(key) && Cfg[key] is true;

    public int OptInt(string key) => Cfg.ContainsKey(key) && Cfg[key] is long l ? (int)Math.Clamp(l, 0, 100000) : 0;

    /// <summary>Khung nhật ký cao tối đa: chừa ~300 đơn vị cho các thẻ.</summary>
    private int LogMax() => Math.Max(80, (int)(ActualHeight > 200 ? ActualHeight : Plan.Height) - 300);

    private GridLength _logExpanded;

    private void LogExpandedChanged(bool expanded)
    {
        if (!expanded)
        {
            _logExpanded = LogRow.Height;
            LogRow.Height = GridLength.Auto;
            LogSplitter.IsEnabled = false;
        }
        else
        {
            LogRow.Height = _logExpanded.Value > 0 ? _logExpanded : new GridLength(160);
            LogSplitter.IsEnabled = true;
        }
    }

    private void LogSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        var h = (int)Math.Max(LogUi.MinHeight, Math.Min(LogRow.ActualHeight - 44, LogMax()));
        LogRow.Height = new GridLength(h + 44);
        Cfg["UiLogHeight"] = (long)h;
    }

    /// <summary>Nhớ cỡ cửa sổ cho lần sau: phóng to hết màn hình, hoặc <c>RỘNGxCAO</c> theo đơn vị LOGIC (DIP).</summary>
    private void RememberWindow()
    {
        if (WindowState == WindowState.Maximized)
        {
            Cfg["UiZoomed"] = true;
            return;
        }
        Cfg["UiZoomed"] = false;
        var b = RestoreBounds.IsEmpty ? new Rect(0, 0, Width, Height) : RestoreBounds;
        Cfg["UiGeometry"] = string.Create(CultureInfo.InvariantCulture, $"{(int)b.Width}x{(int)b.Height}");
    }

    // ------------------------------------------------------------------ chung
    /// <summary>Bí mật hiện có (Credential Manager). Lỗi đọc → báo một lần, coi như trống.</summary>
    public Dictionary<string, string> Secrets()
    {
        try
        {
            return SecretStore.AllValues();
        }
        catch (Exception e)
        {
            if (!_secretErrorShown)
            {
                _secretErrorShown = true;
                Dispatcher.BeginInvoke(() => Log("error", $"[!] Không đọc được Windows Credential Manager: {e.Message}"));
            }
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>Ghi một dòng log từ luồng giao diện (lọc bí mật, dịch).</summary>
    public void Log(string level, string msg) => LogView.Write(level, L.T(Redactor.Redact(msg)));

    public void LogTrace(Exception e)
    {
        Log("error", $"[LỖI giao diện] {e.Message}");
        foreach (var ln in Runner.TraceTail(e, 8))
            LogView.Write("dim", "    " + ln);
    }

    public void Goto(string tab)
    {
        var key = tab switch
        {
            TabFetch => "fetch",
            TabPrep => "prep",
            TabMux => "mux",
            TabSettings => "settings",
            TabAbout => "about",
            _ => tab,
        };
        SwitchTab(key);
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string key })
            SwitchTab(key);
    }

    /// <summary><c>_tab_changed</c>: rời thẻ Cài đặt khi còn sửa chưa lưu → hỏi lưu.</summary>
    private void SwitchTab(string key)
    {
        if (key == _current)
            return;
        if (_current == "settings" && Settings.IsDirty())
        {
            var ans = Dlg.AskYesNoCancel(this, "Cài đặt", "Bạn đã sửa cài đặt mà chưa lưu. Lưu ngay bây giờ?");
            if (ans is null)
                return;
            if (ans == true)
            {
                if (!Settings.Save())
                    return;
            }
            else
            {
                Settings.Reload();
            }
        }
        ShowTab(key);
        _views[key].OnShow();
    }

    private void ShowTab(string key)
    {
        _current = key;
        foreach (var (k, v) in _views)
            v.Root.Visibility = k == key ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (k, b) in _nav)
        {
            // the dang mo: nen + vien mau ho phach (nhu ban thiet ke); the khac: trong suot
            var on = k == key;
            b.Appearance = on ? ControlAppearance.Secondary : ControlAppearance.Transparent;
            if (on)
            {
                b.Background = (System.Windows.Media.Brush)FindResource("AmberSoftBgBrush");
                b.BorderBrush = (System.Windows.Media.Brush)FindResource("AmberAccentBrush");
                b.BorderThickness = new Thickness(1.5);
            }
            else
            {
                b.ClearValue(BackgroundProperty);
                b.ClearValue(BorderBrushProperty);
                b.ClearValue(BorderThicknessProperty);
            }
        }
    }

    // ------------------------------------------------------------------ thu muc phim
    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var cur = Folder ?? OptStr("UiLastFolder");
        var dlg = new OpenFolderDialog { Title = L.T("Chọn thư mục phim") };
        if (cur.Length > 0 && Directory.Exists(cur))
            dlg.InitialDirectory = cur;
        if (dlg.ShowDialog(this) == true)
            SetFolder(dlg.FolderName);
    }

    private void FolderBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            SetFolder(FolderBox.Text);
    }

    public bool SetFolder(string path, bool quiet = false)
    {
        var t = (path ?? "").Trim().Trim('"');
        var p = t.Length > 0 ? Path.GetFullPath(t).TrimEnd('\\') : "";
        if (p.EndsWith(':'))
            p += "\\";
        if (p.Length == 0 || !Directory.Exists(p))
        {
            if (!quiet)
                Dlg.Warn(this, "Thư mục phim", $"Không có thư mục này:\n{path}");
            return false;
        }
        if (Runner.Busy)
        {
            Dlg.Warn(this, "Đang chạy", "Đợi việc đang chạy xong (hoặc bấm Dừng) rồi mới đổi thư mục phim.");
            FolderBox.Text = Folder ?? "";
            return false;
        }
        Folder = p;
        FolderBox.Text = p;
        Cfg["UiLastFolder"] = p;
        try
        {
            ConfigSchema.SaveConfig(Cfg);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // khong ghi duoc cau hinh: van dung thu muc
        }
        Log("head", $"Thư mục phim: {p}");
        foreach (var v in Views)
            v.OnFolderChanged();
        UpdateState();
        UpdateViews();
        return true;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Folder is null || !Shell.Open(Folder))
            Dlg.Warn(this, "Mở thư mục", "Chưa chọn thư mục phim.");
    }

    public string? NeedFolder()
    {
        if (Folder is not null && Directory.Exists(Folder))
            return Folder;
        Dlg.Warn(this, "Chưa chọn thư mục phim", "Chọn thư mục phim ở đầu cửa sổ trước.");
        return null;
    }

    // ------------------------------------------------------------------ sang / toi
    private void SetAppearance(string mode)
    {
        Cfg["UiAppearance"] = mode;
        try
        {
            ConfigSchema.SaveConfig(Cfg);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // khong ghi duoc: chi doi lan nay
        }
        ThemeService.Apply(mode, this, Backdrop);
        Settings.Reload();
        UpdateThemeButtons();
    }

    private void Light_Click(object sender, RoutedEventArgs e) => SetAppearance("light");

    private void Dark_Click(object sender, RoutedEventArgs e) => SetAppearance("dark");

    private void System_Click(object sender, RoutedEventArgs e) => SetAppearance("system");

    private void UpdateThemeButtons()
    {
        var m = Cfg.Str("UiAppearance");
        LightBtn.Appearance = m == "light" ? ControlAppearance.Primary : ControlAppearance.Secondary;
        DarkBtn.Appearance = m == "dark" ? ControlAppearance.Primary : ControlAppearance.Secondary;
        SystemBtn.Appearance = m is not ("light" or "dark") ? ControlAppearance.Primary : ControlAppearance.Secondary;
    }

    // ------------------------------------------------------------------ CAPTCHA (goi tu luong phu)
    /// <summary>Lõi (luồng phụ) gặp CAPTCHA của Titulky → mở hộp thoại ở luồng giao diện, chờ BẠN gõ mã. <c>null</c> = bỏ qua.</summary>
    public string? AskCaptcha(byte[] image, string note) => Runner.Ask(() =>
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        return Dlg.Captcha(this, image, note);
    });

    public void OnConfigChanged(bool secretsChanged = false)
    {
        Redactor.Update(Secrets().Values);
        if (!_forcedAppearance)
        {
            ThemeService.Apply(Cfg.Str("UiAppearance"), this, Backdrop);
            UpdateThemeButtons();
        }
        foreach (var v in Views)
        {
            if (v != Settings)
                v.OnConfigChanged(secretsChanged);
        }
        UpdateState();
    }

    // ------------------------------------------------------------------ tien do / dung
    private void OnBusy(bool busy, string title)
    {
        StopBtn.IsEnabled = busy;
        StopNowBtn.IsEnabled = busy;
        UndoBtn.IsEnabled = !busy;
        StatusDot.Fill = (Brush)FindResource(busy ? "AmberAccentBrush" : "EmeraldAccentBrush");
        StateBusy.Text = busy ? "BUSY" : "IDLE";
        StateBusy.Foreground = StatusDot.Fill;
        if (busy)
        {
            Prog.Value = 0;
            ProgPct.Text = UiText.ProgressText(0, 0);
            StatusText.Text = L.T($"Đang chạy: {title}…");
            Cur.IsIndeterminate = true;
        }
        else
        {
            Cur.IsIndeterminate = false;
            Cur.Value = 0;
            StatusText.Text = L.T(title.Length > 0 ? $"Xong: {title}." : "Sẵn sàng.");
        }
        UpdateViews();
    }

    private void OnProgress(int done, int total, string label)
    {
        if (total > 0)
            Prog.Value = Math.Max(0.0, Math.Min(1.0, (double)done / total));
        ProgPct.Text = UiText.ProgressText(done, total);
        if (Runner.Title == "Mux")
            Mux.ShowRun(done, total, label); // tien do trong khung "Output & mkvmerge" cua the ③
        var t = L.T(Runner.Title);
        StatusText.Text = string.Create(CultureInfo.InvariantCulture, $"{t}: {done}/{total}") + (label.Length > 0 ? " — " + UiText.EllipsizeMiddle(label, 90) : "");
    }

    public void UpdateViews()
    {
        foreach (var v in Views)
            v.UpdateButtons();
    }

    /// <summary>Thẻ "WORKBENCH STATE" ở cột trái.</summary>
    public void UpdateState()
    {
        StateFolder.Text = Folder is null ? "—" : Path.GetFileName(Folder.TrimEnd('\\')) is { Length: > 0 } n ? n : Folder;
        StateFolder.ToolTip = Folder;
        var langs = Cfg.List("Languages");
        StateLangs.Text = langs.Count > 0 ? string.Join(" · ", langs.Select(x => x.ToUpperInvariant())) : "—";
        StateRef.Text = L.T(Prep.IsAudioMode ? "Audio" : "Phụ đề gốc");
        StateDb.Text = Mux.DbChoice switch
        {
            "tmdb" => "TMDB",
            "tvdb" => "TVDB",
            "none" => L.T("Không đổi tên"),
            _ => L.T("chưa chọn"),
        };
        StateCounts.Text = Prep.CountsLine;
    }

    private void StopAfter_Click(object sender, RoutedEventArgs e) => Runner.StopAfterCurrent();

    private void StopNow_Click(object sender, RoutedEventArgs e) => Runner.CancelNow();

    // ------------------------------------------------------------------ hoan tac
    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        var folder = NeedFolder();
        if (folder is not null)
            new UndoDialog(this, folder).Run();
    }

    // ------------------------------------------------------------------ dong
    /// <summary>Đóng không hỏi, không lưu cấu hình (cửa sổ dựng thử của <c>--kiem-tra</c>): dừng việc đang chạy, dọn thư mục tạm.</summary>
    internal void CloseQuiet()
    {
        _closing = true;
        try
        {
            Runner.Job?.CancelNow();
        }
        catch (ObjectDisposedException)
        {
            // viec vua xong
        }
        foreach (var v in Views)
        {
            try
            {
                v.Close();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // xoa thu muc tam that bai: bo qua
            }
        }
        Close();
    }

    private void OnClosingWindow(object? sender, CancelEventArgs e)
    {
        if (_closing)
            return;
        if (Runner.Busy)
        {
            if (!Dlg.AskYesNo(this, "Thoát", "Đang có việc chạy. Dừng ngay và thoát?"))
            {
                e.Cancel = true;
                return;
            }
            try
            {
                Runner.Job?.CancelNow();
            }
            catch (ObjectDisposedException)
            {
                // viec vua xong
            }
        }
        if (Settings.IsDirty())
        {
            var ans = Dlg.AskYesNoCancel(this, "Cài đặt", "Lưu cài đặt đã sửa trước khi thoát?");
            if (ans is null || (ans == true && !Settings.Save()))
            {
                e.Cancel = true;
                return;
            }
        }
        _closing = true;
        try
        {
            RememberWindow();
            ConfigSchema.SaveConfig(Cfg);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConfigValueException)
        {
            // khong ghi duoc cau hinh luc thoat: bo qua
        }
        foreach (var v in Views)
        {
            try
            {
                v.Close();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // xoa thu muc tam that bai: bo qua
            }
        }
    }
}
