using System;
using System.IO;

namespace TraeTools.Platform;

/// <summary>
/// 平台层的最小日志出口。
///
/// 页面级日志走 <c>ViewModels.AccountHelpers.AppLog</c>（按业务分类归档），
/// 但 <c>Platform/</c> 下的代码属于更底层的设施（加解密、浏览器跳转、崩溃捕获），
/// 不应反向依赖 ViewModels，因此在这里提供一个同格式、同目录的写入点：
/// 文件名与格式与页面日志保持一致（<c>{category}_log_yyyyMM.txt</c>），
/// 打开日志目录时能看到完整的一批记录。
/// </summary>
internal static class PlatformLog
{
    private static readonly object Lock = new();

    public static void Info(string category, string message) => Write(category, "INFO", message);

    public static void Warn(string category, string message) => Write(category, "WARN", message);

    public static void Error(string category, string message) => Write(category, "ERROR", message);

    private static void Write(string category, string level, string message)
    {
        try
        {
            lock (Lock)
            {
                var file = Path.Combine(
                    TraeTools.Services.DataPaths.LogsDir,
                    $"{category}_log_{DateTime.Now:yyyyMM}.txt");
                File.AppendAllText(file,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志写入失败绝不能影响主流程（尤其是崩溃与加解密路径）
        }
    }
}
