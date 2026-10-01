using System.IO;
using System.Runtime.InteropServices;

namespace MyProxy.Services;

/// <summary>
/// 安装目录交换：把暂存好的新版本换进安装目录，失败能退回来。
///
/// <para>
/// <b>与 Windows 端的机制不同，因为平台不同。</b>Windows 上运行中的 exe 锁着自己
/// 的目录，所以交换必须由暂存目录里那份「刚验过 Authenticode 的同一份二进制」
/// 在进程退出后执行；Linux 上可以 <c>rename(2)</c> 掉一个正在运行的可执行文件
/// （运行中的进程按 inode 继续跑），所以交换可以在进程内完成，随后用
/// <c>execv(2)</c> 把新版本原地换上——systemd 看到的主 PID 不变，没有重启空窗，
/// 也不需要「等旧进程退出」这种时序约定。
/// </para>
///
/// <para>
/// 三条不变量与 Windows 端一致：
/// </para>
/// <list type="number">
/// <item>交换前必须有备份，且备份路径必须是<b>我们预期的那个</b>（journal 里的
/// 路径不可信：它是磁盘上的一个文件，谁都能改）；</item>
/// <item>暂存目录必须「看起来像一个 MyProxy 安装」，否则宁可什么都不做；</item>
/// <item>暂存目录在数据根下，安装目录可能在另一个挂载点上，跨文件系统的
/// <c>rename</c> 会失败（EXDEV）。所以<b>总是</b>先把暂存内容复制到安装目录同级的
/// <c>.new</c> 目录，再做两次同一父目录内的改名——「一次不原子的复制 + 两次原子的
/// 改名」。不去判断「是不是同一个文件系统」：Linux 上 <c>Path.GetPathRoot</c> 永远是
/// <c>/</c>，按它判断只会一律答「是」，那条复制分支就成了死代码；而多复制一次几十 MB
/// 的代价，比一个在某些机器上永远装不上的更新便宜得多。</item>
/// </list>
/// </summary>
internal static class LinuxUpdateApplier
{
    /// <summary>安装树必须有的东西：主程序与内核目录。</summary>
    private static readonly string[] RequiredEntries = ["myproxy", "Core/xray", "Core/VERSION.txt"];

    public static bool IsPlausibleInstallTree(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        foreach (string relative in RequiredEntries)
        {
            string path = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 安装目录及其父目录必须可写：不可写意味着这是一个系统级安装（/usr、/opt），
    /// 更新应当由发行版的包管理器完成，客户端不能也不该去动它。
    /// </summary>
    public static bool CanSwapDirectory(string installDir)
    {
        try
        {
            string full = Path.GetFullPath(installDir);
            string? parent = Path.GetDirectoryName(full);
            return parent is not null && IsWritable(parent) && Directory.Exists(full);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>把 <paramref name="stagedPath"/> 换进 <paramref name="installDir"/>。</summary>
    public static (bool Ok, string Detail) Swap(string installDir, string stagedPath, string backupPath)
    {
        try
        {
            string install = Path.GetFullPath(installDir).TrimEnd(Path.DirectorySeparatorChar);
            string staged = Path.GetFullPath(stagedPath);
            string backup = Path.GetFullPath(backupPath);

            if (!IsPlausibleInstallTree(staged))
            {
                return (false, "staged tree is not a MyProxy installation");
            }

            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
            }

            // 先把新版本复制到安装目录旁边（与它同一父目录、同一文件系统），
            // 后面两次改名才是原子的。暂存目录本身保持不动，留到确认安装成功后再清。
            string prepared = install + ".new";
            if (Directory.Exists(prepared))
            {
                Directory.Delete(prepared, recursive: true);
            }

            CopyTree(staged, prepared);

            Directory.Move(install, backup);
            try
            {
                Directory.Move(prepared, install);
            }
            catch (Exception ex)
            {
                // 第二步失败：把备份放回去，安装目录绝不能停在「不存在」上。
                try
                {
                    if (!Directory.Exists(install) && Directory.Exists(backup))
                    {
                        Directory.Move(backup, install);
                    }
                }
                catch (Exception restoreEx)
                {
                    return (false, $"swap failed and the backup could not be restored: {restoreEx.Message}");
                }

                DiscardBackup(prepared);
                return (false, ex.Message);
            }

            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static (bool Ok, string Detail) Restore(string installDir, string backupPath)
    {
        try
        {
            string install = Path.GetFullPath(installDir).TrimEnd(Path.DirectorySeparatorChar);
            if (!Directory.Exists(backupPath))
            {
                return (false, "backup is missing");
            }

            if (Directory.Exists(install))
            {
                Directory.Delete(install, recursive: true);
            }

            Directory.Move(backupPath, install);
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static void DiscardBackup(string backupPath)
    {
        try
        {
            if (Directory.Exists(backupPath))
            {
                Directory.Delete(backupPath, recursive: true);
            }
        }
        catch (Exception)
        {
            // 删不掉只是占点磁盘，不影响正确性。
        }
    }

    /// <summary>
    /// 用新版本替换掉当前进程（<c>execv(2)</c>）。
    ///
    /// <para>
    /// 这是 Linux 独有的好事：进程映像被原地换掉，PID 不变，systemd 不会把它当成
    /// 一次崩溃重启，也不需要「先退出、让另一个进程来换目录」那套时序。换完之后
    /// 新进程在启动流程里读到 journal 的 <see cref="UpdateStage.Applied"/> 阶段，
    /// 进入试用期；试用期的确认与「连续起不来就换回旧版本」见
    /// <see cref="LinuxUpdateCoordinator"/>。
    /// </para>
    ///
    /// <para>
    /// 返回 <c>false</c> 表示没换成，调用方应当以非零码退出（systemd 的
    /// <c>Restart=on-failure</c> 会把新版本拉起来，那条路径同样会做恢复判定）。
    /// </para>
    /// </summary>
    public static bool ExecSelf(string installDir, IReadOnlyList<string> arguments)
    {
        string executable = Path.Combine(installDir, "myproxy");
        if (!File.Exists(executable) || !OperatingSystem.IsLinux())
        {
            return false;
        }

        var argv = new List<string> { executable };
        argv.AddRange(arguments);

        try
        {
            // execv 只在失败时返回。argv 用 IntPtr 数组手工封送：
            // 目标进程会接管这一块内存，所以不能用会被 GC 回收的临时数组。
            IntPtr[] pointers = new IntPtr[argv.Count + 1];
            for (int index = 0; index < argv.Count; index++)
            {
                pointers[index] = Marshal.StringToCoTaskMemUTF8(argv[index]);
            }

            pointers[argv.Count] = IntPtr.Zero;
            _ = execv(executable, pointers);
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsWritable(string directory)
    {
        string probe = Path.Combine(directory, $".myproxy-write-probe-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);

            try
            {
                File.SetUnixFileMode(target, File.GetUnixFileMode(file));
            }
            catch (Exception)
            {
                // 模式复制失败时保持默认权限，可执行位由安装器那一侧补。
            }
        }
    }

    [DllImport("libc", EntryPoint = "execv", SetLastError = true)]
    private static extern int execv(string path, IntPtr[] argv);
}
