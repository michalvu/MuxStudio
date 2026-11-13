using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MuxStudio.App;

/// <summary>
/// Nhãn màu (chip / badge) và ô thống kê theo bản thiết kế "Concept 3". Loại màu: <c>good</c> (xanh lá), <c>need</c> / <c>amber</c> (vàng),
/// <c>bad</c> (đỏ), <c>info</c> / <c>cyan</c> (xanh lơ), <c>accent</c> / <c>indigo</c> (tím), <c>dim</c> (xám); rỗng = không tô.
/// Màu nhấn giống nhau ở chế độ Sáng / Tối (nền là màu nhấn pha trong suốt).
/// </summary>
public static class Chips
{
    private static readonly Dictionary<string, (Color Accent, Color Soft)> Kinds = new(StringComparer.Ordinal)
    {
        ["good"] = (Color.FromRgb(0x10, 0xB9, 0x81), Color.FromArgb(0x26, 0x10, 0xB9, 0x81)),
        ["need"] = (Color.FromRgb(0xD9, 0x8C, 0x06), Color.FromArgb(0x2E, 0xF5, 0x9E, 0x0B)),
        ["amber"] = (Color.FromRgb(0xD9, 0x8C, 0x06), Color.FromArgb(0x2E, 0xF5, 0x9E, 0x0B)),
        ["bad"] = (Color.FromRgb(0xF4, 0x3F, 0x5E), Color.FromArgb(0x26, 0xF4, 0x3F, 0x5E)),
        ["info"] = (Color.FromRgb(0x06, 0xA5, 0xC4), Color.FromArgb(0x26, 0x06, 0xB6, 0xD4)),
        ["cyan"] = (Color.FromRgb(0x06, 0xA5, 0xC4), Color.FromArgb(0x26, 0x06, 0xB6, 0xD4)),
        ["accent"] = (Color.FromRgb(0x81, 0x8C, 0xF8), Color.FromArgb(0x26, 0x81, 0x8C, 0xF8)),
        ["indigo"] = (Color.FromRgb(0x81, 0x8C, 0xF8), Color.FromArgb(0x26, 0x81, 0x8C, 0xF8)),
        ["dim"] = (Color.FromRgb(0x8A, 0x93, 0x9E), Color.FromArgb(0x1E, 0x8A, 0x93, 0x9E)),
    };

    private static readonly Dictionary<(string, bool), SolidColorBrush> Cache = [];

    /// <summary>Màu của loại (<paramref name="soft"/> = nền nhạt); loại lạ / rỗng → <c>null</c>.</summary>
    public static SolidColorBrush? Brush(string? kind, bool soft)
    {
        if (string.IsNullOrEmpty(kind) || !Kinds.TryGetValue(kind, out var c))
            return null;
        if (!Cache.TryGetValue((kind, soft), out var b))
        {
            b = new SolidColorBrush(soft ? c.Soft : c.Accent);
            b.Freeze();
            Cache[(kind, soft)] = b;
        }
        return b;
    }

    /// <summary>Một nhãn nhỏ bo góc (chữ đã dịch).</summary>
    public static Border Make(string text, string kind, bool mono = false, double size = 11.5)
    {
        var t = new TextBlock { Text = L.T(text), FontSize = size, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        if (mono)
            t.FontFamily = (FontFamily)Application.Current.FindResource("MonoFont");
        var fg = Brush(kind, false);
        if (fg is not null)
            t.Foreground = fg;
        else
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        return new Border
        {
            Child = t, CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(0, 0, 6, 4),
            Background = Brush(kind, true) ?? Brushes.Transparent, BorderBrush = fg ?? Brushes.Gray, BorderThickness = new Thickness(fg is null ? 0 : 1),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>Đặt lại một dãy nhãn.</summary>
    public static void Fill(Panel panel, IEnumerable<(string Text, string Kind)> chips, bool mono = false)
    {
        panel.Children.Clear();
        foreach (var (t, k) in chips)
            panel.Children.Add(Make(t, k, mono));
    }

    /// <summary>Ô thống kê: nhãn nhỏ ở trên, giá trị đậm ở dưới (như ô "Frame Rate" của bản thiết kế).</summary>
    public static Border Tile(string label, string value, string kind = "", double minWidth = 120)
    {
        var sp = new StackPanel();
        var lab = new TextBlock { Text = L.T(label), FontSize = 10.5, Opacity = 0.7, FontFamily = (FontFamily)Application.Current.FindResource("MonoFont"),
                                  TextTrimming = TextTrimming.CharacterEllipsis };
        lab.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        var val = new TextBlock { Text = value, FontSize = 13, FontWeight = FontWeights.Bold, FontFamily = (FontFamily)Application.Current.FindResource("MonoFont"),
                                  TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        if (Brush(kind, false) is { } fg)
            val.Foreground = fg;
        else
            val.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        sp.Children.Add(lab);
        sp.Children.Add(val);
        var b = new Border { Child = sp, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0),
                             MinWidth = minWidth, BorderThickness = new Thickness(1), ToolTip = value };
        b.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
        b.SetResourceReference(Border.BorderBrushProperty, "ControlElevationBorderBrush");
        return b;
    }
}

/// <summary>Loại màu (chuỗi) → màu: <c>ConverterParameter="soft"</c> = nền nhạt; loại rỗng → trong suốt (nền) / mặc định (chữ).</summary>
public sealed class KindBrushConverter : IValueConverter
{
    public static readonly KindBrushConverter Accent = new();
    public static readonly KindBrushConverter Soft = new() { IsSoft = true };

    public bool IsSoft { get; init; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Chips.Brush(value as string, IsSoft) ?? (IsSoft ? Brushes.Transparent : DependencyProperty.UnsetValue);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Loại màu → kiểu nút WPF-UI (<c>good</c> = Success, <c>primary</c> = Primary, còn lại Secondary).</summary>
public sealed class KindAppearanceConverter : IValueConverter
{
    public static readonly KindAppearanceConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => (value as string) switch
    {
        "good" => ControlAppearance.Success,
        "primary" => ControlAppearance.Primary,
        "bad" => ControlAppearance.Danger,
        _ => ControlAppearance.Secondary,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Chuỗi rỗng → ẩn.</summary>
public sealed class EmptyToCollapsedConverter : IValueConverter
{
    public static readonly EmptyToCollapsedConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Đúng → gạch ngang chữ (track sẽ bị XOÁ).</summary>
public sealed class StrikeConverter : IValueConverter
{
    public static readonly StrikeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? TextDecorations.Strikethrough : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
