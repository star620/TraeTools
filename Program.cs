using Avalonia;
using System;
using System.Threading;

namespace TraeTools;

sealed class Program
{
    /// <summary>单实例互斥名（与项目名一致，避免多实例各自签到/切换冲突）。</summary>
    private const string MutexName = @"Local\TraeTools.SingleInstance";

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    //
    // ⚠️ 整段桌面入口仅在非 Android 目标编译：Android 由 Android.App.Application 入口
    // （见 Platform/MainActivity.cs 的 TraeAndroidApplication）驱动 Avalonia 单视图生命周期，
    // 不使用 Main() 与 StartWithClassicDesktopLifetime（后者由 Avalonia.Desktop 提供，Android 未引用该包）。
#if !ANDROID
    [STAThread]
    public static void Main(string[] args)
    {
        using var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // 已有实例在运行，直接退出本实例（TraeTools 为单实例工具）
            return;
        }
        // 旧版 TraeCheckin / TraeSwitch 数据迁移到统一根（幂等，保留旧目录；Android 为空操作）
        TraeTools.Services.DataPaths.Migrate();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
#endif
}
