using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MyProxy.Core;

namespace MyProxy.Gui;

/// <summary>Draw state-colored tray icons and keep their colors aligned with the theme and TrayPresentation.</summary>
public static class TrayIconFactory
{
    /// <summary>托盘图标尺寸。16 在 HiDPI 下会被放大到发虚，32 是各家托盘都能缩好的尺寸。</summary>
    private const int IconSize = 32;

    private static readonly Dictionary<AppState, WindowIcon> Cache = new();

    /// <summary>
    /// 状态对应的颜色。<b>语义与 <see cref="TrayPresentation.StatusLine"/> 的文字一一对应</b>：
    /// 绿色表示成功，红色表示错误，琥珀表示状态转换。
    /// </summary>
    public static Color ColorFor(AppState state) => state switch
    {
        AppState.Connected => Color.Parse("#6D9E86"),        // Color.Success
        AppState.Connecting => Color.Parse("#C08A3E"),       // 过渡态：中性琥珀
        AppState.Disconnecting => Color.Parse("#C08A3E"),    // 同属过渡态，不为它另发明一个颜色
        AppState.Error => Color.Parse("#A85A5E"),            // Color.Error
        _ => Color.Parse("#8A938F")                          // 未绑定 / 已停止：中性灰
    };

    /// <summary>缓存过的状态图标。<b>不转移所有权</b>——调用方只是借去用，别自己 Dispose。</summary>
    public static WindowIcon IconFor(AppState state)
    {
        if (Cache.TryGetValue(state, out WindowIcon? icon))
        {
            return icon;
        }

        // 只有「正在中途」和「已连接」才带外晕：静止状态发光会看起来像还在做事。
        bool active = state is AppState.Connecting or AppState.Disconnecting or AppState.Connected;
        icon = RenderIcon(ColorFor(state), active);
        Cache[state] = icon;
        return icon;
    }

    /// <summary>窗口与托盘共用同一个渲染器，两处的圆点必须长得一样。</summary>
    public static WindowIcon RenderIcon(Color color, bool glow)
    {
        var target = new RenderTargetBitmap(new PixelSize(IconSize, IconSize), new Vector(96, 96));

        // 画在透明底上：托盘的背景由桌面决定，填一块底色会在浅色面板上留下一个方块。
        //
        // 用 CreateDrawingContext 而不是 Render(visual)：RenderTargetBitmap.Render 收的是
        // 一个 **Visual**，不是一个绘制回调（写初稿时按「回调式」写的，第一次真实编译才发现）。
        // 拿到 DrawingContext 后 `using` 结束即把这一帧提交上去。
        using (DrawingContext context = target.CreateDrawingContext())
        {
            double center = IconSize / 2.0;

            if (glow)
            {
                // 一圈很淡的外晕，让圆点在深浅两种面板上都浮得起来。透明度压得很低：
                context.DrawEllipse(
                    new SolidColorBrush(color, 0.22),
                    null,
                    new Point(center, center),
                    center - 0.5,
                    center - 0.5);
            }

            // 外圈状态色 + 内圈半透明白：深浅面板上都看得清边界，
            // 又不至于把圆点画成一个实心色块。
            context.DrawEllipse(
                new SolidColorBrush(color),
                null,
                new Point(center, center),
                center - 3,
                center - 3);
            context.DrawEllipse(
                new SolidColorBrush(Colors.White, 0.35),
                null,
                new Point(center, center),
                center - 7,
                center - 7);
        }

        // WindowIcon 构造时就把像素拷进了平台实现，而 X11 的 IPlatformIconLoader
        // 顺手把传进去的位图 Dispose 掉了——所以这里不需要（也没法）再管位图的生命周期。
        // WindowIcon 自身没有实现 IDisposable，这不是我们能释放的东西。
        return new WindowIcon(target);
    }

    /// <summary>
    /// 创建托盘图标与操作菜单。
    /// <b>「退出」与「断开并退出」是两件事</b>：
    /// 前者只关掉这个窗口程序（连接仍由守护进程持有，网络不受影响），后者先发 <c>stop</c> 再关。
    ///
    /// <para>
    /// 菜单文字在创建时就定下来，之后<b>不再改</b>：Linux 的托盘菜单经 StatusNotifier 的
    /// DBusMenu 导出，Avalonia 11 对已导出项的属性更新支持有限，改文字或禁用态可能不生效。
    /// 图标本身走的是另一条路径（可以随时换），所以「当前状态」靠图标颜色与悬停提示表达，
    /// 不靠菜单文字。
    /// </para>
    /// </summary>
    /// <summary>
    /// 状态图标与悬停文字。
    ///
    /// <para>
    /// 菜单本身不在这里建：它归 <see cref="LinuxTrayService"/>，因为菜单是有行为的
    /// （点了要发命令、要开关「停止代理」），而这里只管「长什么样」。
    /// 这个划分与 Windows 端一致——那边也是 <c>TrayService</c> 管菜单、
    /// <c>TrayPresentation</c>（共享平面）管状态文案。
    /// </para>
    /// </summary>
    public static string ToolTipFor(AppState state, ProxyMode mode)
        => $"{AppInfo.ProductName} {TrayPresentation.StatusLine(state, mode)}";

    /// <summary>
    /// 释放缓存里的状态图标。进程退出前调用。
    ///
    /// <para>
    /// 这里只是把字典清空：<see cref="WindowIcon"/> 没有实现 <c>IDisposable</c>，
    /// 而它内部的像素在构造时就已由平台图标加载器拷走。清空的目的是别让
    /// 「进程要退出了还攥着四个图标」这件事留下悬念，不是因为有东西必须释放。
    /// </para>
    /// </summary>
    public static void ReleaseCache() => Cache.Clear();
}
