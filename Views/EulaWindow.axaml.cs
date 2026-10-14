using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TraeTools.Views;

/// <summary>
/// 首次启动的最终用户许可协议弹窗（Avalonia 版）。
/// ShowDialog&lt;bool&gt; 返回值：true=同意并继续，false=不同意并退出。
/// </summary>
public partial class EulaWindow : UserControl, IDialogView
{
    /// <summary>对话框标题（桌面端由 DialogHostWindow 读取，作为宿主窗口标题）。</summary>
    public string DialogTitle => "TRAETOOLS 最终用户许可协议";

    public EulaWindow()
    {
        InitializeComponent();
        // 勾选同意后才允许点「同意并继续」
        AgreeCheck.IsCheckedChanged += (_, _) =>
        {
            AgreeButton.IsEnabled = AgreeCheck.IsChecked == true;
        };
    }

    private void OnAgreeClick(object? sender, RoutedEventArgs e)
        => OverlayDialog.CloseDialog(this, true);

    private void OnDeclineClick(object? sender, RoutedEventArgs e)
        => OverlayDialog.CloseDialog(this, false);
}
