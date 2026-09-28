using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using TdPi.App.ViewModels;

namespace TdPi.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Dialogs.Owner = this;
        try
        {
            // 像素风 π 图标(多尺寸 ico)。注意:avares:// 后面是程序集名 td-pi
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://td-pi/Assets/AppIcon.ico")));
        }
        catch
        {
            // 图标缺失不影响功能(exe 内嵌图标会兜底显示)
        }
    }
}
