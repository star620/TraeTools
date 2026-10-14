using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using TraeTools.ViewModels;
using TraeTools.Views;

namespace TraeTools;

/// <summary>
/// 应用入口（双平台）。
/// 注意：.NET Android SDK 会生成含 Android.App 的全局 using，
/// 本文件中的 Application 一律写全称 Avalonia.Application，否则与之歧义（CS0104）。
/// </summary>
public partial class App : Avalonia.Application
{
    /// <summary>系统托盘图标全局引用（TrayIcon 无源头引用会被 GC 回收，故存于此；仅桌面端使用）。</summary>
    public static TrayIcon? TrayRef;

    /// <summary>
    /// 「需要真正退出」的退出入口。桌面端由 MainWindow 挂上（关闭窗口默认是「最小化到托盘」，
    /// 该行为会拦住退出），Android 上没有窗口，<see cref="RequestExit"/> 会退回
    /// ApplicationLifetime 的默认退出方式。
    /// </summary>
    public static System.Action? ExitRequested;

    /// <summary>请求退出应用（不同意 EULA 等场景使用）。</summary>
    public static void RequestExit()
    {
        try
        {
            if (ExitRequested != null)
            {
                ExitRequested();
                return;
            }
            if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }
        catch
        {
            // 退出失败不阻塞其他逻辑
        }
    }

    public override void Initialize()
    {
        try
        {
            AvaloniaXamlLoader.Load(this);
            Platform.CrashLog.Boot("App.Initialize completed");
        }
        catch (System.Exception ex)
        {
            Platform.CrashLog.Write("App.Initialize", ex, true);
            throw;
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Platform.CrashLog.Install();
        Platform.CrashLog.Boot(
            $"App.OnFrameworkInitializationCompleted lifetime={ApplicationLifetime?.GetType().FullName}");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(),
            };
            Platform.CrashLog.Boot("App: desktop MainWindow created");
        }
#if ANDROID
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            // 上次运行崩溃过：本次先进错误页（可复制堆栈 / 重试），避免反复闪退
            var flag = Platform.CrashLog.ReadFlag();
            if (!string.IsNullOrEmpty(flag))
            {
                Platform.CrashLog.Boot("App: previous crash detected -> startup error view");
                singleView.MainView = BuildErrorView(singleView, "上次启动崩溃了", flag);
                base.OnFrameworkInitializationCompleted();
                return;
            }

            try
            {
                // Android：单视图生命周期，MainView（UserControl）作为根视图全屏展示。
                // 不能用 MainWindow（Window）：单视图平台创建 Window 会抛 NotSupportedException。
                singleView.MainView = new MainView
                {
                    DataContext = new MainViewModel(),
                };
                Platform.CrashLog.Boot("App: single-view MainView created");
            }
            catch (System.Exception ex)
            {
                // 主界面构建失败也不能闪退：显示可读的错误页
                Platform.CrashLog.Write("App: create MainView failed", ex, true);
                singleView.MainView = BuildErrorView(singleView, "界面初始化失败", ex.ToString());
            }
        }
#endif

        base.OnFrameworkInitializationCompleted();
    }

#if ANDROID
    /// <summary>构建启动错误页：异常堆栈 + 启动日志 + 「清除并重新加载界面」。</summary>
    private static Control BuildErrorView(
        ISingleViewApplicationLifetime lifetime,
        string title,
        string detail)
    {
        var body = detail + System.Environment.NewLine + System.Environment.NewLine
                 + "==== 启动日志（尾部）====" + System.Environment.NewLine
                 + Platform.CrashLog.ReadRecent();

        var text = new SelectableTextBlock
        {
            Text = title + System.Environment.NewLine + System.Environment.NewLine + body,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontFamily = new Avalonia.Media.FontFamily("Consolas,monospace"),
            FontSize = 12,
            Margin = new Thickness(16),
        };

        var retry = new Button
        {
            Content = "清除崩溃标记并重新加载界面",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Margin = new Thickness(16, 0, 16, 24),
            Padding = new Thickness(16, 8),
        };
        retry.Click += (_, _) =>
        {
            Platform.CrashLog.ClearFlag();
            try
            {
                lifetime.MainView = new MainView { DataContext = new MainViewModel() };
                Platform.CrashLog.Boot("App: retry create MainView ok");
            }
            catch (System.Exception ex)
            {
                Platform.CrashLog.Write("App: retry create MainView failed", ex, true);
            }
        };

        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        var scroller = new ScrollViewer { Content = text };
        grid.Children.Add(scroller);
        Grid.SetRow(retry, 1);
        grid.Children.Add(retry);
        return grid;
    }
#endif
}
