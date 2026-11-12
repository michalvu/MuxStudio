using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MuxStudio.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        // Theo chế độ Sáng/Tối của Windows, cập nhật khi người dùng đổi.
        Loaded += (_, _) => SystemThemeWatcher.Watch(this);
    }
}
