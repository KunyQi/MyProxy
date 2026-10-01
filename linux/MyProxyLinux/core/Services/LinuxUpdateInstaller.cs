using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using MyProxy.Core;

namespace MyProxy.Services;

/// <summary>一次暂存的结果。<see cref="StagedPath"/> 只有成功时才有意义。</summary>
public sealed record LinuxStageResult(bool Ok, string StagedPath = "", string Detail = "");

public interface ILinuxUpdateInstaller
{
    Task<LinuxStageResult> StageAsync(ReleaseManifest manifest, string releaseId, CancellationToken ct);
    UpdateJournal? ReadJournal();
    Task WriteJournalAsync(UpdateJournal journal, CancellationToken ct);
    void ClearJournal();
    string StagingRoot { get; }

    /// <summary>
    /// 新版本换上之后、被确认能跑之前已经启动过几次（见
    /// <see cref="LinuxUpdateCoordinator.RecoverAsync"/>）。读不到按 0。
    /// </summary>
    int ReadProbationStarts();

    /// <summary>写入试用期启动次数；0 表示清掉这份记录。</summary>
    void WriteProbationStarts(int starts);
}

/// <summary>
/// Linux 端的更新暂存：下载 → 按 manifest 里的 SHA-256 校验 → 验产物签名 → 解包。
///
/// <para>
/// <b>校验不过就什么都不装，没有「只是提示一下」的分支</b>（Update Plane 的四条硬约束
/// 之一）。四道判断的顺序是刻意的：
/// </para>
///
/// <list type="number">
/// <item>manifest 本身已经在 <see cref="UpdateService"/> 里验过 Ed25519 签名
/// （它覆盖了 <c>artifact.sha256</c>）；</item>
/// <item>下载时边写边算 SHA-256，与 manifest 里的值比对——这一条把「传输出错」
/// 与「有人塞了别的字节」一起挡掉；</item>
/// <item>产物的分离签名（<c>&lt;url&gt;.sig</c>，见
/// <see cref="LinuxUpdateApplier"/> 之外的说明）用内置的
/// <see cref="LinuxArtifactSigningKeys"/> 验，公钥由 manifest 里的
/// <c>subjectSha256</c> 指定——这一条挡掉「签名密钥对，但发的人不是我们」；</item>
/// <item>解包时逐条校验路径，任何绝对路径、<c>..</c>、符号链接与硬链接一律拒绝——
/// 一个 tarball 里的 <c>../../.bashrc</c> 与一个指向 <c>/etc</c> 的符号链接，
/// 是这类安装器最经典的失手方式。</item>
/// </list>
///
/// <para>
/// 暂存目录在<b>数据根目录</b>下（<c>~/.local/share/myproxy/updates/&lt;releaseId&gt;</c>），
/// 而不是安装目录里：安装目录可能是只读的（/usr、/opt），而暂存必须发生在用户
/// 有权写的地方。跨文件系统的事实留给交换那一步处理（见
/// <see cref="LinuxUpdateApplier"/>）。
/// </para>
/// </summary>
public sealed class LinuxUpdateInstaller : ILinuxUpdateInstaller
{
    public const string StagingDirName = "updates";
    private const string JournalFileName = "update-journal.json";
    private const string ProbationFileName = "update-probation";
    private const long MaxArtifactBytes = 512L * 1024 * 1024;

    private readonly IStorageService _storage;
    private readonly ILogService _log;
    private readonly HttpClient _http;

    public LinuxUpdateInstaller(IStorageService storage, ILogService log, HttpClient? http = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        // 下载产物使用标准 HTTPS 校验和独立的产物签名验证。
        // 这不削弱安全性——字节由被签名覆盖的 SHA-256 与产物签名判定——
        // 但 URL 必须是 https 且校验绝不能跳过（见 HttpClientFactory.CreateForDownload）。
        _http = http ?? HttpClientFactory.CreateForDownload(TimeSpan.FromMinutes(10));
    }

    public string StagingRoot => Path.Combine(_storage.DataRoot, StagingDirName);

    private string JournalPath => Path.Combine(_storage.DataRoot, JournalFileName);

    private string ProbationPath => Path.Combine(_storage.DataRoot, ProbationFileName);

    public async Task<LinuxStageResult> StageAsync(
        ReleaseManifest manifest,
        string releaseId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.ArtifactSize <= 0 || manifest.ArtifactSize > MaxArtifactBytes)
        {
            return new LinuxStageResult(false, Detail: "artifact size is out of the accepted range");
        }

        string versionDir = Path.Combine(StagingRoot, Sanitize(releaseId));
        string stagedPath = Path.Combine(versionDir, "payload");
        string archivePath = Path.Combine(versionDir, "artifact.tar.gz");
        string signaturePath = Path.Combine(versionDir, "artifact.sig");

        try
        {
            Directory.CreateDirectory(versionDir);

            if (!await DownloadAsync(manifest.ArtifactUrl, archivePath, manifest.ArtifactSize, ct)
                    .ConfigureAwait(false))
            {
                return new LinuxStageResult(false, Detail: "download failed");
            }

            string actualHash = await HashFileAsync(archivePath, ct).ConfigureAwait(false);
            if (!string.Equals(actualHash, manifest.ArtifactSha256, StringComparison.Ordinal))
            {
                _log.Warn(nameof(LinuxUpdateInstaller), "artifact SHA-256 mismatch; refusing to install");
                return new LinuxStageResult(false, Detail: "artifact checksum mismatch");
            }

            string signatureDetail = await VerifyArtifactSignatureAsync(
                manifest, archivePath, signaturePath, ct).ConfigureAwait(false);
            if (signatureDetail.Length > 0)
            {
                _log.Warn(nameof(LinuxUpdateInstaller), signatureDetail);
                return new LinuxStageResult(false, Detail: signatureDetail);
            }

            string extractPath = Path.Combine(versionDir, "extract");
            foreach (string stale in new[] { stagedPath, extractPath })
            {
                if (Directory.Exists(stale))
                {
                    Directory.Delete(stale, recursive: true);
                }
            }

            ExtractArchive(archivePath, extractPath);

            // 打包脚本产出的 tarball 带一层 MyProxy-linux-<arch>/ 顶层目录（用户照着说明
            // `tar -xzf ... -C ~/.local/share` 解开时要的就是它），而暂存目录必须**就是**
            // 安装目录的形状。两种都认，然后把安装根整体挪成 payload/。
            string? installRoot = LocateInstallRoot(extractPath);
            if (installRoot is null)
            {
                return new LinuxStageResult(false, Detail: "artifact does not contain a MyProxy installation");
            }

            Directory.Move(installRoot, stagedPath);
            if (Directory.Exists(extractPath))
            {
                Directory.Delete(extractPath, recursive: true);
            }

            EnsureInstallTreeExecutable(stagedPath);
            return new LinuxStageResult(true, stagedPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error(nameof(LinuxUpdateInstaller), "staging failed", ex);
            return new LinuxStageResult(false, Detail: ex.GetType().Name);
        }
    }

    public UpdateJournal? ReadJournal()
    {
        try
        {
            if (!File.Exists(JournalPath))
            {
                return null;
            }

            return System.Text.Json.JsonSerializer.Deserialize<UpdateJournal>(
                File.ReadAllText(JournalPath));
        }
        catch (Exception ex)
        {
            _log.Warn(nameof(LinuxUpdateInstaller), $"update journal unreadable: {ex.GetType().Name}");
            return null;
        }
    }

    public Task WriteJournalAsync(UpdateJournal journal, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(journal);

        LinuxFileSecurity.WriteAtomic(
            JournalPath,
            System.Text.Json.JsonSerializer.Serialize(journal));

        return Task.CompletedTask;
    }

    public void ClearJournal()
    {
        try
        {
            if (File.Exists(JournalPath))
            {
                File.Delete(JournalPath);
            }
        }
        catch (Exception ex)
        {
            _log.Warn(nameof(LinuxUpdateInstaller), $"clearing the update journal failed: {ex.GetType().Name}");
        }
    }

    public int ReadProbationStarts()
    {
        try
        {
            return File.Exists(ProbationPath)
                && int.TryParse(File.ReadAllText(ProbationPath).Trim(), out int starts)
                && starts > 0
                    ? starts
                    : 0;
        }
        catch (Exception ex)
        {
            _log.Warn(nameof(LinuxUpdateInstaller), $"update probation record unreadable: {ex.GetType().Name}");
            return 0;
        }
    }

    public void WriteProbationStarts(int starts)
    {
        if (starts > 0)
        {
            LinuxFileSecurity.WriteAtomic(ProbationPath, starts.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return;
        }

        try
        {
            if (File.Exists(ProbationPath))
            {
                File.Delete(ProbationPath);
            }
        }
        catch (Exception ex)
        {
            _log.Warn(nameof(LinuxUpdateInstaller), $"clearing the update probation record failed: {ex.GetType().Name}");
        }
    }

    private async Task<bool> DownloadAsync(
        string url,
        string destination,
        long? expectedSize,
        CancellationToken ct)
    {
        // https + 不跟随重定向：URL 来自签名覆盖的 manifest，重定向意味着最终落点
        // 不在签名覆盖范围内。
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length > 0)
        {
            _log.Warn(nameof(LinuxUpdateInstaller), "artifact URL must be https without user info");
            return false;
        }

        try
        {
            using HttpResponseMessage response = await _http
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _log.Warn(nameof(LinuxUpdateInstaller), $"artifact download returned {(int)response.StatusCode}");
                return false;
            }

            if (response.Content.Headers.ContentLength is long length && length > MaxArtifactBytes)
            {
                return false;
            }

            await using Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);

            byte[] buffer = new byte[1 << 16];
            long total = 0;
            while (true)
            {
                int read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > MaxArtifactBytes)
                {
                    _log.Warn(nameof(LinuxUpdateInstaller), "artifact exceeded the accepted size while downloading");
                    return false;
                }

                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }

            await target.FlushAsync(ct).ConfigureAwait(false);

            if (expectedSize is long expected && total != expected)
            {
                _log.Warn(
                    nameof(LinuxUpdateInstaller),
                    $"artifact size mismatch: manifest={expected}, downloaded={total}");
                return false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn(nameof(LinuxUpdateInstaller), $"artifact download failed: {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>
    /// 产物签名。返回空串表示通过，否则返回拒绝原因（写给日志与上报）。
    ///
    /// <para>
    /// 签名文件的约定是 <c>&lt;artifact.url&gt;.sig</c>，内容是 64 字节签名的 base64。
    /// 用「同一个 URL 加后缀」而不是在 manifest 里再开一个字段，是因为 manifest 的
    /// schema 由服务端与三端共同解析，加字段要同时改四处；而这条约定只影响 Linux
    /// 客户端自己。<b>如果将来 manifest 里出现 <c>artifact.signature.url</c>，
    /// 客户端应当优先用它</b>，那时这条约定就该退场。
    /// </para>
    /// </summary>
    private async Task<string> VerifyArtifactSignatureAsync(
        ReleaseManifest manifest,
        string archivePath,
        string signaturePath,
        CancellationToken ct)
    {
        if (manifest.PlatformSignatureType != ClientReleasePlatform.Ed25519SignatureType)
        {
            return "artifact signature type is not accepted";
        }

        if (!LinuxArtifactSigningKeys.Trusted.TryGetValue(
                manifest.PlatformSignatureSubjectSha256, out byte[]? publicKey))
        {
            // fail closed：认不出签名身份就什么都不装。
            return "artifact signing key is not trusted";
        }

        string signatureUrl = manifest.ArtifactUrl + ".sig";
        if (!await DownloadAsync(signatureUrl, signaturePath, expectedSize: null, ct).ConfigureAwait(false))
        {
            return "artifact signature could not be downloaded";
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String((await File.ReadAllTextAsync(signaturePath, ct)
                .ConfigureAwait(false)).Trim());
        }
        catch (FormatException)
        {
            return "artifact signature is not valid base64";
        }

        if (signature.Length != 64)
        {
            return "artifact signature must be 64 bytes";
        }

        // Ed25519 验签要的是完整消息，不能流式喂进去（RFC 8032 的哈希覆盖整段字节），
        // 所以这里必须把产物读进内存。加一道上限：真实产物是 30–40MB 量级
        // （内核 20MB + geo 数据 5MB + 运行时），128MB 之外的东西不是我们的包，
        // 宁可拒绝也不要为此吃光内存。
        const long MaxSignatureBytes = 128L * 1024 * 1024;
        var artifactInfo = new FileInfo(archivePath);
        if (artifactInfo.Length > MaxSignatureBytes)
        {
            return "artifact is too large to verify";
        }

        byte[] artifactBytes = await File.ReadAllBytesAsync(archivePath, ct).ConfigureAwait(false);
        bool verified = Ed25519.Verify(publicKey, artifactBytes, signature);
        return verified ? "" : "artifact signature does not match";
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 解包，并逐条拒绝危险的条目。
    ///
    /// <para>
    /// 用 <see cref="TarReader"/> 而不是外部 <c>tar</c>：解包过程要能逐条判断，
    /// 而且不该依赖目标机器上装了什么。拒绝规则是<b>白名单式</b>的——只接受
    /// 普通文件与目录，符号链接、硬链接、设备节点、绝对路径、任何含 <c>..</c> 的
    /// 路径全部拒绝。产物是我们自己打的包，里面本来就不该有链接。
    /// </para>
    /// </summary>
    internal static void ExtractArchive(string archivePath, string destination)
    {
        string root = Path.GetFullPath(destination);
        Directory.CreateDirectory(root);

        using FileStream file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);

        while (reader.GetNextEntry() is { } entry)
        {
            string name = entry.Name.Replace('\\', '/');
            if (name.Length == 0 || name.StartsWith('/') || name.Contains("..", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"拒绝解包可疑条目：{entry.Name}");
            }

            string target = Path.GetFullPath(Path.Combine(root, name));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !string.Equals(target, root, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"条目落到了暂存目录之外：{entry.Name}");
            }

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(target);
                    break;

                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                    break;

                default:
                    throw new InvalidDataException($"产物里含有不允许的条目类型 {entry.EntryType}：{entry.Name}");
            }
        }

    }

    /// <summary>
    /// 在解包结果里找安装根：解包目录本身就是一棵安装树，或者它下面<b>恰好只有一个</b>
    /// 目录且那个目录是一棵安装树（打包脚本的 <c>MyProxy-linux-&lt;arch&gt;/</c>）。
    /// 别的形状（多个顶层目录、顶层还散落着文件）一律不认：猜错了就是把一个不知道
    /// 是什么的目录换进安装目录。
    /// </summary>
    internal static string? LocateInstallRoot(string extracted)
    {
        if (LinuxUpdateApplier.IsPlausibleInstallTree(extracted))
        {
            return extracted;
        }

        if (!Directory.Exists(extracted) || Directory.GetFiles(extracted).Length > 0)
        {
            return null;
        }

        string[] children = Directory.GetDirectories(extracted);
        return children.Length == 1 && LinuxUpdateApplier.IsPlausibleInstallTree(children[0])
            ? children[0]
            : null;
    }

    /// <summary>
    /// 可执行位：tar 里通常带着，但解包工具、中间拷贝、U 盘都可能把它弄丢，
    /// 而丢了的后果是「换完版本起不来」——那时安装目录已经换过了。
    /// 托盘程序也在其中：桌面项的 <c>Exec=myproxy-gui</c> 指的就是它。
    /// </summary>
    internal static void EnsureInstallTreeExecutable(string root)
    {
        EnsureExecutable(Path.Combine(root, "myproxy"));
        EnsureExecutable(Path.Combine(root, "myproxy-gui"));
        EnsureExecutable(Path.Combine(root, "Core", "xray"));
    }

    private static void EnsureExecutable(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            UnixFileMode mode = File.GetUnixFileMode(path);
            UnixFileMode wanted = mode
                | UnixFileMode.UserExecute
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherExecute;
            if (mode != wanted)
            {
                File.SetUnixFileMode(path, wanted);
            }
        }
        catch (Exception)
        {
            // 改不动就让新的进程去报错，这里不做补偿。
        }
    }

    internal static string Sanitize(string releaseId)
    {
        var builder = new StringBuilder();
        foreach (char character in releaseId)
        {
            builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_');
        }

        // 只由点组成的名字（"."、".."）会让 Path.Combine 指回暂存根或它的上一层，
        // 而暂存目录最后是要被递归删除的。
        string sanitized = builder.ToString();
        return sanitized.Trim('.').Length == 0 ? "unknown" : sanitized;
    }
}
