#if ANDROID
using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;

namespace TraeTools.Platform;

/// <summary>
/// 原生崩溃报告页（Android）。
/// 当 Avalonia 尚未启动完成、或已崩溃时，用 Android 原生 UI 展示异常堆栈，
/// 让用户看到的是「可读的错误页」而不是「一闪而过的闪退」：可复制、可重试重启。
/// 使用 DeviceDefault 主题，避免依赖 AppCompat（保证任何阶段都能起来）。
/// </summary>
[Activity(
    Label = "TraeTools 崩溃报告",
    Theme = "@android:style/Theme.DeviceDefault.Light.NoActionBar",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
public class CrashReportActivity : Activity
{
    /// <summary>传递崩溃文本的 Intent extra 键。</summary>
    public const string ExtraText = "crash_text";

    /// <summary>从任意上下文（含 Application）拉起崩溃报告页。</summary>
    public static void Launch(string text)
    {
        try
        {
            var ctx = global::Android.App.Application.Context;
            var intent = new global::Android.Content.Intent(ctx, typeof(CrashReportActivity));
            intent.PutExtra(ExtraText, text);
            intent.AddFlags(global::Android.Content.ActivityFlags.NewTask
                            | global::Android.Content.ActivityFlags.ClearTop
                            | global::Android.Content.ActivityFlags.ClearTask);
            ctx.StartActivity(intent);
        }
        catch
        {
            // 兜底失败也不允许再次抛出（已进入崩溃流程）
        }
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var text = Intent?.GetStringExtra(ExtraText) ?? "(无异常内容)";

        var scroll = new global::Android.Widget.ScrollView(this);
        var root = new global::Android.Widget.LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Vertical
        };
        root.SetPadding(32, 32, 32, 32);

        var title = new global::Android.Widget.TextView(this) { Text = "TraeTools 启动失败" };
        title.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 20f);
        root.AddView(title);

        var hint = new global::Android.Widget.TextView(this)
        {
            Text = "下面是崩溃详情，请截图或复制后反馈给开发者。"
        };
        hint.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 13f);
        hint.SetPadding(0, 8, 0, 12);
        root.AddView(hint);

        var copy = new global::Android.Widget.Button(this) { Text = "复制错误信息" };
        copy.Click += (_, _) =>
        {
            try
            {
                var cm = (global::Android.Content.ClipboardManager?)
                    GetSystemService(global::Android.Content.Context.ClipboardService);
                if (cm != null)
                {
                    var clip = global::Android.Content.ClipData.NewPlainText("TraeTools-crash", text);
                    // 各版本 .NET Android 的 API 形态不一致（PrimaryClip 属性 / SetPrimaryClip 方法），
                    // 用反射兼容，避免编译期绑定失败。
                    var t = cm.GetType();
                    var p = t.GetProperty("PrimaryClip");
                    if (p != null && p.CanWrite)
                    {
                        p.SetValue(cm, clip);
                    }
                    else
                    {
                        var m = t.GetMethod("SetPrimaryClip", new[] { typeof(global::Android.Content.ClipData) });
                        m?.Invoke(cm, new object[] { clip });
                    }
                }
                global::Android.Widget.Toast.MakeText(this, "已复制",
                    global::Android.Widget.ToastLength.Short)?.Show();
            }
            catch
            {
            }
        };
        root.AddView(copy);

        var restart = new global::Android.Widget.Button(this) { Text = "重新启动 TraeTools" };
        restart.Click += (_, _) =>
        {
            try
            {
                StartActivity(new global::Android.Content.Intent(this, typeof(MainActivity)));
                Finish();
            }
            catch
            {
            }
        };
        root.AddView(restart);

        var detail = new global::Android.Widget.TextView(this) { Text = text };
        detail.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 10f);
        detail.SetTextIsSelectable(true);
        detail.SetPadding(0, 16, 0, 0);
        root.AddView(detail);

        scroll.AddView(root);
        SetContentView(scroll);
    }
}
#endif
