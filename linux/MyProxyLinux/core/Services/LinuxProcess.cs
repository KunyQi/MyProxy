namespace MyProxy.Services;

/// <summary>
/// 进程级默认值：必须在**任何 I/O 之前**执行的那几件事。
///
/// <para>
/// 存在的理由是两个可执行文件（<c>myproxy</c> 与 <c>myproxy-gui</c>）在不同的程序集里，
/// 而 <see cref="LinuxFileSecurity"/> 是库内部的东西。这里是公开的接缝，
/// 语义只有一条：<b>在 <c>Main</c> 的第一行调用它</b>。
/// </para>
///
/// <para>
/// 目前只有一件事——把 umask 收成 0077。它必须早于任何文件创建：共享的
/// <c>LogService</c> 与 <c>ConnectionController</c>（两端同一份源码）用的是普通的
/// 文件 API，默认 umask 022 会让 <c>logs/app.log</c> 与 <c>runtime/config.json</c>
/// 落成 0644——那两个文件里有设备 id、错误细节与 REALITY 参数。
/// </para>
/// </summary>
public static class LinuxProcess
{
    /// <summary>设置进程级默认值。幂等，且在没有 libc 时安全退化为空操作。</summary>
    public static void ApplyDefaults() => LinuxFileSecurity.ApplyPrivateUmask();
}
