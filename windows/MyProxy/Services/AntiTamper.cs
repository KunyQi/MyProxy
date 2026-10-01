using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MyProxy.Services;

/// <summary>
/// 一道**减速带**，不是安全边界。
///
/// <para>
/// 客户端不含任何密钥、Device Token 按设计明文存在客户端，安全性由签名与 HTTPS 这些
/// 密码学属性兜底。因此这里做的只是抬高「随手挂个调试器看看」的门槛，挡不住有决心
/// 的逆向，也不打算挡。
/// </para>
///
/// <para>
/// 只在 Release 构建生效（调用点包在 <c>#if !DEBUG</c> 里），否则开发与测试时自己
/// 都没法调试。检测到调试器就安静退出——不弹窗、不解释，避免给逆向者反馈信号。
/// </para>
/// </summary>
internal static class AntiTamper
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsDebuggerPresent();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CheckRemoteDebuggerPresent(IntPtr hProcess, ref bool isPresent);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>
    /// 检测到调试器就立即退出，否则原样返回。放在 <see cref="System.Windows.Application.OnStartup"/>
    /// 最前面调用。
    /// </summary>
    public static void GuardOrExit()
    {
        if (IsDebuggerAttached())
        {
            // 用非零码退出，且不经过正常关停流程——此刻还没有 xray、没有系统代理改动、
            // 没有单实例锁，直接落幕是干净的。
            Environment.Exit(0x5A);
        }
    }

    private static bool IsDebuggerAttached()
    {
        if (Debugger.IsAttached)
        {
            return true;
        }

        try
        {
            if (IsDebuggerPresent())
            {
                return true;
            }

            bool remote = false;
            if (CheckRemoteDebuggerPresent(GetCurrentProcess(), ref remote) && remote)
            {
                return true;
            }
        }
        catch (DllNotFoundException)
        {
            // 拿不到 kernel32 的极端环境不该让程序起不来——减速带缺一环无所谓。
        }
        catch (EntryPointNotFoundException)
        {
        }

        return false;
    }
}
