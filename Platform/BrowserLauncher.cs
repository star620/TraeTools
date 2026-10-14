using System;
using System.Diagnostics;

namespace TraeTools.Platform;

/// <summary>
/// 用系统默认浏览器打开 URL（跨平台封装）。
///
/// <para><b>失败必须可见</b>：早期实现把异常全部吞掉且返回 void，于是 Android 上
/// 「授权 GitHub」按钮点了没反应时，用户与日志都拿不到任何线索。
/// 现在返回 bool，并把失败原因写进平台日志（category = browser）。</para>
///
/// <para><b>Android 侧要点</b>：
/// <list type="bullet">
/// <item>优先用 Activity 启动（<c>MainActivity.Current</c>），Activity 不可用时回退 Application 上下文
///       —— 后者必须带 FLAG_ACTIVITY_NEW_TASK，否则抛 AndroidRuntimeException。</item>
/// <item>部分定制 ROM 上直接 startActivity 会被拦，此时用 <c>Intent.CreateChooser</c> 再试一次。</item>
/// <item>浏览器属于「隐式 Intent 解析」，Android 11+ 的包可见性默认不影响 startActivity，
///       但为兼容个别 ROM 仍在清单里声明了 &lt;queries&gt;（见 Properties/AndroidManifest.xml）。</item>
/// </list></para>
/// </summary>
internal static class BrowserLauncher
{
    /// <summary>打开 URL：成功返回 true；失败返回 false 并已写日志。</summary>
    public static bool Open(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            PlatformLog.Warn("browser", "打开浏览器失败：URL 为空");
            return false;
        }
        try
        {
#if ANDROID
            return OpenOnAndroid(url);
#else
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
#endif
        }
        catch (Exception ex)
        {
            PlatformLog.Warn("browser", $"打开浏览器失败：{ex.GetType().Name}: {ex.Message}（URL={url}）");
            return false;
        }
    }

#if ANDROID
    private static bool OpenOnAndroid(string url)
    {
        var uri = Android.Net.Uri.Parse(url);
        if (uri == null)
        {
            PlatformLog.Warn("browser", $"打开浏览器失败：URL 无法解析（{url}）");
            return false;
        }

        var context = Android.App.Application.Context;
        var activity = TraeTools.MainActivity.Current;

        // ① 首选：Activity 直接启动（无需 NEW_TASK，前台切换最自然）
        if (activity != null)
        {
            try
            {
                var intent = new Android.Content.Intent(Android.Content.Intent.ActionView, uri);
                activity.StartActivity(intent);
                return true;
            }
            catch (Exception ex)
            {
                PlatformLog.Warn("browser", "Activity 启动浏览器失败，回退 Application 上下文：" + ex.Message);
            }
        }

        // ② 回退：Application 上下文（必须 NEW_TASK）
        if (context != null)
        {
            try
            {
                var intent = new Android.Content.Intent(Android.Content.Intent.ActionView, uri);
                intent.AddFlags(Android.Content.ActivityFlags.NewTask);
                context.StartActivity(intent);
                return true;
            }
            catch (Exception ex)
            {
                PlatformLog.Warn("browser", "Application 启动浏览器失败，改用选择器：" + ex.Message);
            }

            // ③ 再回退：让系统弹「用哪个应用打开」，规避个别 ROM 对直接 startActivity 的拦截
            try
            {
                var view = new Android.Content.Intent(Android.Content.Intent.ActionView, uri);
                view.AddFlags(Android.Content.ActivityFlags.NewTask);
                var chooser = Android.Content.Intent.CreateChooser(view, "选择浏览器打开授权页");
                chooser!.AddFlags(Android.Content.ActivityFlags.NewTask);
                context.StartActivity(chooser);
                return true;
            }
            catch (Exception ex)
            {
                PlatformLog.Warn("browser",
                    $"打开浏览器失败（三种方式均未成功）：{ex.GetType().Name}: {ex.Message}（URL={url}）");
                return false;
            }
        }

        PlatformLog.Warn("browser", "打开浏览器失败：Android 上下文不可用（Application.Context 为 null）");
        return false;
    }
#endif
}
