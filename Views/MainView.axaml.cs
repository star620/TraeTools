using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TraeTools.ViewModels;

namespace TraeTools.Views;

/// <summary>
/// 双平台主视图（UserControl）：桌面端由 <see cref="MainWindow"/> 承载，Android 直接作为单视图根。
/// 承载两平台共享逻辑：覆盖层对话框宿主挂接、自动签到定时器、首次启动 EULA。
/// </summary>
public partial class MainView : UserControl
{
    /// <summary>自动签到定时器（双平台）：每 30 秒检查一次，到点且当天未签时自动签到。</summary>
    private readonly DispatcherTimer _autoCheckinTimer;
    private bool _tickBusy;
    private bool _started;
    private bool _eulaChecked;

    public MainView()
    {
        InitializeComponent();

        // 覆盖层宿主：Android 用于承载对话框；桌面端挂上也不使用（IsOverlayMode=false）
        OverlayDialog.Attach(OverlayRoot, OverlayContent);
        Platform.CrashLog.Boot("MainView: ctor, overlay attached");

        _autoCheckinTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        // 防重入：上一轮未结束则跳过本轮，避免长任务在常驻托盘场景下叠加
        _autoCheckinTimer.Tick += async (_, _) =>
        {
            if (_tickBusy) return;
            _tickBusy = true;
            try
            {
                if (DataContext is MainViewModel vm)
                {
                    await vm.AutoCheckinIfDue();
#if !ANDROID
                    await vm.RefreshSnapshotIfNeeded();
#endif
                }
            }
            catch (Exception ex)
            {
                // 定时任务异常不允许带崩 UI（Android 上未捕获异常会直接闪退）
                Platform.CrashLog.Write("AutoCheckin timer tick", ex);
            }
            finally
            {
                _tickBusy = false;
            }
        };

        // EULA 需要 owner 窗口居中（桌面端），故放到 Loaded：此时视觉树与宿主窗口均已就绪
        //（构造期 desktop.MainWindow 尚未赋值，取 owner 会失败导致协议弹不出来）
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_eulaChecked) return;
        _eulaChecked = true;
        Platform.CrashLog.Boot("MainView: loaded");
        try
        {
            _ = EulaCheckAsync();
        }
        catch (Exception ex)
        {
            Platform.CrashLog.Write("MainView.OnLoaded", ex);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_started) return;
        _started = true;
        Platform.CrashLog.Boot("MainView: attached to visual tree");
        try
        {
            // 自动签到仅在 App 运行期间有效；Android 长期无人值守请使用「云端签到」
            _autoCheckinTimer.Start();
        }
        catch (Exception ex)
        {
            Platform.CrashLog.Write("MainView.OnAttachedToVisualTree", ex);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        try
        {
            _autoCheckinTimer.Stop();
        }
        catch
        {
            // 停止定时器失败不影响退出
        }
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// 首次启动弹出免责声明协议（EULA）。同意标记独立存放于数据根目录 eula_accepted，
    /// 与 TraeCheckin 的已同意配置互相独立，保证本程序首次启动必定展示协议。
    /// </summary>
    private async Task EulaCheckAsync()
    {
        try
        {
            var flag = System.IO.Path.Combine(TraeTools.Services.DataPaths.Root, "eula_accepted");
            if (System.IO.File.Exists(flag)) return;

            Platform.CrashLog.Boot("EULA: showing agreement");
            var eula = new EulaWindow();
            bool agreed;
            try
            {
                // owner：桌面端用宿主窗口居中；Android 上为 null（走覆盖层，忽略 owner）
                var owner = TopLevel.GetTopLevel(this) as Window;
                agreed = await OverlayDialog.ShowAsync<bool>(eula, owner);
            }
            catch (Exception ex)
            {
                // 兜底：对话框呈现失败则记录并视为同意，保证应用可用（日志留痕）
                Platform.CrashLog.Write("EULA dialog failed", ex);
                agreed = true;
            }
            Platform.CrashLog.Boot($"EULA: agreed={agreed}");

            if (agreed)
            {
                try
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(flag)!);
                    System.IO.File.WriteAllText(flag, DateTime.UtcNow.ToString("o"));
                }
                catch
                {
                    // 标记写失败下次仍会弹，可接受
                }
            }
            else
            {
                // 不同意：桌面端真正退出（绕过「最小化到托盘」，由 App.ExitRequested 挂接）；
                // Android 单视图无法关闭界面，保持界面不动即可
                App.RequestExit();
            }
        }
        catch (Exception ex)
        {
            Platform.CrashLog.Write("MainView.EulaCheckAsync", ex);
        }
    }
}
