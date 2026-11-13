using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MuxStudio.App;

/// <summary>Chế độ Sáng / Tối / theo Windows (<c>UiAppearance</c>) + nền mờ Windows 11 (<c>UiBackdrop</c>). Đổi ngay, không cần mở lại.
/// Bảng màu riêng của MuxStudio (màu log, màu dòng bảng…) nằm ở Themes/Light.xaml và Themes/Dark.xaml, được thay theo chế độ.</summary>
public static class ThemeService
{
    private static ResourceDictionary? _palette;
    private static bool? _paletteDark;

    public static event Action? Changed;

    public static bool IsDark => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;

    public static WindowBackdropType Backdrop(string? kind) => (kind ?? "none").ToLowerInvariant() switch
    {
        "mica" => WindowBackdropType.Mica,
        "acrylic" => WindowBackdropType.Acrylic,
        _ => WindowBackdropType.None,
    };

    /// <summary><c>set_appearance</c>: <c>system</c> = theo Windows (và đổi theo khi Windows đổi), <c>light</c>, <c>dark</c>.</summary>
    public static void Apply(string? appearance, Window? window, WindowBackdropType backdrop)
    {
        var mode = (appearance ?? "system").ToLowerInvariant();
        if (window is not null)
            SystemThemeWatcher.UnWatch(window);
        if (mode == "light")
        {
            ApplicationThemeManager.Apply(ApplicationTheme.Light, backdrop, true);
        }
        else if (mode == "dark")
        {
            ApplicationThemeManager.Apply(ApplicationTheme.Dark, backdrop, true);
        }
        else
        {
            ApplicationThemeManager.ApplySystemTheme(true);
            if (window is not null)
                SystemThemeWatcher.Watch(window, backdrop, true);
        }
        SyncPalette();
    }

    /// <summary>Thay bảng màu Sáng / Tối của MuxStudio cho khớp chế độ hiện tại.</summary>
    public static void SyncPalette()
    {
        var dark = IsDark;
        if (_paletteDark == dark)
            return;
        var dicts = Application.Current.Resources.MergedDictionaries;
        _palette ??= dicts.LastOrDefault(d => d.Source?.OriginalString.Contains("Themes/", StringComparison.Ordinal) == true
                                              && (d.Source.OriginalString.EndsWith("Dark.xaml", StringComparison.Ordinal)
                                                  || d.Source.OriginalString.EndsWith("Light.xaml", StringComparison.Ordinal)));
        var next = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/MuxStudio;component/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Absolute),
        };
        if (_palette is not null)
        {
            var i = dicts.IndexOf(_palette);
            dicts[i] = next;
        }
        else
        {
            dicts.Add(next);
        }
        _palette = next;
        _paletteDark = dark;
        Changed?.Invoke();
    }

    public static void Init() => ApplicationThemeManager.Changed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(SyncPalette);
}
