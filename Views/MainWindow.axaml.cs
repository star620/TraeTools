using System;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Platform;
using TraeTools.ViewModels;

namespace TraeTools.Views;

/// <summary>
/// 桌面端主窗口（仅 Windows / Linux / macOS 桌面后端）。
/// 界面本体在 <see cref="MainView"/>（UserControl），本窗口只保留桌面专有行为：
/// 系统托盘图标、关闭即最小化到托盘、桌面通知。
/// </summary>
public partial class MainWindow : Window
{
    private TrayIcon? _trayIcon;
    /// <summary>true=用户从托盘「退出」真正退出；false=关闭窗口仅最小化到托盘。</summary>
    private bool _realExit;
    private readonly WindowNotificationManager _notify;

    public MainWindow()
    {
        _notify = new WindowNotificationManager(this) { Position = NotificationPosition.BottomRight, MaxItems = 3 };
        InitializeComponent();

        // 任务栏窗口图标：复用托盘同款 avalonia-logo.ico（缺省时任务栏不显示图标）
        try
        {
            using var iconStream = AssetLoader.Open(new Uri("avares://TraeTools/Assets/avalonia-logo.ico"));
            Icon = new WindowIcon(iconStream);
        }
        catch { /* 图标加载失败不影响主程序 */ }

        BuildTray();
        Closing += OnWindowClosing;

        // 「不同意协议 / 需要真正退出」的退出入口：桌面端要绕过「关闭即最小化到托盘」，
        // 由本窗口接管（Android 上没有窗口，MainView 会走 ApplicationLifetime 兜底）。
        App.ExitRequested = () =>
        {
            _realExit = true;
            Close();
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        App.ExitRequested = null;
        _trayIcon?.Dispose();
        base.OnClosed(e);
    }

    /// <summary>关闭窗口默认最小化到托盘；仅托盘「退出」真正退出。</summary>
    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_realExit) return;
        e.Cancel = true;
        Hide();
        _notify.Show(new Notification("TraeTools", "已最小化到托盘，后台继续自动签到",
            NotificationType.Information, TimeSpan.FromSeconds(2)));
    }

    private void BuildTray()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://TraeTools/Assets/avalonia-logo.ico"));
            var menu = new NativeMenu();
            var show = new NativeMenuItem("显示主窗口");
            show.Click += (_, _) => ShowAndActivate();
            var checkin = new NativeMenuItem("立即签到");
            checkin.Click += async (_, _) => await TrayCheckinAsync();
            var exit = new NativeMenuItem("退出");
            exit.Click += (_, _) =>
            {
                _realExit = true;
                Close();
            };
            menu.Items.Add(show);
            menu.Items.Add(checkin);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(exit);

            _trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(stream),
                ToolTipText = "TraeTools 签到 + 账号切换",
                Menu = menu,
                IsVisible = true
            };
            // 左键单击托盘图标 → 显示/激活主窗口
            _trayIcon.Clicked += (_, _) => ShowAndActivate();
        }
        catch
        {
            // 托盘初始化失败（如系统不支持）不影响主程序
        }
    }

    private void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private async System.Threading.Tasks.Task TrayCheckinAsync()
    {
        if (DataContext is MainViewModel vm)
        {
            bool ok = await vm.QuickCheckinAsync();
            _notify.Show(new Notification(
                ok ? "签到成功" : "签到失败",
                ok ? "今日积分已到账（会员含连签加成）" : "请检查网络或 Token 是否有效",
                ok ? NotificationType.Success : NotificationType.Error));
        }
    }
}
