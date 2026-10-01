using System.Diagnostics;
using System.IO;
using MyProxy.Core;

namespace MyProxy.Services;

/// <summary>目录交换的结果。</summary>
public sealed record SwapOutcome(bool Ok, bool RolledBack, string Detail = "");

/// <summary>
/// 把暂存好的新版本换到安装目录，失败则回滚。
///
/// <para>
/// <b>为什么需要一个独立进程来做这件事：</b>正在运行的 <c>MyProxy.exe</c> 锁着
/// 自己所在的目录，任何进程都无法替换它。所以交换由<b>暂存目录里那个已经
/// 验过签的 MyProxy.exe</b> 带 <c>--apply-update</c> 拉起、等旧进程退出后执行。
/// 用的是刚刚验过 Authenticode 的同一份二进制，不是下载来的脚本、也不是外部
/// 工具——Update Plane 的整条约束就是不引入任何未签名的可执行路径。
/// </para>
///
/// <para>
/// 交换顺序刻意是「先把旧的移走做备份，再把新的移进来」。中间任何一步失败
/// 都能靠备份把安装目录整个还原；如果反过来先删旧的，失败时就只剩一个空目录
/// 和无处可回的状态。这与 <c>RollbackAsync</c> 先恢复系统代理再停 xray 是同一
/// 种排序思路：把「还原得回去」排在「看起来更干净」前面。
/// </para>
/// </summary>
public static class UpdateApplier
{
    public const string ApplySwitch = "--apply-update";
    public const string ApplyFileSwitch = "--apply-update-file";

    /// <summary>等待父进程退出的上限。超时就不动手——半个交换比不交换糟得多。</summary>
    public static readonly TimeSpan ParentExitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 执行交换。<paramref name="installDir"/> 必须已经没有进程占用。
    /// </summary>
    public static SwapOutcome Swap(string installDir, string stagedDir, string backupDir)
    {
        if (!ValidDirectoryPaths(installDir, stagedDir, backupDir) ||
            !CanSwapDirectory(installDir, stagedDir) ||
            (Directory.Exists(backupDir) && !CanSwapDirectory(backupDir, stagedDir)))
        {
            return new SwapOutcome(false, false, "installation directory contains unowned files or links");
        }

        if (!Directory.Exists(stagedDir))
        {
            return new SwapOutcome(false, false, "staged directory is missing");
        }

        bool movedAway = false;
        try
        {
            if (Directory.Exists(backupDir))
            {
                Directory.Delete(backupDir, recursive: true);
            }

            if (Directory.Exists(installDir))
            {
                Directory.Move(installDir, backupDir);
                movedAway = true;
            }

            Directory.Move(stagedDir, installDir);
            return new SwapOutcome(true, false);
        }
        catch (Exception ex)
        {
            if (!movedAway)
            {
                // 安装目录还没被动过，什么都不用还原。
                return new SwapOutcome(false, false, ex.GetType().Name);
            }

            try
            {
                if (Directory.Exists(installDir))
                {
                    Directory.Delete(installDir, recursive: true);
                }

                Directory.Move(backupDir, installDir);
                return new SwapOutcome(false, true, ex.GetType().Name);
            }
            catch (Exception restoreFailure)
            {
                // 这是最坏的一档：新的没装上、旧的也没回来。日志必须说清楚
                // 备份还躺在哪里，否则用户唯一能做的就是重装。
                return new SwapOutcome(
                    false,
                    false,
                    $"{ex.GetType().Name}; restore failed: {restoreFailure.GetType().Name}; backup at {backupDir}");
            }
        }
    }

    /// <summary>把备份还原回安装目录。用于上次交换被打断后的启动恢复。</summary>
    public static SwapOutcome Restore(string installDir, string backupDir)
    {
        if (!ValidDirectoryPaths(installDir, backupDir))
        {
            return new SwapOutcome(false, false, "invalid directory update paths");
        }
        // A backup created by Swap is a complete, recognizable application tree.
        // Never delete the target directory on the strength of a journal path alone.
        if (Directory.Exists(backupDir) && !CanSwapDirectory(backupDir, installDir, allowMissingStage: true))
        {
            return new SwapOutcome(false, false, "backup is not a managed application directory");
        }
        if (Directory.Exists(installDir) && !CanSwapDirectory(installDir, backupDir, allowMissingStage: true))
        {
            return new SwapOutcome(false, false, "installation directory contains unowned files or links");
        }

        if (!Directory.Exists(backupDir))
        {
            // 没有备份说明交换根本没走到移动那一步，安装目录还是完整的。
            return new SwapOutcome(true, false, "no backup to restore");
        }

        try
        {
            if (Directory.Exists(installDir))
            {
                Directory.Delete(installDir, recursive: true);
            }

            Directory.Move(backupDir, installDir);
            return new SwapOutcome(true, true);
        }
        catch (Exception ex)
        {
            return new SwapOutcome(false, false, ex.GetType().Name);
        }
    }

    /// <summary>安装成功后清掉备份。失败无所谓——它只是占点磁盘。</summary>
    public static void DiscardBackup(string backupDir)
    {
        try
        {
            if (Directory.Exists(backupDir))
            {
                Directory.Delete(backupDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Only a verified portable executable is exchanged; neighboring downloads are untouched.</summary>
    public static SwapOutcome SwapExecutable(string installExe, string stagedExe, string backupExe)
    {
        if (!ValidExecutablePaths(installExe, stagedExe, backupExe) ||
            !File.Exists(installExe) || !File.Exists(stagedExe) || File.Exists(backupExe) ||
            !IsSingleExecutableStage(Path.GetDirectoryName(stagedExe)!))
        {
            return new SwapOutcome(false, false, "invalid portable update paths or package");
        }

        bool movedAway = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(backupExe)!);
            File.Move(installExe, backupExe);
            movedAway = true;
            // The helper is running from stagedExe. Windows keeps that file
            // open, so copy its bytes after the old process has exited.
            File.Copy(stagedExe, installExe);
            return new SwapOutcome(true, false);
        }
        catch (Exception ex)
        {
            if (!movedAway) return new SwapOutcome(false, false, ex.GetType().Name);
            try
            {
                if (File.Exists(installExe)) File.Delete(installExe);
                File.Move(backupExe, installExe);
                return new SwapOutcome(false, true, ex.GetType().Name);
            }
            catch (Exception restoreFailure)
            {
                return new SwapOutcome(false, false,
                    $"{ex.GetType().Name}; restore failed: {restoreFailure.GetType().Name}; backup at {backupExe}");
            }
        }
    }

    public static SwapOutcome RestoreExecutable(string installExe, string backupExe)
    {
        if (!ValidExecutablePaths(installExe, backupExe) || !File.Exists(backupExe))
        {
            return new SwapOutcome(!File.Exists(backupExe), false,
                File.Exists(backupExe) ? "invalid portable update paths" : "no backup to restore");
        }
        try
        {
            if (File.Exists(installExe)) File.Delete(installExe);
            File.Move(backupExe, installExe);
            return new SwapOutcome(true, true);
        }
        catch (Exception ex)
        {
            return new SwapOutcome(false, false, ex.GetType().Name);
        }
    }

    public static void DiscardExecutableBackup(string backupExe)
    {
        try { if (File.Exists(backupExe)) File.Delete(backupExe); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static bool IsSingleExecutableStage(string stagedDir)
    {
        try
        {
            return Directory.Exists(stagedDir) &&
                Directory.EnumerateFileSystemEntries(stagedDir).All(path =>
                    Path.GetFileName(path).Equals("MyProxy.exe", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(path) &&
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) &&
                File.Exists(Path.Combine(stagedDir, "MyProxy.exe"));
        }
        catch { return false; }
    }

    private static bool ValidExecutablePaths(params string[] paths)
    {
        try
        {
            if (paths.Any(path => !Path.IsPathFullyQualified(path) ||
                !Path.GetFileName(path).Equals("MyProxy.exe", StringComparison.OrdinalIgnoreCase))) return false;
            return paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == paths.Length;
        }
        catch { return false; }
    }

    private static bool ValidDirectoryPaths(params string[] paths)
    {
        try
        {
            string[] full = paths.Select(Path.GetFullPath)
                .Select(path => Path.TrimEndingDirectorySeparator(path)).ToArray();
            if (paths.Any(path => !Path.IsPathFullyQualified(path))) return false;
            for (int i = 0; i < full.Length; i++)
            for (int j = i + 1; j < full.Length; j++)
            {
                if (full[i].Equals(full[j], StringComparison.OrdinalIgnoreCase) ||
                    full[i].StartsWith(full[j] + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    full[j].StartsWith(full[i] + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>Legacy directory updates are allowed only for a complete app tree with no extra entries.</summary>
    public static bool CanSwapDirectory(string installDir, string stagedDir, bool allowMissingStage = false)
    {
        try
        {
            if (!Directory.Exists(installDir) ||
                (File.GetAttributes(installDir) & FileAttributes.ReparsePoint) != 0 ||
                !File.Exists(Path.Combine(installDir, "MyProxy.exe")) ||
                !File.Exists(Path.Combine(installDir, "MyProxy.dll")) ||
                !File.Exists(Path.Combine(installDir, "MyProxy.deps.json")) ||
                !File.Exists(Path.Combine(installDir, "MyProxy.runtimeconfig.json")) ||
                !File.Exists(Path.Combine(installDir, "Core", "xray.exe"))) return false;

            // The stage may have gained files, but everything being removed must be
            // present in the staged package. This rejects a Downloads directory.
            if (!allowMissingStage && !Directory.Exists(stagedDir)) return false;
            foreach (string path in Directory.EnumerateFileSystemEntries(installDir, "*", SearchOption.AllDirectories))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
                if (!allowMissingStage && !File.Exists(Path.Combine(stagedDir, Path.GetRelativePath(installDir, path))) &&
                    !Directory.Exists(Path.Combine(stagedDir, Path.GetRelativePath(installDir, path)))) return false;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 解析 <c>--apply-update &lt;installDir&gt; &lt;stagedDir&gt; &lt;backupDir&gt; &lt;pid&gt;</c>。
    /// 参数不完整就返回 false：宁可什么都不做，也不要按猜出来的路径去移动目录。
    /// </summary>
    public static bool TryParseArgs(
        string[] args,
        out string installDir,
        out string stagedDir,
        out string backupDir,
        out int parentPid)
    {
        installDir = stagedDir = backupDir = "";
        parentPid = 0;

        int index = Array.IndexOf(args, ApplySwitch);
        if (index < 0 || args.Length < index + 5)
        {
            return false;
        }

        installDir = args[index + 1];
        stagedDir = args[index + 2];
        backupDir = args[index + 3];
        return int.TryParse(args[index + 4], out parentPid)
            && installDir.Length > 0
            && stagedDir.Length > 0
            && backupDir.Length > 0;
    }

    public static bool TryParseFileArgs(string[] args, out string installExe,
        out string stagedExe, out string backupExe, out int parentPid)
    {
        installExe = stagedExe = backupExe = "";
        parentPid = 0;
        int index = Array.IndexOf(args, ApplyFileSwitch);
        if (index < 0 || args.Length != index + 5) return false;
        installExe = args[index + 1];
        stagedExe = args[index + 2];
        backupExe = args[index + 3];
        return int.TryParse(args[index + 4], out parentPid) &&
            ValidExecutablePaths(installExe, stagedExe, backupExe);
    }

    /// <summary>等旧进程退出。它已经退出或者根本不存在时立即返回 true。</summary>
    public static bool WaitForParentExit(int pid, TimeSpan timeout)
    {
        if (pid <= 0)
        {
            return true;
        }

        try
        {
            using Process parent = Process.GetProcessById(pid);
            return parent.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch (ArgumentException)
        {
            // 进程已经没了，正是我们要等的结果。
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}
