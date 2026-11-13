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
/// Nền = màu nhấn pha trong suốt (giống nhau ở Sáng / Tối). Chữ + viền = <c>*TextBrush</c> của Themes/Light.xaml / Dark.xaml (sắc đậm ở
/// chế độ Sáng, sắc sáng ở chế độ Tối — đủ tương phản cả trên nền Acrylic / Mica), gắn bằng tham chiếu động nên tự đổi khi đổi chế độ.
/// </summary>
public static class Chips
{
    private static readonly Dictionary<string, (string TextKey, Color Soft)> Kinds = new(StringComparer.Ordinal)
    {
        ["good"] = ("EmeraldTextBrush", Color.FromArgb(0x26, 0x10, 0xB9, 0x81)),
        ["need"] = ("AmberTextBrush", Color.FromArgb(0x2E, 0xF5, 0x9E, 0x0B)),
        ["amber"] = ("AmberTextBrush", Color.FromArgb(0x2E, 0xF5, 0x9E, 0x0B)),
        ["bad"] = ("RoseTextBrush", Color.FromArgb(0x26, 0xF4, 0x3F, 0x5E)),
        ["info"] = ("CyanTextBrush", Color.FromArgb(0x26, 0x06, 0xB6, 0xD4)),
        ["cyan"] = ("CyanTextBrush", Color.FromArgb(0x26, 0x06, 0xB6, 0xD4)),
        ["accent"] = ("IndigoTextBrush", Color.FromArgb(0x26, 0x81, 0x8C, 0xF8)),
        ["indigo"] = ("IndigoTextBrush", Color.FromArgb(0x26, 0x81, 0x8C, 0xF8)),
        ["dim"] = ("DimTextBrush", Color.FromArgb(0x1E, 0x8A, 0x93, 0x9E)),
    };

    /// <summary>Mọi loại màu có tên.</summary>
    public static IEnumerable<string> KindNames => Kinds.Keys;

    private static readonly Dictionary<string, SolidColorBrush> SoftCache = new(StringComparer.Ordinal);

    /// <summary>Khoá tài nguyên màu CHỮ của loại (<c>EmeraldTextBrush</c>…); loại lạ / rỗng → <c>null</c>.</summary>
    public static string? TextKey(string? kind) => !string.IsNullOrEmpty(kind) && Kinds.TryGetValue(kind, out var c) ? c.TextKey : null;

    /// <summary>Màu của loại: <paramref name="soft"/> = nền nhạt; không thì màu chữ của chế độ Sáng / Tối HIỆN TẠI. Loại lạ / rỗng → <c>null</c>.</summary>
    public static SolidColorBrush? Brush(string? kind, bool soft)
    {
        if (string.IsNullOrEmpty(kind) || !Kinds.TryGetValue(kind, out var c))
            return null;
        if (!soft)
            return Application.Current?.TryFindResource(c.TextKey) as SolidColorBrush;
        if (!SoftCache.TryGetValue(kind, out var b))
        {
            b = new SolidColorBrush(c.Soft);
            b.Freeze();
            SoftCache[kind] = b;
        }
        return b;
    }

    /// <summary>Một nhãn nhỏ bo góc (chữ đã dịch).</summary>
    public static Border Make(string text, string kind, bool mono = false, double size = 11.5)
    {
        var t = new TextBlock { Text = L.T(text), FontSize = size, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        if (mono)
            t.FontFamily = (FontFamily)Application.Current.FindResource("MonoFont");
        var key = TextKey(kind);
        t.SetResourceReference(TextBlock.ForegroundProperty, key ?? "TextFillColorPrimaryBrush");
        var b = new Border
        {
            Child = t, CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(0, 0, 6, 4),
            Background = Brush(kind, true) ?? Brushes.Transparent, BorderThickness = new Thickness(key is null ? 0 : 1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (key is not null)
            b.SetResourceReference(Border.BorderBrushProperty, key);
        return b;
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
        var lab = new TextBlock { Text = L.T(label), FontSize = 10.5, FontFamily = (FontFamily)Application.Current.FindResource("MonoFont"),
                                  TextTrimming = TextTrimming.CharacterEllipsis };
        lab.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var val = new TextBlock { Text = value, FontSize = 13, FontWeight = FontWeights.Bold, FontFamily = (FontFamily)Application.Current.FindResource("MonoFont"),
                                  TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        val.SetResourceReference(TextBlock.ForegroundProperty, TextKey(kind) ?? "TextFillColorPrimaryBrush");
        sp.Children.Add(lab);
        sp.Children.Add(val);
        var b = new Border { Child = sp, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0),
                             MinWidth = minWidth, BorderThickness = new Thickness(1), ToolTip = value };
        b.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
        b.SetResourceReference(Border.BorderBrushProperty, "ControlElevationBorderBrush");
        return b;
    }
}

/// <summary>Loại màu (chuỗi) → màu: <see cref="Soft"/> = nền nhạt, <see cref="Accent"/> = màu chữ của chế độ Sáng / Tối lúc gắn (đổi chế độ thì
/// nơi dùng phải gắn lại — vd <c>MuxView</c> làm mới danh sách thẻ luồng); loại rỗng → trong suốt (nền) / mặc định (chữ).</summary>
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
