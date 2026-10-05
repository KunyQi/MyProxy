using System.Runtime.Versioning;
using Avalonia;
using MyProxy.Services;

// 本程序集只在 Linux 上跑（它引用的 MyProxy.Linux.Core 也是）。声明出来，
// 平台分析器（CA1416）就不会把「Linux 专有 API」的调用报成可能在别的平台上炸。
//
// 注意这里**不能**写分号：程序集级特性写成 `[assembly: X];` 会被编译器当成
// 顶级语句，紧接着的 file-scoped namespace 就报 CS8956「命名空间必须位于
// 所有其他成员之前」，错误位置指向 namespace 行，看不出真正的原因。
[assembly: SupportedOSPlatform("linux")]

namespace MyProxy.Gui;

/// <summary>
/// 托盘 GUI 的入口。
///
/// <para>
/// 这里刻意什么都不做：不抢单实例锁、不构造服务、不碰控制通道。那些都在
/// <see cref="App"/> 的初始化流程里，因为 Avalonia 的窗口系统此时还不存在——
/// 在 <c>Main</c> 里用对话框报告「已经有一个实例在跑」是一条死路。
/// </para>
///
/// <para>
/// 唯一的例外是 umask：它必须在任何文件被创建之前设好（GUI 会写单实例锁文件与日志），
/// 而那正好是 <c>Main</c> 的位置。
/// </para>
/// </summary>
public static class Program
{
    /// <summary>
    /// Avalonia 的构建入口。名字与签名不能改：Avalonia 的 XAML 预览器
    /// 会按约定反射调用 <c>BuildAvaloniaApp</c>。
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    [STAThread]
    public static int Main(string[] args)
    {
        LinuxProcess.ApplyDefaults();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
}
