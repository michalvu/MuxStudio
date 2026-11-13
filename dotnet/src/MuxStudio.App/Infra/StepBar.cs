using System.Windows;
using System.Windows.Controls;
using MuxStudio.Core.Ui;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MuxStudio.App;

/// <summary>
/// Thanh các bước của một thẻ (port <c>W.StepBar</c>): nút TO, đánh số theo thứ tự, mũi tên giữa các nút. Bước kế tiếp nên bấm sáng màu nhấn
/// (▶), bước đã xong xanh lá (✓), còn lại xám. <see cref="Update"/> sau mỗi thay đổi. <see cref="SetHidden"/> ẩn bước không cần cho chế độ
/// đang chọn — các bước còn lại tự đánh số lại.
/// </summary>
public sealed class StepBar
{
    private readonly List<string> _keys;
    private readonly Dictionary<string, string> _titles;
    private readonly HashSet<string> _plain;
    private readonly Dictionary<string, TextBlock> _arrows = [];
    private readonly Dictionary<string, TextBlock> _labels = [];
    private HashSet<string> _hidden = [];

    public StepBar(Panel host, IReadOnlyList<(string Key, string Title, Action Fn)> steps, IEnumerable<string>? plain = null)
    {
        _keys = [.. steps.Select(s => s.Key)];
        _titles = steps.ToDictionary(s => s.Key, s => s.Title, StringComparer.Ordinal);
        _plain = [.. plain ?? []];
        var first = true;
        foreach (var (k, _, fn) in steps)
        {
            if (!first)
            {
                var ar = new TextBlock { Text = "→", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0), FontSize = 15, Opacity = 0.55 };
                ar.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                _arrows[k] = ar;
                host.Children.Add(ar);
            }
            first = false;
            var lab = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 13 };
            var b = new Button { Content = lab, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 3, 0, 3), MinHeight = 36 };
            b.Click += (_, _) => fn();
            _labels[k] = lab;
            Buttons[k] = b;
            host.Children.Add(b);
        }
        Update([.. _keys.Select(_ => false)], [.. _keys.Select(_ => true)], false);
    }

    public Dictionary<string, Button> Buttons { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> States { get; } = new(StringComparer.Ordinal);

    public void SetHidden(IEnumerable<string> keys)
    {
        _hidden = [.. keys.Where(_keys.Contains)];
        var first = true;
        foreach (var k in _keys)
        {
            var show = !_hidden.Contains(k);
            Buttons[k].Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (_arrows.TryGetValue(k, out var ar))
                ar.Visibility = show && !first ? Visibility.Visible : Visibility.Collapsed; // buoc HIEN dau tien: khong co mui ten dung truoc
            first = first && !show;
        }
    }

    public List<string> Update(IReadOnlyList<bool> done, IReadOnlyList<bool> enabled, bool busy)
    {
        var sts = Steps.VisibleStates(_keys, _hidden, done, enabled);
        var n = 0;
        for (var i = 0; i < _keys.Count; i++)
        {
            var k = _keys[i];
            var s = sts[i];
            States[k] = s;
            if (s == "hidden")
                continue;
            var b = Buttons[k];
            _labels[k].Text = L.T(Steps.Label(n, _titles[k], s, !_plain.Contains(k)));
            System.Windows.Automation.AutomationProperties.SetName(b, L.T(_titles[k])); // trinh doc man hinh: ten buoc (khong kem ✓ / ▶)
            b.Appearance = s switch
            {
                "next" => ControlAppearance.Primary,
                "done" => ControlAppearance.Success,
                _ => _plain.Contains(k) ? ControlAppearance.Transparent : ControlAppearance.Secondary,
            };
            b.IsEnabled = enabled[i] && !busy;
            n++;
        }
        return sts;
    }
}
