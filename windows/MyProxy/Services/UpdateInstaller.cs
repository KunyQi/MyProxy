using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using MyProxy.Core;

namespace MyProxy.Services;

/// <summary>暂存一个已验证的更新包的结果。</summary>
public sealed record StageResult(bool Ok, string StagedPath = "", string Detail = "");

public interface IUpdateInstaller
{
    Task<StageResult> StageAsync(ReleaseManifest manifest, string releaseId, CancellationToken ct);

    UpdateJournal? ReadJournal();

    Task WriteJournalAsync(UpdateJournal journal, CancellationToken ct);

    void ClearJournal();
}

/// <summary>
/// 下载、校验并解包一个 release。
///
/// <para>
/// 校验顺序是 <b>manifest 签名 → artifact SHA-256 → 平台签名（Authenticode）</b>，
/// 前一步没过就绝不做后一步。manifest 的签名由调用方在拿到这里之前就已经验过；
/// 本类负责后两步，并且<b>任何一步不符就删包、不安装</b>——不存在「校验没过但
/// 只是提示一下」的分支，那个分支一旦存在就会在某次赶工里被走通。
/// </para>
///
/// <para>
/// ZIP 本身没有 Authenticode 签名，被签名的是包里的 <c>MyProxy.exe</c>。所以先
/// 解包到暂存目录，再对解出来的可执行文件验签，并把签名者证书的 SHA-256 与
/// manifest 里声明的 <c>subjectSha256</c> 逐字节比对。这是<b>两件事</b>，缺一
/// 不可：只验「有签名」而不比对签名者，等于接受任何一张能签名的证书；而只读
/// 证书不验摘要（<c>X509Certificate.CreateFromSignedFile</c> 正是只读不验），
/// 则等于接受任何一份抄了别人证书 blob 的字节。前者由这里比对，后者由
/// <see cref="AuthenticodeVerifier"/> 的 <c>WinVerifyTrust</c> 负责。
/// </para>
/// </summary>
public sealed class UpdateInstaller : IUpdateInstaller
{
    public const string JournalFileName = "update-journal.json";
    public const string StagingDirName = "updates";
    public const string ExecutableName = "MyProxy.exe";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IStorageService _storage;
    private readonly ILogService _log;
    private readonly HttpClient _http;

    public UpdateInstaller(IStorageService storage, ILogService log, HttpClient? http = null)
    {
        _storage = storage;
        _log = log;
        // 下载走独立的 HttpClient：artifact 通常不在 Device API 那个 origin 上，
        // 所以它不能复用被 pin 住的那一个。禁自动重定向的理由与 API 客户端一致。
        _http = http ?? HttpClientFactory.CreateForDownload(TimeSpan.FromMinutes(10));
    }

    private string StagingRoot => Path.Combine(_storage.DataRoot, StagingDirName);

    private string JournalPath => Path.Combine(StagingRoot, JournalFileName);

    public async Task<StageResult> StageAsync(ReleaseManifest manifest, string releaseId, CancellationToken ct)
    {
        string versionDir = Path.Combine(StagingRoot, SanitizeVersion(manifest.Version));
        string packagePath = Path.Combine(versionDir, "package.zip");
        string stagedPath = Path.Combine(versionDir, "staged");

        try
        {
            SafeDelete(versionDir);
            Directory.CreateDirectory(versionDir);

            using (HttpResponseMessage response = await _http
                .GetAsync(manifest.ArtifactUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    return Fail(versionDir, $"download HTTP {(int)response.StatusCode}");
                }

                // 声明的大小是第一道闸：它让一个被换掉的巨大文件在写满磁盘之前
                // 就被拒绝，而不是等到算完哈希才发现。
                long? declared = response.Content.Headers.ContentLength;
                if (declared is not null && declared.Value != manifest.ArtifactSize)
                {
                    return Fail(versionDir, "artifact size mismatch");
                }

                await using FileStream file = File.Create(packagePath);
                await using Stream network = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await CopyBoundedAsync(network, file, manifest.ArtifactSize, ct).ConfigureAwait(false);
            }

            var packageInfo = new FileInfo(packagePath);
            if (packageInfo.Length != manifest.ArtifactSize)
            {
                return Fail(versionDir, "artifact size mismatch");
            }

            string actualSha256 = await ComputeSha256Async(packagePath, ct).ConfigureAwait(false);
            if (!actualSha256.Equals(manifest.ArtifactSha256, StringComparison.Ordinal))
            {
                return Fail(versionDir, "artifact sha256 mismatch");
            }

            Directory.CreateDirectory(stagedPath);
            ZipFile.ExtractToDirectory(packagePath, stagedPath, overwriteFiles: true);

            string executable = Path.Combine(stagedPath, ExecutableName);
            if (!File.Exists(executable))
            {
                return Fail(versionDir, "package does not contain the executable");
            }

            // 先验签名是否真的覆盖这份字节，再问是谁签的。反过来的话，一个只是
            // 把别人的证书 blob 抄过来的二进制会两条断言都通过。见 AuthenticodeVerifier。
            AuthenticodeResult signature = AuthenticodeVerifier.Verify(executable);
            if (!signature.Ok)
            {
                return Fail(versionDir, $"package executable signature rejected: {signature.Detail}");
            }

            if (!signature.SignerSha256.Equals(
                    manifest.PlatformSignatureSubjectSha256, StringComparison.OrdinalIgnoreCase))
            {
                return Fail(versionDir, "authenticode signer mismatch");
            }

            // 校验全过之后才删掉压缩包，省下一份磁盘占用；失败路径上整个目录都会被删。
            File.Delete(packagePath);
            _log.Info(nameof(UpdateInstaller), $"Release {releaseId} staged at {stagedPath}");
            return new StageResult(true, stagedPath);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SafeDelete(versionDir);
            throw;
        }
        catch (Exception ex)
        {
            return Fail(versionDir, $"{ex.GetType().Name}");
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

            string json = File.ReadAllText(JournalPath);
            return JsonSerializer.Deserialize<UpdateJournal>(json);
        }
        catch
        {
            // 读不出来的日志等于没有日志：宁可少做一次恢复，也不要按半个
            // 反序列化出来的状态去动安装目录。
            return null;
        }
    }

    public async Task WriteJournalAsync(UpdateJournal journal, CancellationToken ct)
    {
        Directory.CreateDirectory(StagingRoot);
        string json = JsonSerializer.Serialize(journal with { UpdatedAt = DateTimeOffset.UtcNow }, JsonOptions);
        string temporary = JournalPath + ".tmp";
        await File.WriteAllTextAsync(temporary, json, ct).ConfigureAwait(false);
        // 原子替换：交换目录的那一刻断电，日志本身绝不能是半截的。
        File.Move(temporary, JournalPath, overwrite: true);
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
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private StageResult Fail(string versionDir, string detail)
    {
        SafeDelete(versionDir);
        _log.Warn(nameof(UpdateInstaller), $"Staging refused: {detail}");
        return new StageResult(false, "", detail);
    }

    /// <summary>
    /// 按声明的大小设上限复制。没有上限的话，一个换掉的 artifact 可以一直写到
    /// 磁盘满——哈希校验发生在写完之后，太晚了。
    /// </summary>
    private static async Task CopyBoundedAsync(Stream source, Stream destination, long limit, CancellationToken ct)
    {
        byte[] buffer = new byte[81920];
        long written = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0)
            {
                return;
            }

            written += read;
            if (written > limit)
            {
                throw new InvalidDataException("artifact exceeds the declared size");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string SanitizeVersion(string version)
    {
        Span<char> buffer = stackalloc char[Math.Min(version.Length, 64)];
        int length = 0;
        foreach (char ch in version)
        {
            if (length >= buffer.Length)
            {
                break;
            }

            bool safe = char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' or '+';
            buffer[length++] = safe ? ch : '_';
        }

        return length == 0 ? "unknown" : new string(buffer[..length]);
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
