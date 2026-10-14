#if !ANDROID
using Microsoft.Win32;

namespace TraeCheckin;

/// <summary>
/// 开机自启动（Windows）：通过注册表 HKCU\Software\Microsoft\Windows\CurrentVersion\Run
/// 写入当前程序 exe 的完整路径（带引号）。
/// </summary>
internal static class AutoStartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TraeTools";

    /// <summary>当前进程可执行文件的完整路径。</summary>
    private static string ExecutablePath => Environment.ProcessPath ?? "";

    /// <summary>是否已注册开机自启动。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) != null;
        }
        catch { return false; }
    }

    /// <summary>开启或关闭开机自启动。</summary>
    public static void SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
                key.SetValue(ValueName, $@"""{ExecutablePath}""");
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch { /* 静默失败 */ }
    }
}
#else
namespace TraeCheckin;

/// <summary>
/// Android 空实现：开机自启依赖 Windows 注册表（HKCU\...\Run），Android 上对应的能力是
/// WorkManager / AlarmManager 常驻任务，需要前台服务权限，本工具暂不申请。
/// 移动端自动签到仅在 App 运行期间有效；长期无人值守请使用「云端签到」（GitHub Actions）。
/// </summary>
internal static class AutoStartManager
{
    public static bool IsEnabled() => false;

    public static void SetEnabled(bool enabled) { /* Android 无注册表，空操作 */ }
}
#endif
