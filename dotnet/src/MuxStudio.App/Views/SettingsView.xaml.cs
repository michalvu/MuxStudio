using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MuxStudio.Core;
using MuxStudio.Core.Ui;
using Button = Wpf.Ui.Controls.Button;
using UiTextBox = Wpf.Ui.Controls.TextBox;

namespace MuxStudio.App.Views;

/// <summary>
/// Thẻ "Cài đặt" (port gui/settings_view.py): mọi khoá của bảng khoá cấu hình, chia 5 nhóm, chú thích tiếng Việt; mục "Nâng cao" ẩn tới khi bật.
/// 9 bí mật (ô ••• + "Hiện") lưu vào Windows Credential Manager, KHÔNG vào config.json. "Kiểm tra kết nối" cho SubSource / TMDB / TVDB /
/// OpenSubtitles / Titulky (dùng giá trị đang gõ). "Tự tìm" công cụ + phiên bản đọc được. "Khôi phục mặc định" (không đụng bí mật),
/// "Nhập từ .bat…" (bảng xem trước → Nhập).
/// </summary>
public partial class SettingsView : UserControl, IStepView
{
    private readonly MainWindow _app;
    private readonly Dictionary<string, StackPanel> _groups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _groupBtns = new(StringComparer.Ordinal);
    private TextBlock? _toolLabel;
    private string _gid = "chung";

    public SettingsView(MainWindow app)
    {
        _app = app;
        InitializeComponent();
        foreach (var (gid, label) in ConfigSchema.Groups)
        {
            var b = new Button { Content = L.T(label), Margin = new Thickness(0, 0, 6, 4), Tag = gid };
            b.Click += (_, _) => ShowGroup(gid);
            _groupBtns[gid] = b;
            GroupPanel.Children.Insert(GroupPanel.Children.IndexOf(Adv), b);
        }
        ShowGroup("chung");
    }

    public FrameworkElement Root => this;

    public Dictionary<string, FieldRow> Rows { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, SecretRow> SecretRows { get; } = new(StringComparer.Ordinal);

    public MainWindow App => _app;

    /// <summary><c>status_color</c> → tên brush: chưa kiểm tra / không rõ = xám, được = xanh, lỗi = đỏ.</summary>
    public static string StatusBrush(ConnStatus? st) => st?.Ok is null ? "LogDimBrush" : st.Ok == true ? "LogOkBrush" : "LogErrorBrush";

    // ---- dung nhom (chi dung khi mo lan dau) ----
    private void ShowGroup(string gid)
    {
        _gid = gid;
        if (!_groups.TryGetValue(gid, out var panel))
            _groups[gid] = panel = BuildGroup(gid);
        Body.Content = panel;
        BodyScroll.ScrollToTop();
        foreach (var (k, b) in _groupBtns)
            b.Appearance = k == gid ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
    }

    private static TextBlock Header(string text, double top = 2)
    {
        var t = new TextBlock { Text = L.T(text), FontWeight = FontWeights.Bold, FontSize = 14, Margin = new Thickness(0, top, 0, 4), TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        return t;
    }

    private StackPanel BuildGroup(string gid)
    {
        var sp = new StackPanel();
        if (gid == "chung")
        {
            var box = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
            var h = Header("Công cụ ngoài");
            h.Margin = new Thickness(0, 0, 10, 0);
            box.Children.Add(h);
            var again = new Button { Content = L.T("Kiểm tra lại"), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            again.Click += (_, _) => CheckTools();
            box.Children.Add(again);
            _toolLabel = new TextBlock
            {
                Text = L.T("Ô để trống = chương trình tự tìm; dòng chữ dưới mỗi ô cho biết file đang được dùng."), TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _toolLabel.SetResourceReference(TextBlock.ForegroundProperty, "LogDimBrush");
            box.Children.Add(_toolLabel);
            sp.Children.Add(box);
        }
        if (gid == "api")
        {
            sp.Children.Add(Header("Bí mật — lưu trong Windows Credential Manager, KHÔNG lưu trong config.json, không bao giờ in ra log."));
            var vals = _app.Secrets();
            foreach (var k in Secrets.SecretKeys)
            {
                var sr = new SecretRow(this, k);
                sr.SetValue(vals.GetValueOrDefault(k, ""));
                SecretRows[k] = sr;
                sp.Children.Add(sr.Root);
            }
            sp.Children.Add(Header("Cấu hình API", 16));
        }
        foreach (var f in ConfigSchema.Fields)
        {
            if (f.Group != gid)
                continue;
            var fr = new FieldRow(this, f);
            fr.SetValue(_app.Cfg[f.Key]);
            if (f.Advanced && Adv.IsChecked != true)
                fr.SetVisible(false);
            Rows[f.Key] = fr;
            sp.Children.Add(fr.Root);
        }
        if (gid == "chung")
            Dispatcher.BeginInvoke(() => CheckTools(startup: true), System.Windows.Threading.DispatcherPriority.Background);
        return sp;
    }

    private void Adv_Click(object sender, RoutedEventArgs e)
    {
        foreach (var fr in Rows.Values)
        {
            if (fr.F.Advanced)
                fr.SetVisible(Adv.IsChecked == true);
        }
    }

    // ---- gia tri ----
    /// <summary>Cấu hình theo các ô đang gõ (ô sai kiểu → giữ giá trị cũ). Dùng cho "Tự tìm" / "Kiểm tra kết nối".</summary>
    public Config CurrentCfg()
    {
        var outp = SettingsUi.CopyConfig(_app.Cfg);
        foreach (var (k, fr) in Rows)
        {
            try
            {
                outp[k] = fr.GetValue();
            }
            catch (ConfigValueException)
            {
                // o sai kieu: giu gia tri cu
            }
        }
        return outp;
    }

    public Dictionary<string, string> CurrentSecrets()
    {
        var vals = new Dictionary<string, string>(_app.Secrets(), StringComparer.Ordinal);
        foreach (var (k, sr) in SecretRows)
            vals[k] = sr.GetValue();
        return vals;
    }

    private (Dictionary<string, object?> Vals, List<string> Errors) Collect()
    {
        var vals = new Dictionary<string, object?>(StringComparer.Ordinal);
        var errors = new List<string>();
        foreach (var (k, fr) in Rows)
        {
            try
            {
                vals[k] = fr.GetValue();
                fr.Mark(false);
            }
            catch (ConfigValueException e)
            {
                errors.Add(L.T(e.Message));
                fr.Mark(true);
            }
        }
        return (vals, errors);
    }

    public bool IsDirty()
    {
        var (vals, errors) = Collect();
        if (errors.Count > 0)
            return true;
        foreach (var (k, v) in vals)
        {
            var cur = _app.Cfg[k];
            if (Rows[k].Kind is "langs" or "parts") // o tick: thu tu do chuong trinh quyet
            {
                var a = (cur as IEnumerable<string> ?? []).Order(StringComparer.Ordinal).ToList();
                var b = (v as IEnumerable<string> ?? []).Order(StringComparer.Ordinal).ToList();
                if (!a.SequenceEqual(b))
                    return true;
            }
            else if (!SettingsUi.Same(cur, v))
            {
                return true;
            }
        }
        return SecretRows.Values.Any(sr => sr.GetValue() != sr.Saved);
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Save();

    public bool Save()
    {
        var (vals, errors) = Collect();
        if (errors.Count > 0)
        {
            Dlg.Warn(this, "Chưa lưu được", L.T("Sửa các ô sau rồi lưu lại:") + "\n\n• " + string.Join("\n• ", errors));
            return false;
        }
        var cfg = _app.Cfg;
        foreach (var (k, v) in vals)
            cfg[k] = v;
        foreach (var c in cfg.List("Languages"))
            Naming.RegisterLanguage(c);
        try
        {
            ConfigSchema.SaveConfig(cfg);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dlg.Warn(this, "Không lưu được", L.T("Không ghi được file cấu hình: ") + ex.Message);
            return false;
        }
        var nSec = 0;
        foreach (var (k, sr) in SecretRows)
        {
            var v = sr.GetValue();
            if (v == sr.Saved)
                continue;
            try
            {
                _app.SecretStore.Set(k, v);
                sr.Saved = v;
                nSec++;
            }
            catch (SecretStoreException ex)
            {
                Dlg.Warn(this, "Không lưu được bí mật", ex.Message);
                return false;
            }
        }
        _app.OnConfigChanged(secretsChanged: nSec > 0);
        Msg.Text = L.T("✓ Đã lưu" + (nSec > 0 ? string.Create(CultureInfo.InvariantCulture, $" (cả {nSec} bí mật)") : ""));
        Msg.SetResourceReference(TextBlock.ForegroundProperty, "LogOkBrush");
        _app.Log("ok", $"Đã lưu cài đặt: {ConfigSchema.ConfigPath()}" + (nSec > 0 ? string.Create(CultureInfo.InvariantCulture, $" + {nSec} bí mật vào Credential Manager") : ""));
        return true;
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => Reload();

    public void Reload()
    {
        foreach (var (k, fr) in Rows)
            fr.SetValue(_app.Cfg[k]);
        var vals = _app.Secrets();
        foreach (var (k, sr) in SecretRows)
            sr.SetValue(vals.GetValueOrDefault(k, ""));
        Msg.Text = "";
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (!Dlg.AskYesNo(this, "Khôi phục mặc định", "Đưa MỌI cài đặt về giá trị mặc định?\n\n(API key / tài khoản trong Credential Manager KHÔNG bị xoá.)"))
            return;
        _app.Cfg.ResetDefaults();
        ConfigSchema.SaveConfig(_app.Cfg);
        Reload();
        _app.OnConfigChanged();
        _app.Log("ok", "Đã khôi phục cài đặt mặc định.");
    }

    private void OpenConfigDir_Click(object sender, RoutedEventArgs e)
    {
        if (!Shell.Open(ConfigSchema.AppConfigDir()))
            Dlg.Warn(this, "Cấu hình", "Chưa có thư mục cấu hình (chưa lưu lần nào).");
    }

    // ---- cong cu ----
    private Dictionary<string, FieldRow> ToolRows() =>
        Rows.Where(kv => SettingsUi.ToolByKey.ContainsKey(kv.Key)).ToDictionary(kv => SettingsUi.ToolByKey[kv.Key], kv => kv.Value, StringComparer.Ordinal);

    private static void ShowInfo(FieldRow fr, ToolInfo info, bool auto)
    {
        fr.CheckedPath = fr.EntryText;
        fr.SetStatus(SettingsUi.ToolStatusText(info, auto), SettingsUi.ToolStatusLevel(info));
    }

    /// <summary>Kiểm tra lại MỘT công cụ (sau Chọn… / Tự tìm / gõ đường dẫn).</summary>
    public void ShowToolStatus(FieldRow fr, bool onlyIfChanged = false)
    {
        if (!SettingsUi.ToolByKey.TryGetValue(fr.F.Key, out var name))
            return;
        var p = fr.EntryText;
        if (onlyIfChanged && fr.CheckedPath == p)
            return;
        var cfg = CurrentCfg();
        var folder = _app.Folder;
        _app.Runner.Start($"Kiểm tra {name}", job =>
        {
            var path = p.Length > 0 ? p : Tools.FindAllTools(cfg, folder)[name];
            return (Tools.GetToolInfo(name, path, job), p.Length == 0);
        }, res => ShowInfo(fr, res.Item1, res.Item2), whenFree: true);
    }

    /// <summary>Nút "Kiểm tra lại": file nào đang được dùng cho từng công cụ + phiên bản. Lúc mở chương trình: báo công cụ còn thiếu (và việc gì sẽ
    /// không làm được) trong khung log.</summary>
    public void CheckTools(bool startup = false)
    {
        var cfg = CurrentCfg();
        var folder = _app.Folder;
        var rows = ToolRows();
        _app.Runner.Start("Kiểm tra công cụ", job =>
        {
            var found = Tools.FindAllTools(cfg, folder);
            var outp = new Dictionary<string, (ToolInfo Info, bool Auto)>(StringComparer.Ordinal);
            foreach (var n in Tools.ToolNames)
                outp[n] = (Tools.GetToolInfo(n, found[n], job), cfg.Str(Tools.ConfigPathKey[n]).Trim().Length == 0);
            return outp;
        }, res =>
        {
            foreach (var (n, (info, auto)) in res)
            {
                if (rows.TryGetValue(n, out var fr))
                    ShowInfo(fr, info, auto);
            }
            var lines = SettingsUi.ToolSummary(res);
            foreach (var (lvl, msg) in lines)
            {
                if (!startup || lvl != "ok")
                    _app.Log(lvl, msg);
            }
            if (startup && lines.All(x => x.Level == "ok"))
                _app.Log("ok", lines[0].Message);
        }, whenFree: true);
    }

    // ---- kiem tra ket noi ----
    public void Check(string which)
    {
        var cfg = CurrentCfg();
        var vals = CurrentSecrets();
        var row = SecretRows.Values.FirstOrDefault(r => r.CheckKind == which);
        row?.SetStatus("đang kiểm tra…", "LogDimBrush");
        _app.Redactor.Update([.. _app.Secrets().Values, .. vals.Values]);
        _app.Runner.Start($"Kiểm tra kết nối {SettingsUi.CheckNames.GetValueOrDefault(which, which)}",
                          job => SettingsUi.CheckConnection(which, vals, cfg, job), st =>
        {
            row?.SetStatus(st.Text, StatusBrush(st));
            _app.Log(st.Ok == true ? "ok" : st.Ok is null ? "dim" : "error", st.Text);
        });
    }

    // ---- nhap tu .bat ----
    private void ImportBat_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = L.T("Chọn SubFetch / SubPrep / MuxTool .bat"), Multiselect = true,
            Filter = L.T("File .bat") + " (*.bat)|*.bat|" + L.T("Mọi file") + " (*.*)|*.*",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true || dlg.FileNames.Length == 0)
            return;
        var imp = BatImporter.ImportFromBat(dlg.FileNames);
        var store = _app.SecretStore;
        string CurSecret(string k)
        {
            try
            {
                return store.Get(k);
            }
            catch (SecretStoreException)
            {
                return "";
            }
        }
        var rows = SettingsUi.ImportRows(imp, _app.Cfg, CurSecret);
        if (rows.Count == 0)
        {
            var msg = L.T("Không đọc được khoá cấu hình nào.");
            if (imp.Problems.Count > 0)
                msg += "\n\n" + string.Join("\n", imp.Problems.Take(10).Select(p => $"• {p.File}: {L.T(p.Reason)}"));
            Dlg.Warn(this, "Nhập từ .bat", msg);
            return;
        }
        ImportDialog.Run(this, imp, rows);
    }
}

/// <summary>Một ô cấu hình trên màn hình (nhãn + ô nhập + chú thích) — port <c>FieldRow</c>.</summary>
public sealed class FieldRow
{
    private readonly SettingsView _view;
    private readonly Dictionary<string, CheckBox> _vars = new(StringComparer.Ordinal);
    private readonly TextBlock _label;
    private readonly CheckBox? _bool;
    private readonly ComboBox? _choice;
    private readonly System.Windows.Controls.TextBox? _entry;
    private readonly TextBlock? _status;

    public FieldRow(SettingsView view, Field f)
    {
        _view = view;
        F = f;
        Kind = SettingsUi.FieldKind(f);
        var g = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Root = g;
        var labTxt = L.T(f.Label) + (f.Advanced ? L.T("  (nâng cao)") : "");
        var box = new StackPanel();
        Grid.SetColumn(box, 1);
        if (Kind == "bool")
        {
            _label = new TextBlock { Text = labTxt, TextWrapping = TextWrapping.Wrap };
            _bool = new CheckBox { Content = _label };
            Grid.SetColumnSpan(_bool, 2);
            var col = new StackPanel();
            Grid.SetColumnSpan(col, 2);
            col.Children.Add(_bool);
            g.Children.Add(col);
            box = col;
        }
        else
        {
            _label = new TextBlock { Text = labTxt, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 12, 0), VerticalAlignment = VerticalAlignment.Top };
            g.Children.Add(_label);
            g.Children.Add(box);
            switch (Kind)
            {
                case "langs":
                {
                    var sw = new WrapPanel();
                    foreach (var c in Naming.FetchLangSwitches)
                    {
                        var cb = new CheckBox { Content = c, MinWidth = 0, Margin = new Thickness(0, 0, 12, 4) };
                        _vars[c] = cb;
                        sw.Children.Add(cb);
                    }
                    box.Children.Add(sw);
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
                    row.Children.Add(new TextBlock { Text = L.T("Mã khác:"), VerticalAlignment = VerticalAlignment.Center });
                    _entry = new UiTextBox { Width = 240, Margin = new Thickness(6, 0, 0, 0), PlaceholderText = L.T("vd: pt ru it") };
                    row.Children.Add(_entry);
                    box.Children.Add(row);
                    break;
                }
                case "parts":
                {
                    var wp = new WrapPanel();
                    foreach (var (c, lab) in SettingsUi.CodecParts)
                    {
                        var cb = new CheckBox { Content = L.T(lab), Margin = new Thickness(0, 0, 12, 0) };
                        _vars[c] = cb;
                        wp.Children.Add(cb);
                    }
                    box.Children.Add(wp);
                    break;
                }
                case "choice":
                    _choice = new ComboBox { Width = 180, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = f.ChoiceList };
                    box.Children.Add(_choice);
                    break;
                case "dict":
                case "list" when SettingsUi.LineLists.Contains(f.Key):
                    _entry = new System.Windows.Controls.TextBox
                    {
                        AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, MinHeight = Kind == "list" ? 72 : 90, MaxHeight = 200,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                        FontFamily = (System.Windows.Media.FontFamily)view.FindResource("MonoFont"),
                    };
                    box.Children.Add(_entry);
                    box.Children.Add(Dim(Kind == "list" ? "mỗi dòng một thư mục" : "mỗi dòng: khoá = số ms (vd S01E03 = 1200, tap #643 = -500)", 11));
                    break;
                case "file":
                {
                    var line = new DockPanel();
                    var pick = new Button { Content = L.T("Chọn…"), Margin = new Thickness(6, 0, 0, 0) };
                    var auto = new Button { Content = L.T("Tự tìm"), Margin = new Thickness(6, 0, 0, 0) };
                    DockPanel.SetDock(auto, Dock.Right);
                    DockPanel.SetDock(pick, Dock.Right);
                    line.Children.Add(auto);
                    line.Children.Add(pick);
                    _entry = new UiTextBox { PlaceholderText = L.T("để trống = tự tìm"), FontFamily = (System.Windows.Media.FontFamily)view.FindResource("MonoFont") };
                    line.Children.Add(_entry);
                    box.Children.Add(line);
                    // file dang dung + phien ban (ca khi de trong cho tu tim)
                    _status = Dim("đang kiểm tra…", 12);
                    box.Children.Add(_status);
                    pick.Click += (_, _) => PickFile();
                    auto.Click += (_, _) => AutoFile();
                    _entry.KeyDown += (_, e) =>
                    {
                        if (e.Key == System.Windows.Input.Key.Enter)
                            _view.ShowToolStatus(this);
                    };
                    _entry.LostFocus += (_, _) => _view.ShowToolStatus(this, onlyIfChanged: true);
                    break;
                }
                default:
                {
                    var line = new DockPanel();
                    if (Kind == "dir")
                    {
                        if (SettingsUi.DirFinders.ContainsKey(f.Key))
                        {
                            var auto = new Button { Content = L.T("Tự tìm"), Margin = new Thickness(6, 0, 0, 0) };
                            DockPanel.SetDock(auto, Dock.Right);
                            auto.Click += (_, _) => AutoDir();
                            line.Children.Add(auto);
                        }
                        var pick = new Button { Content = L.T("Chọn…"), Margin = new Thickness(6, 0, 0, 0) };
                        DockPanel.SetDock(pick, Dock.Right);
                        pick.Click += (_, _) => PickDir();
                        line.Children.Add(pick);
                    }
                    var num = Kind is "int" or "float";
                    _entry = new UiTextBox
                    {
                        Width = num ? 140 : double.NaN, HorizontalAlignment = num ? HorizontalAlignment.Left : HorizontalAlignment.Stretch,
                        PlaceholderText = Kind == "list" ? L.T("cách nhau bằng dấu cách") : "",
                    };
                    line.Children.Add(_entry);
                    box.Children.Add(line);
                    break;
                }
            }
        }
        if (f.Help.Length > 0)
        {
            var h = Dim(f.Help, 12);
            h.Margin = new Thickness(Kind == "bool" ? 28 : 0, 2, 0, 0);
            box.Children.Add(h);
        }
    }

    public Field F { get; }

    public string Kind { get; }

    public FrameworkElement Root { get; }

    /// <summary>Đường dẫn đã kiểm tra lần cuối (<c>_checked</c>).</summary>
    public string? CheckedPath { get; set; }

    public string EntryText => _entry?.Text.Trim() ?? "";

    private static TextBlock Dim(string text, double size)
    {
        var t = new TextBlock { Text = L.T(text), TextWrapping = TextWrapping.Wrap, FontSize = size };
        t.SetResourceReference(TextBlock.ForegroundProperty, "LogDimBrush");
        return t;
    }

    public void SetStatus(string text, string level)
    {
        if (_status is null)
            return;
        _status.Text = L.T(text);
        _status.SetResourceReference(TextBlock.ForegroundProperty, Dlg.LevelBrush(level));
    }

    // ---- doc / ghi ----
    public void SetValue(object? v)
    {
        switch (Kind)
        {
            case "bool":
                _bool!.IsChecked = v is true;
                break;
            case "langs":
                var (sw, extra) = SettingsUi.LangsToParts(v is IEnumerable<string> l ? [.. l] : []);
                foreach (var (c, cb) in _vars)
                    cb.IsChecked = sw.GetValueOrDefault(c);
                _entry!.Text = extra;
                break;
            case "parts":
                var parts = v as IEnumerable<string> ?? [];
                foreach (var (c, cb) in _vars)
                    cb.IsChecked = parts.Contains(c);
                break;
            case "choice":
                _choice!.SelectedItem = F.ChoiceList.FirstOrDefault(x => x == (v as string ?? ""));
                break;
            default:
                _entry!.Text = SettingsUi.ValueToText(F, v);
                break;
        }
        Mark(false);
    }

    /// <summary>Giá trị đúng kiểu; sai → <see cref="ConfigValueException"/>.</summary>
    public object? GetValue() => Kind switch
    {
        "bool" => _bool!.IsChecked == true,
        "langs" => SettingsUi.LangsFromParts(_vars.ToDictionary(kv => kv.Key, kv => kv.Value.IsChecked == true, StringComparer.Ordinal), _entry!.Text),
        "parts" => SettingsUi.CodecParts.Where(c => _vars[c.Key].IsChecked == true).Select(c => c.Key).ToList(),
        "choice" => ConfigSchema.Coerce(F.Key, _choice!.SelectedItem as string ?? Convert.ToString(F.Default, CultureInfo.InvariantCulture) ?? ""),
        _ => SettingsUi.TextToValue(F, _entry!.Text.Replace("\r\n", "\n", StringComparison.Ordinal)),
    };

    public void Mark(bool bad)
    {
        if (bad)
            _label.SetResourceReference(TextBlock.ForegroundProperty, "LogErrorBrush");
        else
            _label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
    }

    public void SetVisible(bool on) => Root.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

    // ---- nut ----
    private void PickDir()
    {
        var cur = EntryText;
        var dlg = new OpenFolderDialog { Title = L.T(F.Label) };
        if (cur.Length > 0 && Directory.Exists(cur))
            dlg.InitialDirectory = cur;
        if (dlg.ShowDialog(Window.GetWindow(_view)) == true)
            _entry!.Text = Path.GetFullPath(dlg.FolderName);
    }

    private void PickFile()
    {
        var cur = EntryText;
        var dlg = new OpenFileDialog { Title = L.T(F.Label), Filter = L.T("Chương trình") + " (*.exe)|*.exe|" + L.T("Mọi file") + " (*.*)|*.*" };
        if (cur.Length > 0 && Path.GetDirectoryName(cur) is { Length: > 0 } d && Directory.Exists(d))
            dlg.InitialDirectory = d;
        if (dlg.ShowDialog(Window.GetWindow(_view)) != true)
            return;
        _entry!.Text = Path.GetFullPath(dlg.FileName);
        _view.ShowToolStatus(this);
    }

    private void AutoFile()
    {
        var name = SettingsUi.ToolByKey[F.Key];
        var p = Tools.FindTool(name, _view.CurrentCfg(), _view.App.Folder, useConfigPath: false);
        if (p is null)
        {
            SetStatus($"✗ không tìm thấy {Tools.ExeName(name)} ở các thư mục quen thuộc — bấm \"Chọn…\" để chỉ tới file", "error");
            return;
        }
        _entry!.Text = p;
        _view.ShowToolStatus(this);
    }

    private void AutoDir()
    {
        var name = SettingsUi.DirFinders[F.Key];
        var cfg = _view.CurrentCfg();
        cfg[F.Key] = "";
        var p = Tools.FindTool(name, cfg, _view.App.Folder, useConfigPath: false);
        if (p is not null)
            _entry!.Text = Path.GetDirectoryName(p) ?? "";
        else
            Dlg.Warn(_view, "Tự tìm", $"Không tìm thấy {Tools.ExeName(name)}.");
    }
}

/// <summary>Một bí mật (ô ••• + "Hiện" + "Kiểm tra kết nối") — port <c>SecretRow</c>.</summary>
public sealed class SecretRow
{
    private readonly PasswordBox _hidden = new();
    private readonly Wpf.Ui.Controls.TextBox _shown = new() { Visibility = Visibility.Collapsed };
    private readonly CheckBox _show;
    private readonly TextBlock? _status;

    public SecretRow(SettingsView view, string key)
    {
        Key = key;
        var (label, help, check) = SettingsUi.SecretInfo[key];
        CheckKind = check;
        var g = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Root = g;
        var lab = new TextBlock { Text = "🔒 " + L.T(label), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        lab.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        g.Children.Add(lab);
        var col = new StackPanel();
        Grid.SetColumn(col, 1);
        g.Children.Add(col);
        var line = new WrapPanel();
        var entry = new Grid { Width = 380 };
        _hidden.MinHeight = 32;
        _hidden.VerticalContentAlignment = VerticalAlignment.Center;
        entry.Children.Add(_hidden);
        entry.Children.Add(_shown);
        line.Children.Add(entry);
        _show = new CheckBox { Content = L.T("Hiện"), Margin = new Thickness(8, 0, 8, 0), MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        _show.Click += (_, _) => Toggle();
        line.Children.Add(_show);
        if (check is not null)
        {
            var b = new Button { Content = L.T("Kiểm tra kết nối"), VerticalAlignment = VerticalAlignment.Center };
            b.Click += (_, _) => view.Check(check);
            line.Children.Add(b);
            _status = new TextBlock { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };
            line.Children.Add(_status);
        }
        col.Children.Add(line);
        if (help.Length > 0)
        {
            var h = new TextBlock { Text = L.T(help), TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
            h.SetResourceReference(TextBlock.ForegroundProperty, "LogDimBrush");
            col.Children.Add(h);
        }
    }

    public string Key { get; }

    public string? CheckKind { get; }

    public FrameworkElement Root { get; }

    public string Saved { get; set; } = "";

    private void Toggle()
    {
        if (_show.IsChecked == true)
        {
            _shown.Text = _hidden.Password;
            _shown.Visibility = Visibility.Visible;
            _hidden.Visibility = Visibility.Collapsed;
        }
        else
        {
            _hidden.Password = _shown.Text;
            _hidden.Visibility = Visibility.Visible;
            _shown.Visibility = Visibility.Collapsed;
        }
    }

    public void SetValue(string v)
    {
        Saved = v ?? "";
        _hidden.Password = Saved;
        _shown.Text = Saved;
    }

    public string GetValue() => (_show.IsChecked == true ? _shown.Text : _hidden.Password).Trim();

    public void SetStatus(string text, string brush)
    {
        if (_status is null)
            return;
        _status.Text = L.T(text);
        _status.SetResourceReference(TextBlock.ForegroundProperty, brush);
    }
}

/// <summary>Bảng xem trước "Nhập cấu hình từ file .bat" (port <c>ImportDialog</c>).</summary>
public static class ImportDialog
{
    public static void Run(SettingsView view, BatImport imp, List<ImportRow> rows)
    {
        var d = new Dlg(view, "Nhập cấu hình từ file .bat", 1100, 640);
        var kinds = new Dictionary<string, string>(StringComparer.Ordinal) { ["subfetch"] = "SubFetch", ["subprep"] = "SubPrep", ["muxtool"] = "MuxTool" };
        var files = string.Join(", ", imp.Files.Select(f => $"{Path.GetFileName(f.Path)} → {(f.Kind is not null && kinds.TryGetValue(f.Kind, out var k) ? k : L.T("không nhận ra"))}"));
        d.Text($"Đã đọc: {files}\nTick những khoá muốn nhập. Khoá có hai giá trị khác nhau: chọn một (hoặc bỏ cả hai). Bí mật được ghi thẳng vào Windows Credential Manager.");
        var notes = new List<string>();
        if (imp.Ignored.Count > 0)
            notes.Add(L.T("Bỏ qua (thuộc console / được thay bằng nút): ") + string.Join(", ", imp.Ignored.Select(x => x.Name).Distinct().Order(StringComparer.Ordinal)));
        if (imp.Problems.Count > 0)
            notes.Add(L.T("Không đọc được: ") + string.Join("; ", imp.Problems.Take(8).Select(p =>
                string.Create(CultureInfo.InvariantCulture, $"{p.File} {p.Name} (dòng {p.Line}): {L.T(p.Reason)}"))));
        if (notes.Count > 0)
        {
            var nt = new TextBlock { Text = string.Join("\n", notes), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            nt.SetResourceReference(TextBlock.ForegroundProperty, "LogDimBrush");
            DockPanel.SetDock(nt, Dock.Bottom);
            d.Body.Children.Add(nt);
        }
        var g = d.Fill(new DataGrid());
        g.SetResourceReference(FrameworkElement.StyleProperty, "WbGrid");
        var t = new Table(g,
        [
            new Col("label", "Khoá", 260), new Col("current", "Giá trị hiện tại", 270, Name: true), new Col("new", "Giá trị từ .bat", 300, Stretch: true, Name: true),
            new Col("source", "Nguồn (file:dòng)", 170, Name: true),
        ], check: "Nhập", single: true);
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var iid = "r" + i.ToString(CultureInfo.InvariantCulture);
            var tag = r.Group.Length > 0 ? "need" : r.Same ? "dim" : "";
            if (r.Group.Length > 0)
            {
                var gk = (r.Secret ? "S|" : "V|") + r.Group;
                if (!groups.TryGetValue(gk, out var l))
                    groups[gk] = l = [];
                l.Add(iid);
            }
            t.Add(iid, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["label"] = L.T(r.Label) + (r.Group.Length > 0 ? L.T(" (chọn một)") : ""), ["current"] = r.Current,
                ["new"] = r.New + (r.Same ? L.T("  (giống hiện tại)") : ""), ["source"] = r.Source,
            }, tag, r.Group.Length == 0);
        }
        t.OnToggle = (iid, state) =>
        {
            if (!state)
                return;
            foreach (var ids in groups.Values.Where(x => x.Contains(iid)))
            {
                foreach (var other in ids.Where(o => o != iid))
                    t.SetChecked(other, false);
            }
        };
        d.Button("Nhập", () =>
        {
            var keepV = new HashSet<string>(StringComparer.Ordinal);
            var keepS = new HashSet<string>(StringComparer.Ordinal);
            var res = new Dictionary<string, object?>(StringComparer.Ordinal);
            var chk = t.Checked;
            for (var i = 0; i < rows.Count; i++)
            {
                if (!chk.GetValueOrDefault("r" + i.ToString(CultureInfo.InvariantCulture)))
                    continue;
                var r = rows[i];
                if (r.Group.Length > 0)
                    res[r.Key] = r.Value;
                else if (r.Secret)
                    keepS.Add(r.Key);
                else
                    keepV.Add(r.Key);
            }
            var app = view.App;
            List<string> done;
            try
            {
                done = BatImporter.ApplyBatImport(app.Cfg, SettingsUi.FilteredImport(imp, keepV, keepS), res, app.SecretStore);
                ConfigSchema.SaveConfig(app.Cfg);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecretStoreException or ConfigValueException)
            {
                Dlg.Warn(d.Win, "Không nhập được", e.Message);
                return;
            }
            d.Close(true);
            view.Reload();
            app.OnConfigChanged(secretsChanged: true);
            app.Log("ok", string.Create(CultureInfo.InvariantCulture, $"Đã nhập {done.Count} khoá từ .bat (bí mật đã vào Credential Manager)."));
        }, true);
        d.Button("Huỷ", () => d.Close(null));
        d.Run();
    }
}
