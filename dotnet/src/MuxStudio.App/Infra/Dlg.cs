using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace MuxStudio.App;

/// <summary>
/// Hộp thoại kiểu Fluent (port <c>W.Dialog</c>): tiêu đề, thân (<see cref="Body"/> là <see cref="DockPanel"/>: thêm chữ ở trên, phần tử
/// cuối lấp đầy), hàng nút bên phải. <see cref="Run"/> = mở kiểu modal, trả giá trị đã <see cref="Close"/>.
/// </summary>
public sealed class Dlg
{
    private readonly StackPanel _buttons;
    private object? _result;

    public Dlg(DependencyObject? owner, string title, double width, double height)
    {
        Win = new FluentWindow
        {
            Title = L.T(title),
            Width = width,
            Height = height,
            MinWidth = Math.Min(width, 420),
            MinHeight = Math.Min(height, 200),
            ExtendsContentIntoTitleBar = true,
            WindowBackdropType = WindowBackdropType.None,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.CanResize,
        };
        Win.SetResourceReference(Control.BackgroundProperty, "ApplicationBackgroundBrush");
        var o = owner is null ? null : owner as Window ?? Window.GetWindow(owner);
        o ??= Application.Current?.MainWindow;
        if (o is not null && o != Win && o.IsLoaded)
            Win.Owner = o;
        else
            Win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var tb = new TitleBar { Title = L.T(title), ShowMaximize = false, ShowMinimize = false };
        root.Children.Add(tb);
        Body = new DockPanel { Margin = new Thickness(18, 6, 18, 8), LastChildFill = true };
        Grid.SetRow(Body, 1);
        root.Children.Add(Body);
        _buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(18, 4, 18, 16) };
        Grid.SetRow(_buttons, 2);
        root.Children.Add(_buttons);
        Win.Content = root;
        Win.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close(null);
        };
    }

    public FluentWindow Win { get; }

    public DockPanel Body { get; }

    /// <summary>Bề ngang chữ (<c>d.wrap</c>).</summary>
    public double Wrap => Win.Width - 60;

    /// <summary>Thêm một đoạn chữ ở trên cùng thân hộp thoại. <paramref name="level"/>: màu log (info / dim / warn / error / ok).</summary>
    public TextBlock Text(string text, string level = "", bool bold = false, double top = 0)
    {
        var t = new TextBlock
        {
            Text = L.T(text), TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, top, 0, 6),
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, LevelBrush(level));
        DockPanel.SetDock(t, Dock.Top);
        Body.Children.Add(t);
        return t;
    }

    public static string LevelBrush(string level) => level switch
    {
        "ok" => "LogOkBrush",
        "warn" => "LogWarnBrush",
        "error" => "LogErrorBrush",
        "dim" => "LogDimBrush",
        "head" => "LogHeadBrush",
        _ => "TextFillColorPrimaryBrush",
    };

    /// <summary>Phần tử lấp đầy phần còn lại (bảng, ô chữ…) — gọi SAU các <see cref="Text"/>.</summary>
    public T Fill<T>(T element) where T : UIElement
    {
        Body.Children.Add(element);
        return element;
    }

    public T Top<T>(T element) where T : UIElement
    {
        DockPanel.SetDock(element, Dock.Top);
        Body.Children.Add(element);
        return element;
    }

    /// <summary>Thêm một nút (xếp từ phải sang: nút thêm TRƯỚC nằm bên phải như bản Python — <c>pack(side="right")</c>).</summary>
    public Button Button(string text, Action onClick, bool primary = false)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = L.T(text) }, Margin = new Thickness(8, 0, 0, 0), MinWidth = 96, // TextBlock: giu dau "_" (srt_goc)
            Appearance = primary ? ControlAppearance.Primary : ControlAppearance.Secondary,
        };
        b.Click += (_, _) => onClick();
        _buttons.Children.Insert(0, b);
        if (primary)
            b.IsDefault = true;
        return b;
    }

    public void Close(object? result)
    {
        _result = result;
        Win.Close();
    }

    public object? Run()
    {
        Win.ShowDialog();
        return _result;
    }

    // ==========================================================================
    //  Hộp thoại có sẵn (port info / warn / ask_yes_no / ask_yes_no_cancel / ask_string / confirm_list / show_text)
    // ==========================================================================

    private static void Message(DependencyObject? owner, string title, string message, string level)
    {
        var d = new Dlg(owner, title, 560, 260);
        d.Win.SizeToContent = SizeToContent.Height;
        d.Text(message, level);
        d.Button("OK", () => d.Close(true), true);
        d.Run();
    }

    public static void Info(DependencyObject? owner, string title, string message) => Message(owner, title, message, "");

    public static void Warn(DependencyObject? owner, string title, string message) => Message(owner, title, message, "warn");

    public static bool AskYesNo(DependencyObject? owner, string title, string message)
    {
        var d = new Dlg(owner, title, 560, 260);
        d.Win.SizeToContent = SizeToContent.Height;
        d.Text(message);
        d.Button("Không", () => d.Close(false));
        d.Button("Có", () => d.Close(true), true);
        return d.Run() is true;
    }

    /// <summary>Có / Không / Huỷ (<c>null</c>).</summary>
    public static bool? AskYesNoCancel(DependencyObject? owner, string title, string message)
    {
        var d = new Dlg(owner, title, 600, 260);
        d.Win.SizeToContent = SizeToContent.Height;
        d.Text(message);
        d.Button("Huỷ", () => d.Close(null));
        d.Button("Không", () => d.Close(false));
        d.Button("Có", () => d.Close(true), true);
        return d.Run() as bool?;
    }

    public static string? AskString(DependencyObject? owner, string title, string prompt, string initial = "")
    {
        var d = new Dlg(owner, title, 560, 230);
        d.Win.SizeToContent = SizeToContent.Height;
        d.Text(prompt);
        var e = d.Top(new TextBox { Text = initial, Margin = new Thickness(0, 4, 0, 8) });
        e.KeyDown += (_, ev) =>
        {
            if (ev.Key == Key.Enter)
                d.Close(e.Text);
        };
        d.Button("Huỷ", () => d.Close(null));
        d.Button("OK", () => d.Close(e.Text), true);
        d.Win.Loaded += (_, _) =>
        {
            e.Focus();
            e.SelectAll();
        };
        return d.Run() as string;
    }

    public static System.Windows.Controls.TextBox Console(string text, bool wrap = false)
    {
        var tb = new System.Windows.Controls.TextBox { Text = text, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        tb.SetResourceReference(FrameworkElement.StyleProperty, "ConsoleBox");
        return tb;
    }

    /// <summary><c>confirm_list</c>: hộp thoại xác nhận có danh sách đầy đủ (vd file sẽ vào Thùng rác).</summary>
    public static bool ConfirmList(DependencyObject? owner, string title, string message, IReadOnlyList<string> lines, string okText = "Đồng ý",
                                   string cancelText = "Huỷ", string warn = "")
    {
        var d = new Dlg(owner, title, 820, 480);
        d.Text(message);
        if (warn.Length > 0)
            d.Text(warn, "error");
        d.Fill(Console(lines.Count > 0 ? string.Join("\n", lines.Select(L.T)) : L.T("(không có)")));
        d.Button(cancelText, () => d.Close(false));
        d.Button(okText, () => d.Close(true), true);
        return d.Run() is true;
    }

    /// <summary><c>show_text</c>: hộp thoại xem chữ (vd "Xem lệnh") + nút "Chép".</summary>
    public static void ShowText(DependencyObject? owner, string title, string text, string note = "")
    {
        var d = new Dlg(owner, title, 960, 520);
        if (note.Length > 0)
            d.Text(note);
        d.Fill(Console(text, wrap: true));
        Button? copy = null;
        copy = d.Button("Chép", () =>
        {
            try
            {
                Clipboard.SetText(text);
                copy!.Content = new TextBlock { Text = L.T("Đã chép ✓") };
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // bo nho tam dang bi chuong trinh khac giu
            }
        });
        d.Button("Đóng", () => d.Close(null), true);
        d.Run();
    }

    /// <summary><c>CaptchaDialog</c>: hiện ảnh mã CAPTCHA (Titulky.com), BẠN gõ mã. Trả mã, hoặc <c>null</c> nếu bỏ qua.</summary>
    public static string? Captcha(DependencyObject? owner, byte[] image, string note = "", string site = "Titulky.com")
    {
        var d = new Dlg(owner, $"Mã CAPTCHA của {site}", 580, 420);
        d.Win.SizeToContent = SizeToContent.Height;
        d.Text($"{site} yêu cầu gõ mã trong ảnh trước khi cho tải (giới hạn tải của trang). Gõ đúng các ký tự trong ảnh rồi bấm Tải.");
        if (note.Length > 0)
            d.Text(note, "dim");
        BitmapImage? bmp = null;
        try
        {
            bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(image);
            bmp.EndInit();
            bmp.Freeze();
        }
        catch (Exception e) when (e is NotSupportedException or IOException or ArgumentException)
        {
            bmp = null;
        }
        if (bmp is not null)
        {
            d.Top(new Border
            {
                BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 10), Child = new System.Windows.Controls.Image { Source = bmp, Stretch = Stretch.None },
            });
        }
        else
        {
            d.Text("Không hiện được ảnh trong cửa sổ này.", "warn", top: 8);
            var open = d.Top(new Button { Content = L.T("Mở ảnh bằng trình xem ảnh"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) });
            open.Click += (_, _) =>
            {
                var ext = image.Length >= 4 && image[0] == 0x89 && image[1] == (byte)'P' ? ".png" : image.Length >= 3 && image[0] == (byte)'G' ? ".gif" : ".jpg";
                var path = Path.Combine(Path.GetTempPath(), "muxstudio_captcha_" + Guid.NewGuid().ToString("N")[..8] + ext);
                File.WriteAllBytes(path, image);
                Shell.Open(path);
            };
        }
        var row = d.Top(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center });
        row.Children.Add(new TextBlock { Text = L.T("Mã trong ảnh:"), FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var entry = new TextBox { Width = 220, FontSize = 16 };
        row.Children.Add(entry);
        void Submit()
        {
            var code = entry.Text.Trim();
            if (code.Length > 0)
                d.Close(code);
        }
        entry.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                Submit();
        };
        d.Button("Bỏ qua bản này", () => d.Close(null));
        d.Button("Tải", Submit, true);
        d.Win.Loaded += (_, _) => entry.Focus();
        try
        {
            d.Win.Activate();
        }
        catch (InvalidOperationException)
        {
            // cua so chua san sang
        }
        return d.Run() as string;
    }
}

/// <summary>Mở thư mục / file / trang web bằng chương trình mặc định (port <c>open_path</c>).</summary>
public static class Shell
{
    public static bool Open(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        var isUrl = path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (!isUrl && !File.Exists(path) && !Directory.Exists(path))
            return false;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
