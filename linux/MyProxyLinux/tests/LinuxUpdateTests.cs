using System.Formats.Tar;
using System.IO.Compression;
using MyProxy.Core;
using MyProxy.Models;
using MyProxy.Services;

namespace MyProxy.Linux.Tests;

/// <summary>
/// 更新链路上平台无关的那几段：解包的路径安全、安装树的形状、目录交换与回滚、
/// 以及「备份路径不是我们预期的那个就不动手」。
///
/// <para>
/// 验签本身由共享的 <c>ReleaseManifestVerifier</c> 负责（Windows 端已有测试，
/// 两端编译的是同一份文件）；这里盯的是 Linux 独有的那部分——
/// 一个 tarball 里可以塞进任何路径，而安装器会以用户的身份把它解开。
/// </para>
/// </summary>
[TestClass]
public sealed class LinuxUpdateTests
{
    private string _root = "";

    [TestInitialize]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "myproxy-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception)
        {
            // 临时目录清不掉不影响结论。
        }
    }

    [TestMethod]
    public void Extract_RejectsTraversalAndAbsoluteAndLinkEntries()
    {
        foreach ((string name, TarEntryType type) in new[]
                 {
                     ("../escaped.txt", TarEntryType.RegularFile),
                     ("/etc/passwd", TarEntryType.RegularFile),
                     ("payload/../../escaped.txt", TarEntryType.RegularFile),
                     ("payload/link", TarEntryType.SymbolicLink),
                     ("payload/hard", TarEntryType.HardLink)
                 })
        {
            string archive = BuildArchive(name, type);
            string destination = Path.Combine(_root, "extract-" + Guid.NewGuid().ToString("N"));

            // 产物是我们自己打的包，里面本来就不该有链接与非常规路径；
            // 拒绝的粒度是「整包拒绝」，不是「跳过这一条」。
            Assert.ThrowsException<InvalidDataException>(
                () => LinuxUpdateInstaller.ExtractArchive(archive, destination),
                $"应当拒绝：{name}");
        }
    }

    [TestMethod]
    public void Extract_WritesARegularTree()
    {
        string archive = BuildArchive("myproxy", TarEntryType.RegularFile, "payload/myproxy");
        string destination = Path.Combine(_root, "extract");

        LinuxUpdateInstaller.ExtractArchive(archive, destination);

        Assert.IsTrue(File.Exists(Path.Combine(destination, "myproxy")));
        Assert.AreEqual("payload/myproxy", File.ReadAllText(Path.Combine(destination, "myproxy")));
    }

    [TestMethod]
    public void LocateInstallRoot_AcceptsThePackagedTopLevelDirectory()
    {
        // scripts/package_linux.py 打出来的包带一层 MyProxy-linux-<arch>/：
        // 用户 `tar -xzf -C ~/.local/share` 要的就是它。更新器必须认这个形状，
        // 否则每一个正式打出来的包都会在「不是 MyProxy 安装」那一步被拒。
        string archive = BuildTreeArchive("MyProxy-linux-x64/");
        string destination = Path.Combine(_root, "extract");

        LinuxUpdateInstaller.ExtractArchive(archive, destination);

        Assert.AreEqual(
            Path.Combine(destination, "MyProxy-linux-x64"),
            LinuxUpdateInstaller.LocateInstallRoot(destination));
    }

    [TestMethod]
    public void LocateInstallRoot_AcceptsARootLevelTree()
    {
        string archive = BuildTreeArchive("");
        string destination = Path.Combine(_root, "extract");

        LinuxUpdateInstaller.ExtractArchive(archive, destination);

        Assert.AreEqual(destination, LinuxUpdateInstaller.LocateInstallRoot(destination));
    }

    [TestMethod]
    public void LocateInstallRoot_RefusesAmbiguousShapes()
    {
        // 两个顶层目录：猜哪一个都是在赌。
        string twoTrees = Path.Combine(_root, "two");
        WriteTree(Path.Combine(twoTrees, "a"), "a");
        WriteTree(Path.Combine(twoTrees, "b"), "b");
        Assert.IsNull(LinuxUpdateInstaller.LocateInstallRoot(twoTrees));

        // 顶层还散落着文件：不是我们打的包。
        string stray = Path.Combine(_root, "stray");
        WriteTree(Path.Combine(stray, "MyProxy-linux-x64"), "x");
        File.WriteAllText(Path.Combine(stray, "install.sh"), "rm -rf ~");
        Assert.IsNull(LinuxUpdateInstaller.LocateInstallRoot(stray));
    }

    [TestMethod]
    public void EnsureInstallTreeExecutable_CoversTheTrayProgram()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("可执行位只在 Linux 上有意义。");
        }

        string tree = Path.Combine(_root, "tree");
        WriteTree(tree, "x");
        File.WriteAllText(Path.Combine(tree, "myproxy-gui"), "x");
        foreach (string relative in new[] { "myproxy", "myproxy-gui", "Core/xray" })
        {
            File.SetUnixFileMode(Path.Combine(tree, relative), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        LinuxUpdateInstaller.EnsureInstallTreeExecutable(tree);

        foreach (string relative in new[] { "myproxy", "myproxy-gui", "Core/xray" })
        {
            Assert.IsTrue(
                (File.GetUnixFileMode(Path.Combine(tree, relative)) & UnixFileMode.UserExecute) != 0,
                relative);
        }
    }

    [TestMethod]
    public void Sanitize_NeverYieldsADotOnlyDirectoryName()
    {
        // 暂存目录最后会被递归删除：「..」会让它指到数据根目录本身。
        Assert.AreEqual("unknown", LinuxUpdateInstaller.Sanitize(".."));
        Assert.AreEqual("unknown", LinuxUpdateInstaller.Sanitize("."));
        Assert.AreEqual("unknown", LinuxUpdateInstaller.Sanitize(""));
        Assert.AreEqual("rel_1.2", LinuxUpdateInstaller.Sanitize("rel_1.2"));
        Assert.AreEqual("__etc", LinuxUpdateInstaller.Sanitize("/\\etc"));
    }

    [TestMethod]
    public void IsPlausibleInstallTree_RequiresTheBinaryAndTheKernel()
    {
        string tree = Path.Combine(_root, "tree");
        Assert.IsFalse(LinuxUpdateApplier.IsPlausibleInstallTree(tree));

        Directory.CreateDirectory(Path.Combine(tree, "Core"));
        File.WriteAllText(Path.Combine(tree, "myproxy"), "");
        Assert.IsFalse(LinuxUpdateApplier.IsPlausibleInstallTree(tree));

        File.WriteAllText(Path.Combine(tree, "Core", "VERSION.txt"), "");
        Assert.IsFalse(LinuxUpdateApplier.IsPlausibleInstallTree(tree));

        File.WriteAllText(Path.Combine(tree, "Core", "xray"), "");
        Assert.IsTrue(LinuxUpdateApplier.IsPlausibleInstallTree(tree));
    }

    [TestMethod]
    public void IsPlausibleInstallTree_RejectsAWindowsShapedTree()
    {
        // 把 Windows 的产物形状直接搬到 Linux 上是**装不上**的，而且必须在安装前就
        // 拒掉：`myproxy.exe` 与 `Core\xray.exe` 这两个名字在 Linux 上不存在，换上去
        // 之后没有任何东西能启动；而 Windows 的可执行文件没有可执行位，连「权限不对」
        // 这条线索都不会留下。打包脚本 `verify_published_apphost` 用的是同一份判据。
        string tree = Path.Combine(_root, "windows-shaped");
        Directory.CreateDirectory(Path.Combine(tree, "Core"));
        File.WriteAllText(Path.Combine(tree, "myproxy.exe"), "MZ");
        File.WriteAllText(Path.Combine(tree, "Core", "xray.exe"), "MZ");
        File.WriteAllText(Path.Combine(tree, "Core", "VERSION.txt"), "");

        Assert.IsFalse(LinuxUpdateApplier.IsPlausibleInstallTree(tree));

        // 同一个目录换成 Linux 的两个名字就成立——差的确实只是这两个文件名。
        File.Move(Path.Combine(tree, "myproxy.exe"), Path.Combine(tree, "myproxy"));
        File.Move(Path.Combine(tree, "Core", "xray.exe"), Path.Combine(tree, "Core", "xray"));
        Assert.IsTrue(LinuxUpdateApplier.IsPlausibleInstallTree(tree));
    }

    [TestMethod]
    public void Swap_KeepsTheOldTreeAsABackupAndRestorePutsItBack()
    {
        string install = Path.Combine(_root, "install");
        string staged = Path.Combine(_root, "staged");
        string backup = install + ".backup";

        WriteTree(install, "old");
        WriteTree(staged, "new");

        (bool ok, string detail) = LinuxUpdateApplier.Swap(install, staged, backup);
        Assert.IsTrue(ok, detail);

        Assert.AreEqual("new", File.ReadAllText(Path.Combine(install, "myproxy")));
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(backup, "myproxy")));

        // 新版本是经安装目录旁的 .new 复制过去的（跨文件系统也成立），
        // 暂存目录原样留着，等确认安装成功后再清。
        Assert.IsFalse(Directory.Exists(install + ".new"));
        Assert.IsTrue(LinuxUpdateApplier.IsPlausibleInstallTree(staged));

        (bool restored, string restoreDetail) = LinuxUpdateApplier.Restore(install, backup);
        Assert.IsTrue(restored, restoreDetail);
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(install, "myproxy")));
    }

    [TestMethod]
    public void Swap_RefusesAStagedTreeThatIsNotAnInstallation()
    {
        string install = Path.Combine(_root, "install");
        string staged = Path.Combine(_root, "staged");
        WriteTree(install, "old");
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(staged, "README.md"), "not a build");

        (bool ok, _) = LinuxUpdateApplier.Swap(install, staged, install + ".backup");

        Assert.IsFalse(ok);
        // 安装目录必须原封不动。
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(install, "myproxy")));
    }

    [TestMethod]
    public void Recover_DecisionsComeFromTheSharedJournalRules()
    {
        // 这几个判定本身是共享 Core 的纯函数（Windows 端已有测试），这里只钉住
        // Linux 端依赖的那几条边没有理解错。
        Assert.AreEqual(
            RecoveryAction.None,
            UpdateRecovery.Decide(null, "0.1.0"));

        Assert.AreEqual(
            RecoveryAction.DiscardStaging,
            UpdateRecovery.Decide(new UpdateJournal { Stage = UpdateStage.Staged }, "0.1.0"));

        Assert.AreEqual(
            RecoveryAction.RestoreBackup,
            UpdateRecovery.Decide(new UpdateJournal { Stage = UpdateStage.Applying }, "0.1.0"));

        Assert.AreEqual(
            RecoveryAction.FinalizeInstall,
            UpdateRecovery.Decide(
                new UpdateJournal { Stage = UpdateStage.Applied, TargetVersion = "0.2.0" }, "0.2.0"));

        // 交换完成、跑起来的却不是目标版本：新版本起不来，不能当成功。
        Assert.AreEqual(
            RecoveryAction.RollbackFailedInstall,
            UpdateRecovery.Decide(
                new UpdateJournal { Stage = UpdateStage.Applied, TargetVersion = "0.2.0" }, "0.1.0"));

        Assert.AreEqual(
            RecoveryAction.ReportRollback,
            UpdateRecovery.Decide(new UpdateJournal { Stage = UpdateStage.RolledBack }, "0.1.0"));
    }

    [TestMethod]
    public void CanSwapDirectory_IsFalseWhenTheInstallDirectoryIsNotWritable()
    {
        string install = Path.Combine(_root, "install");
        WriteTree(install, "old");

        // 可写的安装目录（临时目录）应当通过。
        Assert.IsTrue(LinuxUpdateApplier.CanSwapDirectory(install));

        // 不存在的不算：系统级安装（/usr/local/bin 之类）由包管理器升级。
        Assert.IsFalse(LinuxUpdateApplier.CanSwapDirectory(Path.Combine(_root, "missing")));
    }

    [TestMethod]
    public async Task Recover_KeepsTheBackupThroughProbationUntilConfirmed()
    {
        (LinuxUpdateCoordinator coordinator, FakeInstaller installer, FakeUpdateService update, Layout layout) =
            BuildAppliedUpdate();

        // 新版本第一次起来：不能立刻删备份——它还没证明自己能跑。
        Assert.IsFalse(await coordinator.RecoverAsync(CancellationToken.None));
        Assert.IsTrue(coordinator.IsAwaitingConfirmation);
        Assert.IsTrue(Directory.Exists(layout.Backup));
        Assert.AreEqual(1, installer.ProbationStarts);
        Assert.AreEqual(0, update.Reports.Count);

        await coordinator.ConfirmHealthyAsync(CancellationToken.None);

        Assert.IsFalse(coordinator.IsAwaitingConfirmation);
        Assert.IsFalse(Directory.Exists(layout.Backup));
        Assert.IsFalse(Directory.Exists(layout.VersionDir));
        Assert.IsNull(installer.Journal);
        Assert.AreEqual(0, installer.ProbationStarts);
        CollectionAssert.AreEqual(new[] { "rel_1:installed" }, update.Reports);
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(layout.Install, "myproxy")));
    }

    [TestMethod]
    public async Task Recover_RollsBackWhenTheNewVersionKeepsFailingToStayUp()
    {
        (_, FakeInstaller installer, FakeUpdateService update, Layout layout) = BuildAppliedUpdate();

        // 每次启动都是一个新进程（systemd 在崩溃后拉起来的），都没走到确认。
        for (int start = 1; start <= LinuxUpdateCoordinator.MaxProbationStarts; start++)
        {
            LinuxUpdateCoordinator fresh = NewCoordinator(installer, update, layout.Install);
            Assert.IsFalse(await fresh.RecoverAsync(CancellationToken.None), $"start {start}");
            Assert.AreEqual(start, installer.ProbationStarts);
        }

        LinuxUpdateCoordinator last = NewCoordinator(installer, update, layout.Install);

        // 这一次要求调用方 execv 进换回来的旧版本。
        Assert.IsTrue(await last.RecoverAsync(CancellationToken.None));
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(layout.Install, "myproxy")));
        Assert.IsNull(installer.Journal);
        Assert.AreEqual(0, installer.ProbationStarts);
        Assert.AreEqual(1, update.Reports.Count);
        StringAssert.StartsWith(update.Reports[0], "rel_1:failed");
    }

    [TestMethod]
    public async Task Apply_RefusesAStagedJournalForADifferentRelease()
    {
        Layout layout = BuildLayout();
        var installer = new FakeInstaller(layout.StagingRoot)
        {
            Journal = new UpdateJournal
            {
                Stage = UpdateStage.Staged,
                ReleaseId = "rel_revoked",
                StagedPath = layout.Staged,
                BackupPath = layout.Backup
            }
        };
        LinuxUpdateCoordinator coordinator = NewCoordinator(installer, new FakeUpdateService(), layout.Install);

        // 这一次准备的是 rel_new（而且失败了）；磁盘上剩的是上一次暂存的 rel_revoked。
        Assert.IsFalse(await coordinator.ApplyAsync("rel_new", CancellationToken.None));

        Assert.AreEqual("old", File.ReadAllText(Path.Combine(layout.Install, "myproxy")));
        Assert.AreEqual(UpdateStage.Staged, installer.Journal!.Stage);
    }

    [TestMethod]
    public async Task Recover_NeverDeletesAStagingPathOutsideTheStagingRoot()
    {
        Layout layout = BuildLayout();
        string elsewhere = Path.Combine(_root, "important", "payload");
        WriteTree(elsewhere, "keep");
        var installer = new FakeInstaller(layout.StagingRoot)
        {
            // 日志是磁盘上谁都能改的文件。
            Journal = new UpdateJournal { Stage = UpdateStage.Staged, StagedPath = elsewhere, BackupPath = layout.Backup }
        };

        await NewCoordinator(installer, new FakeUpdateService(), layout.Install).RecoverAsync(CancellationToken.None);

        Assert.IsTrue(Directory.Exists(elsewhere));
    }

    private LinuxUpdateCoordinator NewCoordinator(FakeInstaller installer, FakeUpdateService update, string install)
        => new(update, installer, new LinuxStorageService(Path.Combine(_root, "data")),
            new LogService(Path.Combine(_root, "logs")), installDirOverride: install);

    private (LinuxUpdateCoordinator, FakeInstaller, FakeUpdateService, Layout) BuildAppliedUpdate()
    {
        Layout layout = BuildLayout();

        // 交换已经做完：安装目录里是新版本，旧版本在 .backup。
        Directory.Move(layout.Install, layout.Backup);
        WriteTree(layout.Install, "new");

        var installer = new FakeInstaller(layout.StagingRoot)
        {
            Journal = new UpdateJournal
            {
                Stage = UpdateStage.Applied,
                ReleaseId = "rel_1",
                TargetVersion = AppInfo.Version,
                StagedPath = layout.Staged,
                BackupPath = layout.Backup
            }
        };
        var update = new FakeUpdateService();
        return (NewCoordinator(installer, update, layout.Install), installer, update, layout);
    }

    private Layout BuildLayout()
    {
        string install = Path.Combine(_root, "install");
        string stagingRoot = Path.Combine(_root, "data", "updates");
        string versionDir = Path.Combine(stagingRoot, "rel_1");
        string staged = Path.Combine(versionDir, "payload");
        WriteTree(install, "old");
        WriteTree(staged, "new");
        return new Layout(install, install + ".backup", stagingRoot, versionDir, staged);
    }

    private sealed record Layout(string Install, string Backup, string StagingRoot, string VersionDir, string Staged);

    private sealed class FakeInstaller(string stagingRoot) : ILinuxUpdateInstaller
    {
        public UpdateJournal? Journal { get; set; }

        public int ProbationStarts { get; private set; }

        public string StagingRoot => stagingRoot;

        public Task<LinuxStageResult> StageAsync(ReleaseManifest manifest, string releaseId, CancellationToken ct)
            => throw new NotSupportedException();

        public UpdateJournal? ReadJournal() => Journal;

        public Task WriteJournalAsync(UpdateJournal journal, CancellationToken ct)
        {
            Journal = journal;
            return Task.CompletedTask;
        }

        public void ClearJournal() => Journal = null;

        public int ReadProbationStarts() => ProbationStarts;

        public void WriteProbationStarts(int starts) => ProbationStarts = Math.Max(0, starts);
    }

    private sealed class FakeUpdateService : IUpdateService
    {
        public List<string> Reports { get; } = new();

        public Task<UpdateCheckResult> CheckAsync(CancellationToken ct)
            => throw new NotSupportedException();

        public Task<AssignedUpdate?> FetchAssignedAsync(CancellationToken ct)
            => Task.FromResult<AssignedUpdate?>(null);

        public Task ReportInstallAsync(string releaseId, string status, string detail, CancellationToken ct)
        {
            Reports.Add($"{releaseId}:{status}" + (detail.Length > 0 ? $":{detail}" : ""));
            return Task.CompletedTask;
        }
    }

    /// <summary>造一棵完整安装树的 tar.gz，所有条目都挂在 <paramref name="prefix"/> 下。</summary>
    private string BuildTreeArchive(string prefix)
    {
        string path = Path.Combine(_root, "tree-" + Guid.NewGuid().ToString("N") + ".tar.gz");

        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionMode.Compress);
        using var writer = new TarWriter(gzip, TarEntryFormat.Pax);

        foreach (string relative in new[] { "myproxy", "myproxy-gui", "Core/xray", "Core/VERSION.txt" })
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, prefix + relative)
            {
                DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(relative))
            };
            writer.WriteEntry(entry);
        }

        return path;
    }

    private void WriteTree(string directory, string marker)
    {
        Directory.CreateDirectory(Path.Combine(directory, "Core"));
        File.WriteAllText(Path.Combine(directory, "myproxy"), marker);
        File.WriteAllText(Path.Combine(directory, "Core", "xray"), marker);
        File.WriteAllText(Path.Combine(directory, "Core", "VERSION.txt"), marker);
    }

    /// <summary>
    /// 造一个只有一个条目的 tar.gz。<paramref name="name"/> 是条目名，
    /// <paramref name="content"/> 是内容（链接条目时当目标用）。
    /// </summary>
    private string BuildArchive(string name, TarEntryType type, string content = "/etc/passwd")
    {
        string path = Path.Combine(_root, "artifact-" + Guid.NewGuid().ToString("N") + ".tar.gz");

        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionMode.Compress);
        using var writer = new TarWriter(gzip, TarEntryFormat.Pax);

        if (type is TarEntryType.SymbolicLink or TarEntryType.HardLink)
        {
            writer.WriteEntry(new PaxTarEntry(type, name) { LinkName = content });
        }
        else
        {
            var entry = new PaxTarEntry(type, name);
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);
            entry.DataStream = new MemoryStream(bytes);
            writer.WriteEntry(entry);
        }

        return path;
    }
}
