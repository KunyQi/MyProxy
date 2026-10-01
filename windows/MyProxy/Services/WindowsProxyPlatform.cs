using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using MyProxy.Models;

namespace MyProxy.Services;

// Isolates OS access so recovery retries can be tested without changing the
// user's proxy settings. Recovery policy and evidence stay in WindowsProxyService.
internal interface IWindowsProxyPlatform
{
    ProxySnapshot ReadSnapshot();
    void WriteSnapshot(ProxySnapshot snapshot);
    void RefreshWinInet();
}

internal sealed class WindowsProxyPlatform : IWindowsProxyPlatform
{
    internal static readonly WindowsProxyPlatform Default = new();
    private const string InternetSettingsKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    // ⚠ 已知缺口：AutoDetect（WPAD）在这里按独立的 DWORD 读写，而它的**权威位置**是
    //   Connections\DefaultConnectionSettings 的标志字节 bit 0x08。某些 Windows 配置下
    //   （CI 的 windows-latest runner 实测如此）WinINET 会在 InternetSetOption 之后把
    //   独立值吸收进 blob 并删除它 —— 于是我们快照里的 AutoDetect 可能读到「不存在」
    //   而不是用户真实的设置。
    //
    //   影响面：仅限快照/还原时对 WPAD 这一项的记录；手动代理（ProxyEnable/ProxyServer）
    //   与 PAC（AutoConfigURL）不受影响，且 RestoreSnapshotOrDisableFallback 对 AutoDetect
    //   取的是 OR，所以最坏情况是「本该关的没被关掉」而不是「本该开的被关掉」——
    //   不会静默覆盖用户的设置。
    //
    //   没有顺手改成读写 blob：那是一份半公开的二进制结构，在代理客户端里写错它的代价
    //   远大于这个缺口本身。要动需要单独立项并配一套真机验证。
    public ProxySnapshot ReadSnapshot()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(InternetSettingsKeyPath, writable: false);
        return new ProxySnapshot
        {
            CapturedAt = DateTimeOffset.UtcNow,
            ProxyEnable = key?.GetValue("ProxyEnable") is int enabled ? enabled : 0,
            ProxyServer = key?.GetValue("ProxyServer") as string ?? "",
            ProxyOverride = key?.GetValue("ProxyOverride") as string ?? "",
            AutoConfigUrl = key?.GetValue("AutoConfigURL") as string ?? "",
            AutoDetect = key?.GetValue("AutoDetect") is int detect && detect != 0
        };
    }

    public void WriteSnapshot(ProxySnapshot snapshot)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(InternetSettingsKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开 Internet Settings 注册表项。");
        key.SetValue("ProxyEnable", snapshot.ProxyEnable, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", snapshot.ProxyServer, RegistryValueKind.String);
        key.SetValue("ProxyOverride", snapshot.ProxyOverride, RegistryValueKind.String);
        key.SetValue("AutoConfigURL", snapshot.AutoConfigUrl, RegistryValueKind.String);
        key.SetValue("AutoDetect", snapshot.AutoDetect ? 1 : 0, RegistryValueKind.DWord);
    }

    public void RefreshWinInet()
    {
        const int settingsChanged = 39;
        const int refresh = 37;
        if (!InternetSetOption(IntPtr.Zero, settingsChanged, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "通知 WinINET 代理设置变更失败。");
        }
        if (!InternetSetOption(IntPtr.Zero, refresh, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "刷新 WinINET 代理设置失败。");
        }
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}
