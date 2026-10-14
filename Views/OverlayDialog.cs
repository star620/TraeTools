using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Presenters;

namespace TraeTools.Views;

/// <summary>
/// 双平台模态对话框宿主。
///
/// ⚠️ 关键背景：Android 是 Avalonia 单视图平台，<c>new Window()</c> 会在
/// <c>Avalonia.Android.WindowingPlatformStub.CreateWindow()</c> 抛 NotSupportedException，
/// 因此**所有对话框内容都必须是 UserControl**：
///   - Android：把 UserControl 直接挂到主视图的覆盖层（ZIndex 999）呈现，
///     用 TaskCompletionSource 模拟模态返回；
///   - 桌面端：由 <see cref="DialogHostWindow"/>（真正的 Window）包装后 ShowDialog。
/// </summary>
public static class OverlayDialog
{
    private static Panel? _root;
    private static ContentPresenter? _host;
    private static UserControl? _current;
    private static TaskCompletionSource<object?>? _tcs;

    /// <summary>当前是否处于覆盖层对话框模式（仅 Android）。</summary>
    public static bool IsOverlayMode => OperatingSystem.IsAndroid();

    /// <summary>主视图启动时挂接覆盖层（桌面端挂上也不使用）。</summary>
    public static void Attach(Panel root, ContentPresenter host)
    {
        _root = root;
        _host = host;
    }

    /// <summary>
    /// 双平台对话框入口：桌面端用 DialogHostWindow 包装后原生 ShowDialog，
    /// Android 走覆盖层。返回对话框通过 <see cref="CloseDialog"/> 关闭时携带的结果。
    /// </summary>
    public static async Task<T> ShowAsync<T>(UserControl dialog, Window? owner)
    {
        if (!IsOverlayMode)
        {
            try
            {
                var host = new DialogHostWindow();
                host.AttachDialog(dialog, (dialog as IDialogView)?.DialogTitle);
                // ShowDialog 需要 owner；缺省时退回桌面端主窗口
                var target = owner ?? (Avalonia.Application.Current?.ApplicationLifetime
                                       as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                if (target == null)
                {
                    Platform.CrashLog.Write("OverlayDialog.ShowAsync",
                        new InvalidOperationException("桌面端无可用 owner 窗口，无法显示对话框"));
                    return default!;
                }
                var result = await host.ShowDialog<object?>(target);
                host.DetachDialog();
                return result is T typed ? typed : default!;
            }
            catch (Exception ex)
            {
                Platform.CrashLog.Write("OverlayDialog.ShowAsync(desktop)", ex);
                return default!;
            }
        }

        try
        {
            var tcs = ShowCore(dialog);
            var result = await tcs.Task;
            return result is T typedValue ? typedValue : default!;
        }
        catch (Exception ex)
        {
            // 覆盖层呈现失败时降级返回默认值：绝不让对话框问题带崩应用（Android 上即闪退）
            Platform.CrashLog.Write("OverlayDialog.ShowAsync(overlay)", ex);
            return default!;
        }
    }

    /// <summary>对话框按钮统一关闭入口：Android 走覆盖层还原，桌面走宿主 Window.Close(result)。</summary>
    public static void CloseDialog(UserControl dialog, object? result)
    {
        if (IsOverlayMode && ReferenceEquals(_current, dialog))
        {
            Finish(dialog, result);
            return;
        }

        try
        {
            var host = TopLevel.GetTopLevel(dialog) as Window;
            if (host != null)
            {
                host.Close(result);
                return;
            }
        }
        catch (Exception ex)
        {
            Platform.CrashLog.Write("OverlayDialog.CloseDialog", ex);
        }
    }

    private static TaskCompletionSource<object?> ShowCore(UserControl dialog)
    {
        if (_root == null || _host == null)
            throw new InvalidOperationException("OverlayDialog 尚未 Attach（主视图未就绪）");
        if (_tcs != null)
            throw new InvalidOperationException("已有对话框在显示中，不允许叠加");

        // 手机屏幕较窄：清掉 XAML 中为桌面端写死的 Width，改为填满覆盖层卡片
        try
        {
            dialog.Width = double.NaN;
            dialog.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        }
        catch
        {
            // 尺寸调整失败不影响显示
        }

        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tcs = tcs;
        _current = dialog;
        _host.Content = dialog;
        _root.IsVisible = true;
        return tcs;
    }

    private static void Finish(UserControl dialog, object? result)
    {
        if (!ReferenceEquals(_current, dialog) || _tcs == null || _host == null || _root == null)
            return;

        _host.Content = null;
        _root.IsVisible = false;

        var tcs = _tcs;
        _tcs = null;
        _current = null;
        tcs.TrySetResult(result);
    }
}
