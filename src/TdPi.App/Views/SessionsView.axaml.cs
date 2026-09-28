using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using TdPi.App.ViewModels;

namespace TdPi.App.Views;

public partial class SessionsView : UserControl
{
    public SessionsView()
    {
        InitializeComponent();
        DoubleTapped += OnDoubleTapped;
    }

    /// <summary>双击行 = 查看会话内容(点在按钮上时不触发)。</summary>
    private async void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Control c && c.FindLogicalAncestorOfType<Button>() != null) return;
        if (DataContext is SessionsViewModel vm && vm.Selected != null)
        {
            await vm.ViewRowAsync(vm.Selected);
        }
    }
}
