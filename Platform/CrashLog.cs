using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace TraeTools.Platform;

/// <summary>
/// 全局崩溃 / 启动轨迹日志。
///
/// <para>Android：写入外部应用目录
/// <c>/storage/emulated/0/Android/data/&lt;包名&gt;/files/crash/</c>（无需 root，手机文件管理器即可查看），
/// 并同步输出 logcat（tag: TraeTools）。</para>
/// <para>Windows：写入 <c>%APPDATA%\TraeTools\logs\crash\</c>。</para>
///
/// <para>闪退排查：把 log_日期.txt 发回即可。另提供崩溃标记（crash_flag）：
/// 上一次运行若崩溃，本次启动直接进入错误页，保证用户至少能看到堆栈，而不是反复闪退。</para>
/// </summary>
public static class CrashLog
{
    private static readonly object LockObj = new();
    private static string? _dir;
    private static bool _installed;

    /// <summary>崩溃标记文件路径（存在即代表上次运行崩溃过）。</summary>
    public static string? FlagPath
    {
        get
        {
            try
            {
                return Path.Combine(TraeTools.Services.DataPaths.Root, "crash_flag");
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>日志文件所在目录（供界面展示）。</summary>
    public static string? LogDirectory => EnsureDir();

    /// <summary>
    /// 注册全局异常捕获。fatalHandler 在 Android 上会额外拉起原生崩溃报告页。
    /// 可重复调用（幂等）。
    /// </summary>
    public static void Install(Action<string>? fatalHandler = null)
    {
        if (_installed) return;
        _installed = true;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Write("AppDomain.UnhandledException", ex, e.IsTerminating);
            TryFatal(fatalHandler, ex);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

#if ANDROID
        Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, e) =>
        {
            Write("AndroidEnvironment.UnhandledExceptionRaiser", e.Exception);
            // 反射设置 Handled=true：阻止 Android 立即杀掉进程，改为弹出可读的崩溃报告页。
            // 用反射是为了兼容不同 .NET Android 版本的事件参数差异（失败也不影响编译/运行）。
            try
            {
                var prop = e.GetType().GetProperty("Handled");
                prop?.SetValue(e, true);
            }
            catch
            {
            }
            TryFatal(fatalHandler, e.Exception);
        };
#endif
    }

    private static void TryFatal(Action<string>? fatalHandler, Exception? ex)
    {
        if (fatalHandler == null) return;
        try
        {
            fatalHandler(ex?.ToString() ?? "(未知异常)");
        }
        catch
        {
        }
    }

    /// <summary>记录启动轨迹（无异常也记，用于定位闪退卡在哪个环节）。</summary>
    public static void Boot(string message)
    {
        try
        {
#if ANDROID
            global::Android.Util.Log.Info("TraeTools", message);
#endif
            Append($"[BOOT {DateTime.Now:HH:mm:ss.fff}] {message}");
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }

    /// <summary>记录异常。</summary>
    public static void Write(string source, Exception? ex, bool terminating = false)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{source}] terminating={terminating} =====");
            sb.AppendLine(ex?.ToString() ?? "(non-exception object)");
            sb.AppendLine();
#if ANDROID
            global::Android.Util.Log.Error("TraeTools", ex?.ToString() ?? source);
#endif
            Append(sb.ToString());
            MarkCrashed(ex);
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>写崩溃标记（内容即异常摘要），供下次启动进入错误页。</summary>
    public static void MarkCrashed(Exception? ex)
    {
        try
        {
            var flag = FlagPath;
            if (string.IsNullOrEmpty(flag)) return;
            var root = Path.GetDirectoryName(flag);
            if (!string.IsNullOrEmpty(root)) Directory.CreateDirectory(root);
            File.WriteAllText(flag,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{ex?.ToString() ?? "(unknown)"}",
                Encoding.UTF8);
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>读取崩溃标记内容（无则返回 null）。</summary>
    public static string? ReadFlag()
    {
        try
        {
            var flag = FlagPath;
            if (string.IsNullOrEmpty(flag) || !File.Exists(flag)) return null;
            return File.ReadAllText(flag, Encoding.UTF8);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>清除崩溃标记（用户确认后恢复正常运行）。</summary>
    public static void ClearFlag()
    {
        try
        {
            var flag = FlagPath;
            if (!string.IsNullOrEmpty(flag) && File.Exists(flag)) File.Delete(flag);
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>读取最近的日志文本（截断到尾部 maxChars 个字符），供错误页展示。</summary>
    public static string ReadRecent(int maxChars = 4000)
    {
        try
        {
            var dir = EnsureDir();
            if (dir == null) return "(日志目录不可用)";
            var file = Path.Combine(dir, $"log_{DateTime.Now:yyyyMMdd}.txt");
            if (!File.Exists(file)) return "(今日无日志)";
            var text = File.ReadAllText(file, Encoding.UTF8);
            if (text.Length > maxChars)
                text = text.Substring(text.Length - maxChars);
            return text;
        }
        catch (Exception ex)
        {
            return "(读取日志失败) " + ex.Message;
        }
    }

    private static void Append(string text)
    {
        lock (LockObj)
        {
            var dir = EnsureDir();
            if (dir == null) return;
            File.AppendAllText(Path.Combine(dir, $"log_{DateTime.Now:yyyyMMdd}.txt"),
                text + Environment.NewLine, Encoding.UTF8);
        }
    }

    private static string? EnsureDir()
    {
        if (_dir != null) return _dir;
        try
        {
#if ANDROID
            // 外部应用专属目录：无需 root，文件管理器 / USB 直接可见
            var ext = Android.App.Application.Context.GetExternalFilesDir(null);
            var baseDir = ext?.AbsolutePath;
            if (string.IsNullOrEmpty(baseDir)) return null;
            _dir = Path.Combine(baseDir, "crash");
#else
            _dir = Path.Combine(TraeTools.Services.DataPaths.Root, "logs", "crash");
#endif
            Directory.CreateDirectory(_dir);
            return _dir;
        }
        catch
        {
            return null;
        }
    }
}
