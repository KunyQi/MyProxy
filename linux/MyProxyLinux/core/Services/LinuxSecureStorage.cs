using System.IO;

namespace MyProxy.Services;

/// <summary>
/// Linux 端的凭据存储：设备令牌明文落在 <c>$XDG_DATA_HOME/myproxy/secrets/device.json</c>，
/// 权限 0600，目录 0700。
///
/// <para>
/// <b>与 Windows / Android 的差别要说清楚，不能含糊过去。</b>
/// Windows 用 DPAPI、Android 用 EncryptedSharedPreferences，两者都有一把由系统
/// 保管的密钥。Linux 上没有等价物：内核不提供「以当前用户身份加密」的接口，
/// 而可用的密钥环（libsecret / GNOME Keyring）在无桌面会话的机器上根本不存在，
/// 把它做成必需依赖会让客户端在服务器与容器里直接不可用。
/// </para>
///
/// <para>
/// 所以这里的信任根<b>就是文件权限</b>——与 OpenSSH 私钥、<c>~/.netrc</c>、
/// 浏览器 Cookie 库在 Linux 上的保护级别相同。<b>不得把它描述成加密存储。</b>
/// 一个有 root 或能读该用户家目录的攻击者本来就能读到同一台机器上的一切。
/// </para>
///
/// <para>
/// 仍然做对的三件事：写入是原子的（临时文件 + 改名，断电不会留下半个令牌）；
/// 读之前核对权限并就地收窄；<b>读失败与「没有绑定」严格区分</b>——前者抛
/// <see cref="SecureStorageReadException"/>，否则一次瞬时 IO 错误就会把用户送回
/// 配对页，而重新绑定会覆盖掉仍然有效的设备记录，在服务端留下一个孤儿设备。
/// </para>
/// </summary>
public sealed class LinuxSecureStorage : ISecureStorage
{
    private const string Prefix = "myproxy-secret-v1";

    private readonly string _directory;
    private ILogService? _log;

    public LinuxSecureStorage(string dataRoot, ILogService? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _directory = Path.Combine(dataRoot, "secrets");
        _log = log;
        LinuxFileSecurity.EnsurePrivateDirectory(_directory);
    }

    /// <summary>
    /// 组装根在日志服务就绪之后才把它接上（存储先于日志构造，与 Windows 端同一个
    /// 顺序）。凭据的读写发生在连接与解绑时，那时日志早就在了。
    /// </summary>
    public void AttachLogger(ILogService logService)
    {
        ArgumentNullException.ThrowIfNull(logService);
        _log = logService;
    }

    public Task SaveAsync(string name, string plainText, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(plainText);

        string path = PathFor(name);
        try
        {
            // 前缀只是给「这是谁的秘密」留个标记，不提供任何机密性；
            // 真正的保护是下面这行之后的 0600。
            LinuxFileSecurity.WriteAtomic(path, Prefix + "\n" + plainText);
            LinuxFileSecurity.TryRestrict(path);
        }
        catch (Exception ex)
        {
            _log?.Error(nameof(LinuxSecureStorage), "写入凭据失败", ex);
            throw;
        }

        return Task.CompletedTask;
    }

    public Task<string?> ReadAsync(string name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string path = PathFor(name);

        // 符号链接一律拒绝：有人可以把 device.json 指向别处，让本程序去读一个
        // 它本不该读的文件（或者让写入落到别处）。OpenSSH 对私钥也是这个态度。
        if (IsSymlink(path))
        {
            throw new SecureStorageReadException(
                $"凭据路径是符号链接，已拒绝读取：{path}",
                new IOException("Refusing to follow a symlinked credential path."));
        }

        if (!File.Exists(path))
        {
            return Task.FromResult<string?>(null);
        }

        string contents;
        try
        {
            if (!LinuxFileSecurity.IsPrivate(path))
            {
                // 权限曾经宽过：说清楚，并立刻收窄。读还是照读——把用户挡在门外
                // 解决不了已经发生过的暴露，只能让他再也连不上。
                _log?.Warn(
                    nameof(LinuxSecureStorage),
                    $"凭据文件权限过宽，已收窄为 0600：{Path.GetFileName(path)}");
                LinuxFileSecurity.TryRestrict(path);
            }

            contents = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不出来 ≠ 没有绑定（见类型注释）。
            throw new SecureStorageReadException("读取凭据失败。", ex);
        }

        int separator = contents.IndexOf('\n');
        if (separator < 0 || contents[..separator].Trim() != Prefix)
        {
            _log?.Warn(nameof(LinuxSecureStorage), "凭据文件格式不认识，视为内容损坏");
            return Task.FromResult<string?>(null);
        }

        return Task.FromResult<string?>(contents[(separator + 1)..]);
    }

    public Task DeleteAsync(string name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string path = PathFor(name);
        try
        {
            if (File.Exists(path) || IsSymlink(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _log?.Error(nameof(LinuxSecureStorage), "删除凭据失败", ex);
            throw;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 名字只允许小写字母、数字、下划线与短横线，且不含路径分隔符——
    /// 这个存储的名字来自调用方，不能让它变成一次任意的文件写入。
    /// </summary>
    private string PathFor(string name)
    {
        foreach (char character in name)
        {
            bool allowed = character is >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '_' or '-';
            if (!allowed)
            {
                throw new ArgumentException(
                    $"凭据名字只允许小写字母、数字、下划线与短横线：{name}", nameof(name));
            }
        }

        return Path.Combine(_directory, name + ".json");
    }

    private static bool IsSymlink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception)
        {
            return true;     // 问不出来就不读：宁可不读，也不去读一个说不清的东西。
        }
    }
}
