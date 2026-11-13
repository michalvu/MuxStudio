using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MuxStudio.App;

/// <summary>
/// Icon của chương trình (port <c>W.ICON_ICO</c> / <c>W.ICON_PNG</c>): <c>Assets/muxstudio.ico</c> (7 cỡ 16–256 px) — cũng là icon của file
/// .exe (<c>ApplicationIcon</c> trong .csproj) — và <c>Assets/muxstudio.png</c> (256 px, cho thanh tiêu đề). Nhúng vào chương trình dạng
/// tài nguyên WPF.
/// </summary>
public static class AppIcon
{
    public static readonly Uri IcoUri = new("pack://application:,,,/MuxStudio;component/Assets/muxstudio.ico", UriKind.Absolute);
    public static readonly Uri PngUri = new("pack://application:,,,/MuxStudio;component/Assets/muxstudio.png", UriKind.Absolute);

    private static ImageSource? _image;
    private static bool _tried;

    /// <summary>Icon cửa sổ (Windows chọn cỡ hợp với thanh tác vụ / Alt+Tab); không đọc được → <c>null</c> (icon mặc định, vẫn chạy).</summary>
    public static ImageSource? Image
    {
        get
        {
            if (!_tried)
            {
                _tried = true;
                try
                {
                    _image = Frames(IcoUri)[0];
                }
                catch (Exception e) when (e is IOException or NotSupportedException or FileFormatException or InvalidOperationException)
                {
                    _image = null;
                }
            }
            return _image;
        }
    }

    /// <summary>Mọi cỡ trong một file icon / ảnh (ném lỗi nếu không có / không đọc được).</summary>
    public static IReadOnlyList<BitmapFrame> Frames(Uri uri) =>
        BitmapDecoder.Create(uri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames;
}
