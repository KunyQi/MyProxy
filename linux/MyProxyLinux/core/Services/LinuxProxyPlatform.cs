using System.IO;
using System.Text;

namespace MyProxy.Services;

/// <summary>
/// 系统代理这一层真正的 I/O：起外部命令、读写环境文件。
///
/// <para>
/// 抽成接口是为了让 <see cref="LinuxProxyService"/> 的所有权与恢复顺序能在没有
/// GNOME / KDE 的机器上被测试——那是这套逻辑里最贵的一段（恢复失败 = 用户断网），
/// 不能只靠在真机上点一遍。命令本身交给 <see cref="LinuxCommands"/>。
/// </para>
/// </summary>
internal interface ILinuxProxyPlatform
{
    bool CommandExists(string command);

    /// <summary>跑一条命令并等它结束；超时或起不来都返回 <c>null</c>。</summary>
    (int ExitCode, string StandardOutput)? Run(
        string command,
        IReadOnlyList<string> arguments,
        TimeSpan timeout);

    string? ReadEnvironmentFile(string path);

    void WriteEnvironmentFile(string path, string contents);

    void DeleteEnvironmentFile(string path);
}

internal sealed class LinuxProxyPlatform : ILinuxProxyPlatform
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    public bool CommandExists(string command) => LinuxCommands.Exists(command);

    public (int ExitCode, string StandardOutput)? Run(
        string command,
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
        => LinuxCommands.Run(command, arguments, timeout);

    public string? ReadEnvironmentFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void WriteEnvironmentFile(string path, string contents)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        LinuxFileSecurity.WriteAtomic(path, contents);
    }

    public void DeleteEnvironmentFile(string path)
    {
        // File.Delete 对不存在的文件本身就是 no-op；删除失败必须上抛，
        // 让 LinuxProxyService 保留恢复标记与备份，避免内核先停掉后留下死代理。
        File.Delete(path);
    }

    /// <summary>环境变量文件的正文（systemd 的 environment.d 与 shell 的 proxy.env 通用）。</summary>
    public static string BuildEnvironmentFile(string host, int port, IReadOnlyList<string> noProxy)
    {
        string url = $"http://{host}:{port}";
        string noProxyValue = string.Join(',', noProxy);

        var builder = new StringBuilder();
        builder.AppendLine("# 由 MyProxy 写入，随连接建立；断开时本文件被删除。");
        builder.AppendLine("# 只设 http/https：本地入站是 http 协议的代理，写成 socks5:// 指向它不是一个");
        builder.AppendLine("# 有效的 SOCKS 代理，客户端会直接失败而不是回退。");
        builder.AppendLine($"http_proxy={url}");
        builder.AppendLine($"https_proxy={url}");
        builder.AppendLine($"HTTP_PROXY={url}");
        builder.AppendLine($"HTTPS_PROXY={url}");
        builder.AppendLine($"no_proxy={noProxyValue}");
        builder.AppendLine($"NO_PROXY={noProxyValue}");
        return builder.ToString();
    }
}
