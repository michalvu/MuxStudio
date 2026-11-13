using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MuxStudio.Core.Ui;

namespace MuxStudio.App;

/// <summary>
/// Lưới tick cho bảng chọn phụ đề theo tập (port <c>W.CheckGrid</c>). Ô xám = tập đó không có loại phụ đề này; ô vàng = khác quy tắc tự
/// động. Bấm ô → đổi; bấm tiêu đề cột → cả cột; bấm tên tập → cả dòng. Tooltip = <c>tips[(dòng, cột)]</c>. <see cref="OnChange"/> sau mỗi
/// thay đổi. Tiêu đề cột phụ đề NGOÀI (file cạnh phim) tô màu khác cột phụ đề TRONG MKV.
/// </summary>
public sealed class CheckGrid
{
    private readonly Grid _grid = new();
    private readonly Dictionary<(string R, string C), (Border Cell, CheckBox Box)> _cells = [];
    private readonly Dictionary<string, Border> _rowLabels = new(StringComparer.Ordinal);

    public CheckGrid()
    {
        View = new ScrollViewer
        {
            Content = _grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    public ScrollViewer View { get; }

    public GridModel? Model { get; private set; }

    public Action? OnChange { get; set; }

    public void SetModel(GridModel model, IReadOnlyDictionary<string, string> headers, IReadOnlyDictionary<string, string> labels,
                         IReadOnlyDictionary<(string R, string C), string>? tips = null)
    {
        Model = model;
        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
        _grid.ColumnDefinitions.Clear();
        _cells.Clear();
        _rowLabels.Clear();
        if (model.Rows.Count == 0)
            return;
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 260 });
        foreach (var _ in model.Cols)
            _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var corner = Cell(L.T("Tập / file"), "GridHeadBrush", bold: true);
        Place(corner, 0, 0);
        for (var j = 0; j < model.Cols.Count; j++)
        {
            var c = model.Cols[j];
            var h = Cell(L.T(headers.GetValueOrDefault(c, c)), c.StartsWith("E|", StringComparison.Ordinal) ? "GridHeadExtBrush" : "GridHeadBrush", bold: true,
                         center: true);
            h.Cursor = Cursors.Hand;
            h.ToolTip = L.T("Bấm để tick / bỏ cả cột");
            h.MouseLeftButtonUp += (_, _) =>
            {
                if (Model?.ToggleCol(c) == true)
                    Changed();
            };
            Place(h, 0, j + 1);
        }
        for (var i = 0; i < model.Rows.Count; i++)
        {
            var r = model.Rows[i];
            _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
            var stripe = i % 2 == 1 ? "GridStripeBrush" : "GridBgBrush";
            var lab = Cell(labels.GetValueOrDefault(r, r), stripe);
            lab.Cursor = Cursors.Hand;
            lab.ToolTip = labels.GetValueOrDefault(r, r) + "\n" + L.T("Bấm để tick / bỏ cả dòng");
            lab.MouseLeftButtonUp += (_, _) =>
            {
                if (Model?.ToggleRow(r) == true)
                    Changed();
            };
            _rowLabels[r] = lab;
            Place(lab, i + 1, 0);
            for (var j = 0; j < model.Cols.Count; j++)
            {
                var c = model.Cols[j];
                if (!model.Present(r, c))
                {
                    var abs = Cell("—", "GridAbsentBrush", center: true);
                    ((TextBlock)abs.Child).SetResourceReference(TextBlock.ForegroundProperty, "GridAbsentFgBrush");
                    Place(abs, i + 1, j + 1);
                    continue;
                }
                var box = new CheckBox { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0, Padding = new Thickness(0) };
                var cell = new Border { BorderThickness = new Thickness(0, 0, 1, 1), Child = box, Cursor = Cursors.Hand };
                cell.SetResourceReference(Border.BorderBrushProperty, "GridLineBrush");
                if (tips is not null && tips.TryGetValue((r, c), out var tip) && tip.Length > 0)
                    cell.ToolTip = L.T(tip);
                box.Click += (_, _) =>
                {
                    Model?.ToggleCell(r, c);
                    Changed();
                };
                cell.MouseLeftButtonUp += (_, e) =>
                {
                    if (e.OriginalSource == cell && Model?.ToggleCell(r, c) == true)
                        Changed();
                };
                _cells[(r, c)] = (cell, box);
                Place(cell, i + 1, j + 1);
            }
        }
        Redraw();
    }

    public void Clear() => SetModel(new GridModel([], [], new Dictionary<(string, string), bool>()), new Dictionary<string, string>(),
                                    new Dictionary<string, string>());

    private void Changed()
    {
        Redraw();
        OnChange?.Invoke();
    }

    /// <summary>Vẽ lại trạng thái các ô theo <see cref="Model"/> (sau "Tick tất cả", "Trả về mặc định"…).</summary>
    public void Redraw()
    {
        if (Model is null)
            return;
        var rowIndex = Model.Rows.Select((r, i) => (r, i)).ToDictionary(x => x.r, x => x.i, StringComparer.Ordinal);
        foreach (var ((r, c), (cell, box)) in _cells)
        {
            box.IsChecked = Model.Get(r, c) == true;
            var key = Model.Differs(r, c) ? "GridChangedBrush" : rowIndex.GetValueOrDefault(r) % 2 == 1 ? "GridStripeBrush" : "GridBgBrush";
            cell.SetResourceReference(Border.BackgroundProperty, key);
        }
    }

    private static Border Cell(string text, string bgKey, bool bold = false, bool center = false)
    {
        var t = new TextBlock
        {
            Text = text, TextWrapping = center ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = center ? TextAlignment.Center : TextAlignment.Left, Margin = new Thickness(8, 4, 8, 4), MaxWidth = center ? 110 : 520, FontSize = 12,
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        var b = new Border { Child = t, BorderThickness = new Thickness(0, 0, 1, 1) };
        b.SetResourceReference(Border.BackgroundProperty, bgKey);
        b.SetResourceReference(Border.BorderBrushProperty, "GridLineBrush");
        return b;
    }

    private void Place(UIElement e, int row, int col)
    {
        Grid.SetRow(e, row);
        Grid.SetColumn(e, col);
        _grid.Children.Add(e);
    }
}
