using System.IO;
using System.Security.Cryptography;
using MyProxy.Core;

namespace MyProxy.Services;

/// <summary>
/// 内核资产（<c>Core/xray</c>、<c>geoip.dat</c>、<c>geosite.dat</c>）的定位与校验。
///
/// <para>
/// 这是与 Windows 端 <c>Services/CoreAssets.cs</c> <b>同名的平台实现</b>：共享的
/// <c>Services/ConnectionController.cs</c> 调用 <c>CoreAssets.ResolveDirectory(...)</c>，
/// 两端各自提供这一份文件（与 <c>AppInfo</c> 同一个套路）。
/// </para>
///
/// <para>
/// 与 Windows 端的两点差别，都是平台给的：
/// </para>
/// <list type="bullet">
/// <item>Linux 产物是目录发布（tarball / 发行版包），内核就在 <c>Core/</c> 里躺着，
/// 不需要 Windows 那种「把内嵌资源解压到 %LocalAppData%」的搬运。</item>
/// <item>因此校验从「解压时校验一次」变成「每次启动都校验」：文件长期躺在磁盘上、
/// 可被同用户的其他进程改写，而它接下来要拿到用户的 REALITY 凭据与全部流量。
/// 一次 SHA-256 的代价（20MB 级文件，几十毫秒）换来的是「跑的一定是随包发出
/// 的那个内核」。</item>
/// </list>
/// </summary>
internal static class CoreAssets
{
    private const string XrayName = "xray";
    private static readonly string[] RequiredFiles = [XrayName, "geoip.dat", "geosite.dat"];
    private const string ManifestName = "VERSION.txt";

    private static readonly object Sync = new();
    private static string? _resolvedDirectory;

    /// <summary>
    /// 返回一个<b>已按 VERSION.txt 校验通过</b>的内核目录。
    /// 任何一步不过都抛 <see cref="ErrorCode.XrayMissing"/>，绝不降级为「用找到的那个」。
    /// </summary>
    public static string ResolveDirectory(IStorageService storage)
    {
        ArgumentNullException.ThrowIfNull(storage);

        lock (Sync)
        {
            if (_resolvedDirectory is not null)
            {
                return _resolvedDirectory;
            }

            _resolvedDirectory = ResolveUncached();
            return _resolvedDirectory;
        }
    }

    /// <summary>测试与诊断用：清掉本进程缓存的定位结果。</summary>
    internal static void ResetCache()
    {
        lock (Sync)
        {
            _resolvedDirectory = null;
        }
    }

    /// <summary>
    /// 崩溃恢复时的判据：这个路径是不是<b>我们自己的</b>内核。
    ///
    /// <para>
    /// 只按进程名杀 xray 是错的——PID 会被复用。所以要同时满足两件事：路径落在
    /// 本 build 的内核目录里，并且该文件当前的 SHA-256 与清单一致。
    /// </para>
    /// </summary>
    public static bool IsOwnedXrayPath(IStorageService storage, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            string actual = Path.GetFullPath(path);
            string expected = Path.GetFullPath(Path.Combine(CoreDirectory(), XrayName));
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                return false;
            }

            Dictionary<string, string> manifest = ReadManifest(CoreDirectory());
            return File.Exists(actual) && HasHash(actual, manifest[XrayName]);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string ResolveUncached()
    {
        string directory = CoreDirectory();
        try
        {
            Dictionary<string, string> manifest = ReadManifest(directory);

            foreach (string name in RequiredFiles)
            {
                string path = Path.Combine(directory, name);
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException($"内核文件缺失：{path}", path);
                }

                if (!HasHash(path, manifest[name]))
                {
                    throw new InvalidDataException($"内核文件与 {ManifestName} 的哈希不符：{name}");
                }
            }

            // 可执行位掉了就跑不起来，而 tarball 解包、U 盘拷贝、Windows 上解压都会掉。
            // 这是本机文件属性、不是安全边界，所以就地补上并继续。
            EnsureExecutable(Path.Combine(directory, XrayName));
            return directory;
        }
        catch (MyProxyException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new MyProxyException(
                ErrorCodeMessages.Get(ErrorCode.XrayMissing), ErrorCode.XrayMissing, ex);
        }
    }

    private static string CoreDirectory()
    {
        string? configured = Environment.GetEnvironmentVariable("MYPROXY_CORE_DIR");
        if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathRooted(configured))
        {
            return configured.TrimEnd(Path.DirectorySeparatorChar);
        }

        return LinuxPaths.CoreDir;
    }

    private static Dictionary<string, string> ReadManifest(string directory)
    {
        string path = Path.Combine(directory, ManifestName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"内核清单缺失：{path}", path);
        }

        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in File.ReadAllLines(path))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            int separator = trimmed.IndexOf('=');
            if (separator > 0)
            {
                entries[trimmed[..separator].Trim()] = trimmed[(separator + 1)..].Trim();
            }
        }

        foreach (string name in RequiredFiles)
        {
            // 空值 = 还没人回填（见 assets/VERSION.txt 的说明）。fail closed：
            // 没有哈希可核对的 xray 与「随便哪个 xray」没有区别。
            if (!entries.TryGetValue(name, out string? hash) ||
                hash.Length != 64 ||
                !hash.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException(
                    $"{ManifestName} 里 {name} 的 SHA-256 无效或未回填（运行 scripts/fetch_xray_linux.py）。");
            }
        }

        return entries;
    }

    private static void EnsureExecutable(string path)
    {
        try
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            UnixFileMode wanted = mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute
                | UnixFileMode.OtherExecute;
            if (mode != wanted)
            {
                File.SetUnixFileMode(path, wanted);
            }
        }
        catch (Exception)
        {
            // 权限改不动（只读挂载、非属主）时留给 Process.Start 去报真正的错误。
        }
    }

    private static bool HasHash(string path, string expected)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        string actual = Convert.ToHexString(SHA256.HashData(stream));
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }
}
