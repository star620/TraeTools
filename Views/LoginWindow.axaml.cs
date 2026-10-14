using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TraeCheckin;
using TraeTools.Services.Login;
using TraeTools.ViewModels;

namespace TraeTools.Views;

/// <summary>
/// 登录对话框（UserControl，双平台）。
/// 桌面端：优先用独立宿主进程里的内嵌 WebView2 自动登录（自动读取 token + session），
/// 找不到宿主或 Android 端时，走手动粘贴 Cloud-IDE-Token / Session 的通道。
/// </summary>
public partial class LoginWindow : UserControl, IDialogView
{
    /// <summary>对话框标题（桌面端由 DialogHostWindow 读取作为窗口标题）。</summary>
    public string DialogTitle => "登录 TRAE 账号";

    private readonly TraeAccount? _account;
    private bool _busy;

    public LoginWindow()
    {
        InitializeComponent();
    }

    public LoginWindow(TraeAccount? account)
        : this()
    {
        _account = account;
    }

    /// <summary>内嵌 WebView2 自动登录：由独立宿主进程完成，自动读取 Cloud-IDE-Token 与 X-Cloudide-Session。</summary>
    private async void OnAutoLoginClick(object? sender, RoutedEventArgs e)
    {
#if ANDROID
        // Android 上不存在 WebView2 宿主进程：直接引导用户走手动方案（下方「手动粘贴」）。
        AutoStatus.Text = "手机端请使用下方「手动粘贴」：在电脑浏览器登录 www.trae.cn 后，"
                        + "从开发者工具复制 Cloud-IDE-Token / X-Cloudide-Session 粘贴到下面。";
        await System.Threading.Tasks.Task.CompletedTask;
#else
        if (_busy || _account == null) return;

        // P2-1：内嵌浏览器已拆到独立宿主进程（TraeTools.LoginHost.exe），主程序不含 WinForms/WebView2。
        // 找不到宿主（未随程序分发）时明确提示改用下方手动粘贴方案。
        if (!LoginHostClient.IsAvailable())
        {
            AutoStatus.Text = $"未找到登录宿主程序 {LoginHostClient.HelperFileName}，请使用下方「手动方案」粘贴 Cloud-IDE-Token";
            return;
        }

        _busy = true;
        AutoStatus.Text = "正在启动浏览器…";
        try
        {
            string webViewDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TraeTools", "WebView", "account_" + (_account.Id.Length > 12 ? _account.Id[..12] : _account.Id));

            // 接口探索已完成；false=正常登录（自动识别并关闭窗口）。需要再抓包时改为 true
            var result = await LoginHostClient.RunAsync(webViewDir, _account.Token, debugCapture: false);

            if (result == null)
            {
                AutoStatus.Text = "未获取到 Token（已取消或登录未完成）";
                return;
            }

            _account.Token = result.Token;
            _account.Session = result.Session;
            _account.TokenUpdatedAt = DateTime.Now;
            var uid = TokenUtils.ParseAccountUid(result.Token);
            if (!string.IsNullOrEmpty(uid)) _account.AccountUid = uid;
            MainViewModel.AppConfig?.Save();
            OverlayDialog.CloseDialog(this, true);
        }
        catch (Exception ex)
        {
            AutoStatus.Text = "自动登录失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
        }
#endif
    }

    private void OnOpenBrowserClick(object? sender, RoutedEventArgs e)
        => TraeTools.Platform.BrowserLauncher.Open("https://www.trae.cn");

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var pasted = TokenBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(pasted))
        {
            StatusText.Text = "请先粘贴 Cloud-IDE-Token";
            return;
        }
        var initial = _account?.Token;
        if (!TokenUtils.ShouldAcceptNewToken(initial, pasted))
        {
            StatusText.Text = "Token 无效（长度不足或与当前账号相同），请重新复制";
            return;
        }
        if (_account == null)
        {
            StatusText.Text = "无当前账号，无法保存";
            return;
        }
        _account.Token = pasted;
        _account.TokenUpdatedAt = DateTime.Now;
        var uid = TokenUtils.ParseAccountUid(pasted);
        if (!string.IsNullOrEmpty(uid)) _account.AccountUid = uid;
        MainViewModel.AppConfig?.Save();
        OverlayDialog.CloseDialog(this, true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
        => OverlayDialog.CloseDialog(this, false);
}
