using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace TraeTools.Views;

/// <summary>
/// 通用单行文本输入窗口。ShowDialog&lt;string&gt; 返回：true 路径关窗时返回输入文本（trim 后），
/// 取消返回 null。
/// </summary>
public partial class InputWindow : UserControl, IDialogView
{
    /// <summary>对话框标题（桌面端由 DialogHostWindow 读取，作为宿主窗口标题）。</summary>
    public string DialogTitle { get; private set; } = "输入";

    public string? ResultText { get; private set; }

    public InputWindow()
    {
        InitializeComponent();
    }

    /// <param name="title">窗口标题。</param>
    /// <param name="message">提示文案。</param>
    /// <param name="placeholder">输入框占位符。</param>
    public InputWindow(string title, string message, string placeholder)
        : this()
    {
        TitleText.Text = title;
        DialogTitle = title;
        MessageText.Text = message;
        InputBox.PlaceholderText = placeholder;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var value = InputBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            InputBox.Focus();
            return;
        }
        ResultText = value;
        OverlayDialog.CloseDialog(this, ResultText);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
        => OverlayDialog.CloseDialog(this, null);

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnOkClick(sender, e);
    }
}