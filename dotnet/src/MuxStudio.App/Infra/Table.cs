using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace MuxStudio.App;

/// <summary>Một cột (port <c>W.Col</c>): <c>Align</c> = <c>w</c> (trái) | <c>e</c> (phải); <c>Stretch</c> = giãn theo bề ngang;
/// <c>Name</c> = cột tên file (<c>fit="name"</c>: KHÔNG dịch); <c>Badge</c> = vẽ ô thành NHÃN màu theo giá trị ẩn <c>"~" + Key</c> của dòng
/// (loại màu của <see cref="Chips"/>; rỗng = chữ thường).</summary>
public sealed record Col(string Key, string Title, double Width, string Align = "w", bool Stretch = false, bool Name = false, bool Badge = false);

/// <summary>Một dòng của <see cref="Table"/>: giá trị theo khoá cột (<c>row["src"]</c>), ô tick (<c>null</c> = không có ô tick), nhãn màu.</summary>
public sealed class TRow(string id) : INotifyPropertyChanged
{
    private readonly Dictionary<string, string> _v = new(StringComparer.Ordinal);
    private bool? _checked;

    /// <summary>Giá trị gốc (chưa dịch) — <c>full(iid)</c> của bản Python.</summary>
    public Dictionary<string, string> Raw { get; } = new(StringComparer.Ordinal);
    private string _tag = "";
    private string _text = "";
    private Thickness _indent;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; } = id;

    public string? Parent { get; init; }

    public string this[string key]
    {
        get => _v.GetValueOrDefault(key, "");
        set
        {
            if (_v.TryGetValue(key, out var old) && old == value)
                return;
            _v[key] = value;
            Raise("Item[]");
        }
    }

    public bool? Checked
    {
        get => _checked;
        set
        {
            if (_checked == value)
                return;
            _checked = value;
            Raise();
            Raise(nameof(HasCheck));
        }
    }

    public bool HasCheck => _checked is not null;

    public string Tag
    {
        get => _tag;
        set
        {
            if (_tag == value)
                return;
            _tag = value;
            Raise();
        }
    }

    /// <summary>Chữ ở cột cây (cột đầu, khi bảng có <c>tree</c>).</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (_text == value)
                return;
            _text = value;
            Raise();
        }
    }

    public Thickness Indent
    {
        get => _indent;
        set
        {
            _indent = value;
            Raise();
        }
    }

    public void SetAll(IEnumerable<KeyValuePair<string, string>> vals)
    {
        foreach (var (k, v) in vals)
            _v[k] = v;
        Raise("Item[]");
        Raise(nameof(Raw)); // nhan mau (Raw[~khoa]) cap nhat theo
    }

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Bảng (port <c>W.Table</c> của bản Python) bọc một <see cref="DataGrid"/>: cột dựng theo <see cref="Col"/>, ô tick (<c>check</c> = tiêu đề
/// cột tick; bấm tiêu đề = tick / bỏ tất cả), cột cây (<c>tree</c>), dòng tô màu theo <see cref="TRow.Tag"/>, tooltip từng ô
/// (<see cref="Tip"/>), bấm đúp (<see cref="OnDouble"/>), chọn dòng (<see cref="OnSelect"/>). Chữ tiêu đề được dịch.
/// </summary>
public sealed class Table
{
    private readonly Dictionary<string, TRow> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<DataGridColumn, string> _colKey = [];
    private readonly Dictionary<string, Col> _cols = new(StringComparer.Ordinal);
    private readonly CheckBox? _headCheck;
    private bool _suppressSelect;

    public Table(DataGrid grid, IReadOnlyList<Col> cols, string? check = null, string? tree = null, double treeWidth = 0, bool single = false)
    {
        Grid = grid;
        Cols = cols;
        grid.Columns.Clear();
        grid.ItemsSource = Rows;
        grid.SelectionMode = single ? DataGridSelectionMode.Single : DataGridSelectionMode.Extended;
        if (check is not null)
        {
            _headCheck = new CheckBox { IsThreeState = false, Content = new TextBlock { Text = L.T(check), FontWeight = FontWeights.SemiBold }, MinWidth = 0, Padding = new Thickness(4, 0, 0, 0) };
            _headCheck.Click += (_, _) => ToggleAll();
            var f = new FrameworkElementFactory(typeof(CheckBox));
            f.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(TRow.Checked)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            f.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(TRow.HasCheck)) { Converter = new BooleanToVisibilityConverter() });
            f.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            f.SetValue(FrameworkElement.MinWidthProperty, 0.0);
            f.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 0, 0));
            f.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((s, _) =>
            {
                if (s is FrameworkElement { DataContext: TRow r } && r.Checked is { } st)
                    OnToggle?.Invoke(r.Id, st);
            }));
            var c = new DataGridTemplateColumn
            {
                Header = _headCheck, Width = new DataGridLength(Math.Max(84, L.T(check).Length * 9 + 60)), CellTemplate = new DataTemplate { VisualTree = f },
                CanUserResize = false,
            };
            grid.Columns.Add(c);
        }
        if (tree is not null)
        {
            var f = new FrameworkElementFactory(typeof(TextBlock));
            f.SetValue(FrameworkElement.StyleProperty, TagText(new Style(typeof(TextBlock), grid.TryFindResource("DefaultDataGridTextElementStyle") as Style)));
            f.SetBinding(TextBlock.TextProperty, new Binding(nameof(TRow.Text)));
            f.SetBinding(FrameworkElement.MarginProperty, new Binding(nameof(TRow.Indent)));
            f.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            f.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            var c = new DataGridTemplateColumn { Header = L.T(tree), Width = new DataGridLength(Math.Max(treeWidth, 40)), CellTemplate = new DataTemplate { VisualTree = f } };
            _colKey[c] = "#0";
            grid.Columns.Add(c);
        }
        foreach (var col in cols)
        {
            _cols[col.Key] = col;
            var style = new Style(typeof(TextBlock), grid.TryFindResource("DefaultDataGridTextElementStyle") as Style);
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(4, 0, 4, 0)));
            if (col.Align == "e")
                style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
            TagText(style);
            var width = col.Stretch ? new DataGridLength(Math.Max(col.Width, 40) / 100.0, DataGridLengthUnitType.Star) : new DataGridLength(col.Width);
            DataGridColumn c;
            if (col.Badge)
            {
                // NHAN mau: Border (nen nhat + vien theo Raw[~khoa]) chua chu cua o
                var bd = new FrameworkElementFactory(typeof(Border));
                bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
                bd.SetValue(Border.PaddingProperty, new Thickness(6, 1, 6, 1));
                bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
                bd.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 0, 4, 0));
                bd.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                bd.SetValue(FrameworkElement.HorizontalAlignmentProperty, col.Align == "e" ? HorizontalAlignment.Right : HorizontalAlignment.Left);
                bd.SetBinding(Border.BackgroundProperty, new Binding($"Raw[~{col.Key}]") { Converter = KindBrushConverter.Soft, FallbackValue = Brushes.Transparent });
                bd.SetBinding(Border.BorderBrushProperty, new Binding($"Raw[~{col.Key}]") { Converter = KindBrushConverter.Soft, FallbackValue = Brushes.Transparent });
                // mau chu theo loai: trigger (khong binding) — loai rong thi giu mau theo nhan dong / che do Sang-Toi
                var bstyle = new Style(typeof(TextBlock), style);
                foreach (var kind in Chips.KindNames)
                {
                    var dt = new DataTrigger { Binding = new Binding($"Raw[~{col.Key}]"), Value = kind };
                    // tham chieu dong: chu dam o che do Sang, sang o che do Toi (doc ro tren nen Acrylic), tu doi khi doi che do
                    dt.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension(Chips.TextKey(kind))));
                    bstyle.Triggers.Add(dt);
                }
                var tb = new FrameworkElementFactory(typeof(TextBlock));
                tb.SetValue(FrameworkElement.StyleProperty, bstyle);
                tb.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
                tb.SetBinding(TextBlock.TextProperty, new Binding($"[{col.Key}]"));
                bd.AppendChild(tb);
                c = new DataGridTemplateColumn { Header = L.T(col.Title), CellTemplate = new DataTemplate { VisualTree = bd }, Width = width };
            }
            else
            {
                c = new DataGridTextColumn { Header = L.T(col.Title), Binding = new Binding($"[{col.Key}]"), ElementStyle = style, Width = width };
            }
            c.MinWidth = col.Stretch ? Math.Min(col.Width, 120) : 30;
            _colKey[c] = col.Key;
            grid.Columns.Add(c);
        }
        var cellStyle = new Style(typeof(DataGridCell), grid.TryFindResource("WbCell") as Style);
        cellStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, ""));
        cellStyle.Setters.Add(new EventSetter(FrameworkElement.ToolTipOpeningEvent, new ToolTipEventHandler(CellTipOpening)));
        grid.CellStyle = cellStyle;
        grid.MouseDoubleClick += GridDoubleClick;
        grid.SelectionChanged += (_, _) =>
        {
            if (!_suppressSelect)
                OnSelect?.Invoke(Selected());
        };
    }

    /// <summary>Chữ trong ô: cỡ 12 (gọn), màu / đậm theo nhãn của dòng (dim = xám, ok = xanh, head = đậm xanh dương).</summary>
    private static Style TagText(Style style)
    {
        style.Setters.Add(new Setter(TextBlock.FontSizeProperty, 12.5));
        foreach (var (tag, key, bold) in new[] { ("dim", "LogDimBrush", false), ("ok", "LogOkBrush", false), ("head", "LogHeadBrush", true) })
        {
            var dt = new DataTrigger { Binding = new Binding(nameof(TRow.Tag)), Value = tag };
            dt.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension(key)));
            if (bold)
                dt.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));
            style.Triggers.Add(dt);
        }
        return style;
    }

    public DataGrid Grid { get; }

    public IReadOnlyList<Col> Cols { get; }

    public ObservableCollection<TRow> Rows { get; } = [];

    public Action<string, bool>? OnToggle { get; set; }

    public Action<bool>? OnToggleAll { get; set; }

    /// <summary>(id dòng, khoá cột) — cột cây là <c>#0</c>.</summary>
    public Action<string, string>? OnDouble { get; set; }

    public Action<List<string>>? OnSelect { get; set; }

    /// <summary>(id dòng, khoá cột) → chữ tooltip; <c>null</c> = hiện nguyên giá trị ô nếu bị cắt (không có thì thôi).</summary>
    public Func<string, string, string?>? Tip { get; set; }

    public TRow Add(string id, IEnumerable<KeyValuePair<string, string>> vals, string tag = "", bool? check = null, string text = "",
                    string? parent = null)
    {
        var r = new TRow(id) { Parent = parent };
        Put(r, vals);
        r.Tag = tag;
        r.Checked = check;
        r.Text = L.T(text);
        r.Indent = new Thickness(parent is null ? 4 : 22, 0, 4, 0);
        _byId[id] = r;
        if (parent is not null && _byId.ContainsKey(parent))
        {
            // chen sau dong con cuoi cung cua dong cha
            var idx = Rows.IndexOf(_byId[parent]);
            var j = idx + 1;
            while (j < Rows.Count && Rows[j].Parent == parent)
                j++;
            Rows.Insert(j, r);
        }
        else
        {
            Rows.Add(r);
        }
        return r;
    }

    public TRow? Row(string id) => _byId.GetValueOrDefault(id);

    /// <summary>Thêm cột NÚT thao tác ở cuối bảng: chữ nút = giá trị <c>act</c> của dòng (rỗng = không có nút), kiểu nút = <c>~act</c>
    /// (<c>good</c> = xanh lá, <c>primary</c>, còn lại xám). Bấm → <paramref name="click"/>(id dòng).</summary>
    public void AddActionColumn(string title, double width, Action<string> click)
    {
        var b = new FrameworkElementFactory(typeof(Wpf.Ui.Controls.Button));
        b.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 1, 6, 1));
        b.SetValue(Control.PaddingProperty, new Thickness(10, 2, 10, 2));
        b.SetValue(Control.FontSizeProperty, 12.0);
        b.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        b.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        b.SetBinding(ContentControl.ContentProperty, new Binding("[act]"));
        b.SetBinding(UIElement.VisibilityProperty, new Binding("[act]") { Converter = EmptyToCollapsedConverter.Instance });
        b.SetBinding(Wpf.Ui.Controls.Button.AppearanceProperty, new Binding("Raw[~act]") { Converter = KindAppearanceConverter.Instance });
        b.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is FrameworkElement { DataContext: TRow r })
                click(r.Id);
        }));
        var c = new DataGridTemplateColumn
        {
            Header = L.T(title), Width = new DataGridLength(width), CellTemplate = new DataTemplate { VisualTree = b }, CanUserResize = false,
        };
        _colKey[c] = "act";
        Grid.Columns.Add(c);
    }

    /// <summary>Chữ hiển thị: dịch theo ngôn ngữ giao diện, trừ cột tên file.</summary>
    private string Disp(string key, string v) => _cols.TryGetValue(key, out var c) && c.Name ? v : L.T(v);

    private void Put(TRow r, IEnumerable<KeyValuePair<string, string>> vals)
    {
        var shown = new List<KeyValuePair<string, string>>();
        foreach (var (k, v) in vals)
        {
            r.Raw[k] = v ?? "";
            shown.Add(new(k, Disp(k, v ?? "")));
        }
        r.SetAll(shown);
    }

    public void Update(string id, IEnumerable<KeyValuePair<string, string>> vals, string? tag = null)
    {
        if (!_byId.TryGetValue(id, out var r))
            return;
        Put(r, vals);
        if (tag is not null)
            r.Tag = tag;
    }

    public void SetCell(string id, string key, string value)
    {
        if (_byId.TryGetValue(id, out var r))
            Put(r, [new(key, value)]);
    }

    public string Full(string id, string key) => _byId.TryGetValue(id, out var r) ? r.Raw.GetValueOrDefault(key, "") : "";

    public IEnumerable<TRow> Children(string parent) => Rows.Where(r => r.Parent == parent);

    public void Clear()
    {
        _suppressSelect = true;
        Rows.Clear();
        _byId.Clear();
        _suppressSelect = false;
        if (_headCheck is not null)
            _headCheck.IsChecked = false;
    }

    public List<string> Selected() => [.. Grid.SelectedItems.OfType<TRow>().Select(r => r.Id)];

    public void Select(string id)
    {
        if (!_byId.TryGetValue(id, out var r))
            return;
        Grid.SelectedItem = r;
        Grid.ScrollIntoView(r);
    }

    /// <summary><c>t.checked</c>: id → trạng thái, chỉ dòng có ô tick.</summary>
    public Dictionary<string, bool> Checked => Rows.Where(r => r.Checked is not null).ToDictionary(r => r.Id, r => r.Checked == true, StringComparer.Ordinal);

    public List<string> CheckedIds() => [.. Rows.Where(r => r.Checked == true).Select(r => r.Id)];

    public void SetChecked(string id, bool state)
    {
        if (_byId.TryGetValue(id, out var r) && r.Checked is not null)
            r.Checked = state;
    }

    private void ToggleAll()
    {
        var rows = Rows.Where(r => r.Checked is not null).ToList();
        var nv = !rows.All(r => r.Checked == true);
        foreach (var r in rows)
            r.Checked = nv;
        if (_headCheck is not null)
            _headCheck.IsChecked = nv;
        OnToggleAll?.Invoke(nv);
    }

    private static T? Up<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T)
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    private void GridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var cell = Up<DataGridCell>(e.OriginalSource as DependencyObject);
        if (cell?.DataContext is not TRow r)
            return;
        if (Up<DataGridColumnHeader>(e.OriginalSource as DependencyObject) is not null)
            return;
        OnDouble?.Invoke(r.Id, _colKey.GetValueOrDefault(cell.Column, ""));
    }

    private void CellTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is not DataGridCell { DataContext: TRow r } cell)
        {
            e.Handled = true;
            return;
        }
        var key = _colKey.GetValueOrDefault(cell.Column, "");
        string? tip = key.Length > 0 ? Tip?.Invoke(r.Id, key) : null;
        if (tip is null)
        {
            var val = key == "#0" ? r.Text : key.Length > 0 ? r[key] : "";
            // chi hien khi chu bi cat (ô hẹp hơn chữ)
            if (val.Length > 0 && cell.ActualWidth < MeasureWidth(val, cell) + 10)
                tip = val;
        }
        if (string.IsNullOrEmpty(tip))
        {
            e.Handled = true;
            return;
        }
        cell.ToolTip = L.T(tip);
    }

    private static double MeasureWidth(string text, Control c)
    {
        var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                   new Typeface(c.FontFamily, c.FontStyle, c.FontWeight, c.FontStretch), c.FontSize, Brushes.Black,
                                   VisualTreeHelper.GetDpi(c).PixelsPerDip);
        return ft.Width;
    }
}
