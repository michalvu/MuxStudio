using System.Windows.Markup;
using MuxStudio.Core;

namespace MuxStudio.App;

/// <summary>
/// <c>{l:Tr 'Quét thư mục'}</c> trong XAML: chữ tiếng Việt gốc → chữ theo ngôn ngữ giao diện (<see cref="I18n.T"/>). Ngôn ngữ đổi có hiệu lực
/// khi mở lại chương trình (như bản Python), nên dịch MỘT lần lúc nạp XAML là đủ. Mã so sánh vẫn dùng chữ tiếng Việt.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string text) => Text = text;

    [ConstructorArgument("text")]
    public string Text { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => I18n.T(Text);
}

/// <summary>Dịch ở chỗ hiển thị (= <c>W.tr</c> của bản Python).</summary>
public static class L
{
    public static string T(string? text) => I18n.T(text ?? "");
}
