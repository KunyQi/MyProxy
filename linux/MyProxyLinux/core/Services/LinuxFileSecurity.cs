using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MyProxy.Services;

/// <summary>
/// Linux 端文件权限与原子写的收口处。
///
/// <para>
/// 规矩只有一条：<b>本程序写下的每一个文件都只有属主可读</b>（0600 文件 / 0700 目录）。
/// 数据根目录下有三样东西对攻击者有价值——设备令牌、缓存的 REALITY 配置、
/// 系统代理快照——它们的保密性全部押在这个权限位上。
/// </para>
/// </summary>
internal static class LinuxFileSecurity
{
    public const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const int OReadOnly = 0;

    /// <summary>open(2) 的 O_DIRECTORY：asm-generic 上是 0o200000，x86-64 与 arm64 同为 65536。</summary>
    private const int ODirectory = 0x10000;

    /// <summary>
    /// 把进程的 umask 收成 0077：此后**任何**新建文件都是 0600、目录都是 0700，
    /// 不必逐个 chmod。
    ///
    /// <para>
    /// 为什么需要它：本程序并不是所有文件都由这里写的。共享的 <c>LogService</c>
    /// （两端同一份源码）用普通的 <c>File.AppendAllText</c> 写 <c>logs/app.log</c>，
    /// 共享的 <c>ConnectionController</c> 写 <c>runtime/config.json</c>——那两个文件里有
    /// 设备 id、错误细节与 REALITY 参数，而在默认 umask 022 下它们是 **0644**
    /// （原生 Linux 冒烟测试实测到的：<c>-rw-r--r--</c>）。逐个 chmod 是打地鼠，
    /// umask 从源头收紧新建文件的默认权限，与 OpenSSH 这类程序的习惯一致。
    /// </para>
    ///
    /// <para>
    /// 只在 Linux 上生效。三个入口在**做任何 I/O 之前**调用它：CLI、守护进程
    /// （两者共用同一条 <c>Main</c>）与托盘 GUI。
    /// </para>
    /// </summary>
    public static void ApplyPrivateUmask()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            // C# 没有八进制字面量：0077 就是 0x3F。
            _ = umask(0x3F);
        }
        catch (Exception)
        {
            // libc 不可用时退回原状：数据目录仍是 0700，由目录兜住。
        }
    }

    /// <summary>建目录（缺哪层建哪层），并把最后两层收成 0700。</summary>
    public static void EnsurePrivateDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        Directory.CreateDirectory(path);

        // 只收自己创建的那一层与它的父层：往上一直 chmod 会把 $HOME、/tmp
        // 这类别人也在用的目录一起改掉。
        string? current = path;
        for (int depth = 0; depth < 2 && !string.IsNullOrEmpty(current); depth++)
        {
            TrySetMode(current, PrivateDirectoryMode, isDirectory: true);
            current = Path.GetDirectoryName(current);
        }
    }

    /// <summary>把文件收成 0600；失败不抛（只读挂载、非属主），由调用方决定要不要在意。</summary>
    public static bool TryRestrict(string path)
        => TrySetMode(path, PrivateFileMode, isDirectory: false);

    /// <summary>读之前核对权限：没有任何 group/other 位才算私密。</summary>
    public static bool IsPrivate(string path)
    {
        try
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            return (mode & ~PrivateFileMode) == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 写入：同目录临时文件 → fsync(文件) → 原子改名 → fsync(目录)。
    ///
    /// <para>
    /// 目录也要 fsync。只 fsync 文件的话，改名这一步可能还留在页缓存里，断电后
    /// 看到的是一个「内容对但名字还是旧的」、甚至两边都没有的目录项。设备令牌丢一次
    /// 只是要重新配对，但系统代理快照丢一次就意味着用户的原始代理设置没了——
    /// 那是 fail-open 顺序里唯一能救命的证据。
    /// </para>
    /// </summary>
    public static void WriteAtomic(string path, string contents)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            EnsurePrivateDirectory(directory);
        }

        string temporary = path + ".tmp";
        byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents);

        using (var stream = new FileStream(
            temporary,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        TrySetMode(temporary, PrivateFileMode, isDirectory: false);
        File.Move(temporary, path, overwrite: true);
        FsyncDirectory(directory);
    }

    /// <summary>把目录项刷到盘上。失败不抛：内容已经写完，改名也已生效。</summary>
    public static void FsyncDirectory(string? directory)
    {
        if (string.IsNullOrEmpty(directory) || !OperatingSystem.IsLinux())
        {
            return;
        }

        int handle = -1;
        try
        {
            handle = Open(directory, OReadOnly | ODirectory);
            if (handle >= 0)
            {
                _ = Fsync(handle);
            }
        }
        catch (Exception)
        {
            // libc 不可用时不做补偿。
        }
        finally
        {
            if (handle >= 0)
            {
                _ = Close(handle);
            }
        }
    }

    private static bool TrySetMode(string path, UnixFileMode mode, bool isDirectory)
    {
        try
        {
            if (isDirectory)
            {
                if (!Directory.Exists(path))
                {
                    return false;
                }

                File.SetUnixFileMode(path, mode);
            }
            else
            {
                if (!File.Exists(path))
                {
                    return false;
                }

                File.SetUnixFileMode(path, mode);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // DllImport 而不是 LibraryImport：后者的源生成器要求 AllowUnsafeBlocks，
    // 而本仓库的两个客户端工程都不开 unsafe（反逆向只是减速带，不是安全边界，
    // 但能不开的能力就不开）。
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int handle);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int handle);

    [DllImport("libc", EntryPoint = "umask", SetLastError = true)]
    private static extern uint umask(uint mask);
}
