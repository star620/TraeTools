using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TraeTools.Views;

/// <summary>粘贴 GitHub PAT 的输入窗口：返回 true 表示已提交有效文本（访问 PatText 获取）。</summary>
public partial class PatInputWindow : UserControl, IDialogView
{
    /// <summary>对话框标题（桌面端由 DialogHostWindow 读取，作为宿主窗口标题）。</summary>
    public string DialogTitle => "粘贴 GitHub Token（PAT）";

    public string? PatText { get; private set; }

    public PatInputWindow()
    {
        InitializeComponent();
    }

    private void OnOpenGithubClick(object? sender, RoutedEventArgs e)
    {
        // 走跨平台封装：Android 用 Intent 拉起系统浏览器，失败会写日志（category=browser）
        TraeTools.Platform.BrowserLauncher.Open("https://github.com/settings/personal-access-tokens/new");
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var value = PatBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            PatBox.Text = "";
            PatBox.Focus();
            return;
        }
        PatText = value;
        OverlayDialog.CloseDialog(this, true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => OverlayDialog.CloseDialog(this, false);
}
