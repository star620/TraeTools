using Avalonia.Controls;

namespace TraeTools.Views;

/// <summary>
/// 桌面端对话框承载窗口。
/// 由于 Android 单视图平台不能创建 Window（WindowingPlatformStub.CreateWindow 抛
/// NotSupportedException），所有对话框内容都实现为 UserControl；
/// 桌面端仍需要一个真正的窗口来 ShowDialog，故由本窗口包装后显示。
/// ⚠️ 仅在桌面端（<c>!OverlayDialog.IsOverlayMode</c>）实例化。
/// </summary>
public partial class DialogHostWindow : Window
{
    public DialogHostWindow()
    {
        InitializeComponent();
    }

    /// <summary>把对话框 UserControl 装入本窗口，并沿用其标题作为窗口标题。</summary>
    public void AttachDialog(UserControl dialog, string? title)
    {
        DialogHost.Content = dialog;
        if (!string.IsNullOrWhiteSpace(title))
            Title = title;
    }

    /// <summary>卸载内容，避免窗口关闭后仍持有对话框引用。</summary>
    public void DetachDialog()
    {
        DialogHost.Content = null;
    }
}
