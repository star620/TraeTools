namespace TraeTools.Views;

/// <summary>
/// 对话框视图（UserControl）的标题契约。
///
/// 桌面端由 <see cref="DialogHostWindow"/> 承载对话框时需要一个窗口标题。
/// 这里用接口而不是反射（<c>GetType().GetProperty("DialogTitle")</c>）读取：
/// 本项目开启了 full 裁剪与 NativeAOT 分析，反射读属性会产生 IL2075 警告，
/// 且裁剪后属性可能被移除导致标题静默丢失；接口调用在编译期确定，两者都不会发生。
/// </summary>
public interface IDialogView
{
    /// <summary>对话框标题；为空时宿主窗口沿用默认标题。</summary>
    string? DialogTitle { get; }
}
