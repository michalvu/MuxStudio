using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using MuxStudio.Core.Ui;

namespace MuxStudio.App.Views;

/// <summary>Một dòng của khung nhật ký (đã lọc bí mật + đã dịch).</summary>
public sealed record LogEntry(string Time, string Level, string Text);

/// <summary>
/// Khung log màu luôn hiện ở dưới cửa sổ (port gui/log_panel.py). Mức: info, ok (xanh), warn (vàng), error (đỏ), dim (xám), head (đậm).
/// Nút: Thu gọn / Mở rộng, Xoá, Lưu log (UTF-8), Mở thư mục. Kéo thanh ngăn phía trên để đổi chiều cao (nhớ trong <c>UiLogHeight</c>).
/// </summary>
public partial class LogPanel : UserControl
{
    private readonly ObservableCollection<LogEntry> _items = [];
    private readonly List<LogEntry> _all = [];
    private int _warn;
    private int _err;

    public LogPanel()
    {
        InitializeComponent();
        Box.ItemsSource = _items;
        Expanded = true;
        ToggleText.Text = L.T("▾ Nhật ký");
        Count();
    }

    /// <summary>Thư mục phim hiện tại (nút "Mở thư mục", nơi gợi ý lưu log).</summary>
    public Func<string?> Folder { get; set; } = () => null;

    /// <summary>Lọc bí mật lần cuối trước khi hiện (dòng từ luồng phụ đã lọc ở <see cref="MuxStudio.Core.Job"/>).</summary>
    public Func<string, string> Redact { get; set; } = s => s;

    public bool Expanded { get; private set; }

    /// <summary>Bấm "Thu gọn" / "Mở rộng" (cửa sổ chính đổi chiều cao hàng chứa khung).</summary>
    public event Action<bool>? ExpandedChanged;

    public IReadOnlyList<LogEntry> Lines => _all;

    public void Write(string level, string msg) => WriteMany([(level, msg)]);

    /// <summary>Ghi nhiều dòng một lần (chữ đã dịch). Giữ tối đa <see cref="LogUi.MaxLines"/> dòng trong khung; file "Lưu log" giữ đủ.</summary>
    public void WriteMany(IReadOnlyList<(string Level, string Message)> items)
    {
        if (items.Count == 0)
            return;
        var now = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        LogEntry? last = null;
        foreach (var (level, msg0) in items)
        {
            var lv = LogUi.Levels.Contains(level) ? level : "info";
            var e = new LogEntry(now, lv, Redact(msg0));
            _all.Add(e);
            _items.Add(e);
            _warn += lv == "warn" ? 1 : 0;
            _err += lv == "error" ? 1 : 0;
            last = e;
        }
        var over = _items.Count - LogUi.MaxLines;
        for (var i = 0; i < over; i++)
            _items.RemoveAt(0);
        if (last is not null && Expanded)
            Box.ScrollIntoView(last);
        Count();
    }

    private void Count()
    {
        var parts = new List<string> { L.T(string.Create(CultureInfo.InvariantCulture, $"{_all.Count} dòng")) };
        if (_warn > 0)
            parts.Add(L.T(string.Create(CultureInfo.InvariantCulture, $"{_warn} cảnh báo")));
        if (_err > 0)
            parts.Add(L.T(string.Create(CultureInfo.InvariantCulture, $"{_err} lỗi")));
        CountText.Text = string.Join(" · ", parts);
    }

    public void Clear()
    {
        _items.Clear();
        _all.Clear();
        _warn = _err = 0;
        Count();
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => Clear();

    private void Toggle_Click(object sender, RoutedEventArgs e) => Toggle();

    public void Toggle()
    {
        Expanded = !Expanded;
        Box.Visibility = Expanded ? Visibility.Visible : Visibility.Collapsed;
        ToggleText.Text = L.T(Expanded ? "▾ Nhật ký" : "▸ Nhật ký");
        if (Expanded && _items.Count > 0)
            Box.ScrollIntoView(_items[^1]);
        ExpandedChanged?.Invoke(Expanded);
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Save();

    /// <summary>Nút "Lưu log…": <c>HH:MM:SS  [mức]  chữ</c>, UTF-8.</summary>
    public string? Save()
    {
        var d = Folder();
        var dlg = new SaveFileDialog
        {
            Title = L.T("Lưu log"),
            InitialDirectory = d is not null && Directory.Exists(d) ? d : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            FileName = $"MuxStudio_log_{DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}.txt",
            DefaultExt = ".txt",
            Filter = L.T("Văn bản") + " (*.txt)|*.txt|" + L.T("Mọi file") + " (*.*)|*.*",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true)
            return null;
        File.WriteAllText(dlg.FileName, LogUi.FormatLog(_all.Select(x => (x.Time, x.Level, x.Text))), new System.Text.UTF8Encoding(false));
        Write("ok", L.T($"Đã lưu log: {dlg.FileName}"));
        return dlg.FileName;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!Shell.Open(Folder()))
            Dlg.Warn(this, "Mở thư mục", "Chưa chọn thư mục phim (hoặc thư mục không còn).");
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopySelected();

    private void Box_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            CopySelected();
            e.Handled = true;
        }
    }

    private void CopySelected()
    {
        var sel = Box.SelectedItems.OfType<LogEntry>().OrderBy(_items.IndexOf).Select(x => x.Text).ToList();
        if (sel.Count == 0)
            return;
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, sel));
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // bo nho tam dang bi chuong trinh khac giu
        }
    }
}
