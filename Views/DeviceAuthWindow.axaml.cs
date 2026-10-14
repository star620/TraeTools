using Avalonia.Controls;
using Avalonia.Interactivity;
using TraeCheckin;

namespace TraeTools.Views;

/// <summary>
/// GitHub 设备码授权窗口。申请设备码→展示授权码→打开授权页→轮询授权结果，
/// ShowDialog&lt;bool&gt; 返回是否授权成功；成功后通过 <see cref="AccessToken"/> 取回 token。
/// </summary>
public partial class DeviceAuthWindow : UserControl, IDialogView
{
    /// <summary>对话框标题（桌面端由 DialogHostWindow 读取，作为宿主窗口标题）。</summary>
    public string DialogTitle => "GitHub 授权";

    private readonly GitHubApiClient _api = null!;
    private GitHubDeviceCode? _code;
    private bool _polling;
    private bool _done;

    /// <summary>授权成功后的 access_token；仅当对话框返回 true 时有效。</summary>
    public string? AccessToken { get; private set; }

    public DeviceAuthWindow() { InitializeComponent(); }

    public DeviceAuthWindow(GitHubApiClient api)
        : this()
    {
        _api = api;
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            StatusText.Text = "正在申请设备码…";
            var code = await _api.RequestDeviceCodeAsync();
            if (code == null)
            {
                StatusText.Text = "申请设备码失败：" + (_api.LastError ?? "未知错误");
                _done = true;
                return;
            }
            _code = code;
            CodeText.Text = code.UserCode;
            UriText.Text = code.VerificationUri;
            OpenPage(code);
            StatusText.Text = "已打开授权页并复制授权码，请在页面输入授权码完成授权…";
            StartPolling();
        }
        catch (Exception ex)
        {
            StatusText.Text = "初始化失败：" + ex.Message;
            _done = true;
        }
    }

    private void OpenPage(GitHubDeviceCode code)
    {
        
        // 跨平台打开：Android 走 Intent（Activity → Application → 选择器 三级回退），失败会写日志
        if (!TraeTools.Platform.BrowserLauncher.Open(code.VerificationUri))
            StatusText.Text = "自动打开浏览器失败，请手动访问：" + code.VerificationUri;
    }

    private void OnOpenPageClick(object? sender, RoutedEventArgs e)
    {
        if (_code != null)
        {
            OpenPage(_code);
            StatusText.Text = "已重新打开授权页面，授权码：" + _code.UserCode;
        }
    }

    private void OnWaitClick(object? sender, RoutedEventArgs e)
    {
        if (!_polling && !_done) StartPolling();
    }

    private void StartPolling()
    {
        if (_polling || _code == null) return;
        _polling = true;
        _ = PollLoop();
    }

    private async Task PollLoop()
    {
        var interval = _code!.Interval >= 1 ? _code.Interval : 5;
        // interval 秒 ×120 ≈ 最长 10 分钟
        for (int i = 0; i < 120; i++)
        {
            await Task.Delay(interval * 1000);
            var (state, token) = await _api.PollForAccessTokenAsync(_code.DeviceCode);
            if (state == DeviceAuthState.Success && !string.IsNullOrEmpty(token))
            {
                AccessToken = token;
                _done = true;
                StatusText.Text = "授权成功 ✓";
                OverlayDialog.CloseDialog(this, true);
                return;
            }
            if (state == DeviceAuthState.Failed)
            {
                _polling = false;
                _done = true;
                StatusText.Text = "授权失败：" + (_api.LastError ?? "未知错误");
                return;
            }
            StatusText.Text = $"等待授权中…（已等待 {((i + 1) * interval) / 60} 分钟，请在浏览器确认）";
        }
        _polling = false;
        _done = true;
        StatusText.Text = "授权超时（约 10 分钟），请关闭后重试";
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _done = true;
        OverlayDialog.CloseDialog(this, false);
    }
}



