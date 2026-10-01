using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MyProxy.Core;

namespace MyProxy.Services;

/// <summary>
/// 守护进程的控制口：一个只属于当前用户的 UNIX 域套接字。
///
/// <para>
/// 放在 <c>runtime/control.sock</c>，权限 0600，外层目录 0700。两道关卡：
/// </para>
///
/// <list type="number">
/// <item>文件权限：别的用户连打开这个套接字都做不到（目录也是 0700）；</item>
/// <item><c>SO_PEERCRED</c>：连接建立后核对对端 uid 是不是自己。
/// 这一条与 <c>server/myproxy_server</c> 里特权 helper 的做法是同一条规矩——
/// 权限位是「正常人进不来」，<c>SO_PEERCRED</c> 是「进来了也得自报家门」。
/// 它是纵深防御：一个能连上这个套接字的进程，已经能命令内核进程去连任意服务器。</item>
/// </list>
///
/// <para>
/// 协议：一行 JSON 请求 → 一行 JSON 响应，然后关闭。没有长连接、没有推送，
/// 所以不存在「谁的连接僵住了要不要清」这类状态。
/// </para>
/// </summary>
public sealed class ControlServer : IDisposable
{
    /// <summary>单条请求的上限。超过就断开——这是本机协议，没有超长请求的理由。</summary>
    private const int MaxRequestBytes = 64 * 1024;

    private const int SolSocket = 1;
    private const int SoPeerCred = 17;

    private readonly string _socketPath;
    private readonly Func<ControlRequest, CancellationToken, Task<ControlResponse>> _handler;
    private readonly ILogService? _log;
    private readonly CancellationTokenSource _cts = new();
    private Socket? _listener;
    private Task? _acceptLoop;

    public ControlServer(
        string socketPath,
        Func<ControlRequest, CancellationToken, Task<ControlResponse>> handler,
        ILogService? log = null)
    {
        _socketPath = socketPath ?? throw new ArgumentNullException(nameof(socketPath));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _log = log;
    }

    public string SocketPath => _socketPath;

    public void Start()
    {
        string? directory = Path.GetDirectoryName(_socketPath);
        if (!string.IsNullOrEmpty(directory))
        {
            LinuxFileSecurity.EnsurePrivateDirectory(directory);
        }

        // 上一次被 SIGKILL 留下的套接字文件会挡住 bind。先试着连一下：连不上说明
        // 没有人监听，那个文件就是残留，删掉。
        if (File.Exists(_socketPath) && !IsAnyoneListening())
        {
            DeleteSocketFile();
        }

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
            listener.Listen(backlog: 8);
        }
        catch (SocketException ex)
        {
            listener.Dispose();
            throw new MyProxyException(
                $"控制套接字无法建立：{_socketPath}",
                Core.ErrorCode.ProxyApplyFailed,
                ex);
        }

        LinuxFileSecurity.TryRestrict(_socketPath);
        _listener = listener;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经取消过了。
        }

        try
        {
            _listener?.Dispose();
        }
        catch (Exception)
        {
            // 关不掉也不影响退出。
        }

        try
        {
            _acceptLoop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // 接受循环里抛出的异常在它自己的任务里已经记过日志。
        }

        DeleteSocketFile();
        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        Socket? listener = _listener;
        if (listener is null)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            Socket connection;
            try
            {
                connection = await listener.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                _log?.Warn(nameof(ControlServer), $"接受控制连接失败：{ex.SocketErrorCode}");
                continue;
            }

            _ = Task.Run(() => HandleConnectionAsync(connection, ct), CancellationToken.None);
        }
    }

    private async Task HandleConnectionAsync(Socket connection, CancellationToken ct)
    {
        using (connection)
        {
            try
            {
                if (!IsSameUser(connection))
                {
                    _log?.Warn(nameof(ControlServer), "拒绝了一个来自其他用户 uid 的控制连接");
                    return;
                }

                string? line = await ReadLineAsync(connection, ct).ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                ControlRequest? request;
                try
                {
                    request = JsonSerializer.Deserialize<ControlRequest>(line, ControlJson.Options);
                }
                catch (JsonException)
                {
                    await WriteAsync(
                        connection,
                        ControlResponse.Failure(Core.ErrorCode.Unknown, "请求不是合法的 JSON。"),
                        ct).ConfigureAwait(false);
                    return;
                }

                if (request is null || request.Command.Length == 0)
                {
                    await WriteAsync(
                        connection,
                        ControlResponse.Failure(Core.ErrorCode.Unknown, "缺少 command 字段。"),
                        ct).ConfigureAwait(false);
                    return;
                }

                ControlResponse response;
                try
                {
                    response = await _handler(request, ct).ConfigureAwait(false);
                }
                catch (Core.MyProxyException ex)
                {
                    response = ControlResponse.Failure(ex.ErrorCode, ex.Message);
                }
                catch (OperationCanceledException)
                {
                    response = ControlResponse.Failure(Core.ErrorCode.Unknown, "操作已取消。");
                }
                catch (Exception ex)
                {
                    _log?.Error(nameof(ControlServer), $"命令 {request.Command} 失败", ex);
                    response = ControlResponse.Failure(Core.ErrorCode.Unknown, "内部错误，详见日志。");
                }

                await WriteAsync(connection, response, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.Warn(nameof(ControlServer), $"控制连接处理失败：{ex.GetType().Name}");
            }
        }
    }

    private static async Task<string?> ReadLineAsync(Socket connection, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var accumulated = new StringBuilder();

        while (true)
        {
            int read = await connection.ReceiveAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return accumulated.Length == 0 ? null : accumulated.ToString();
            }

            for (int index = 0; index < read; index++)
            {
                char character = (char)buffer[index];
                if (character == '\n')
                {
                    return accumulated.ToString();
                }

                accumulated.Append(character);
                if (accumulated.Length > MaxRequestBytes)
                {
                    throw new InvalidDataException("控制请求过长。");
                }
            }
        }
    }

    private static async Task WriteAsync(Socket connection, ControlResponse response, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(response, ControlJson.Options) + "\n";
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        await connection.SendAsync(bytes, SocketFlags.None, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 对端 uid 是不是当前用户。取不到时<b>拒绝</b>：这条通道能命令内核进程，
    /// 「问不出对方是谁」不能当成「对方是自己人」。
    /// </summary>
    private static bool IsSameUser(Socket connection)
    {
        try
        {
            var credentials = default(Ucred);
            int length = Marshal.SizeOf<Ucred>();
            IntPtr handle = connection.Handle;

            if (getsockopt(handle, SolSocket, SoPeerCred, ref credentials, ref length) != 0)
            {
                return false;
            }

            return credentials.Uid == getuid();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool IsAnyoneListening()
    {
        try
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            probe.Connect(new UnixDomainSocketEndPoint(_socketPath));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void DeleteSocketFile()
    {
        try
        {
            if (File.Exists(_socketPath))
            {
                File.Delete(_socketPath);
            }
        }
        catch (Exception)
        {
            // 删不掉就留着；下一次 Start 会再试一次。
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Ucred
    {
        public int Pid;
        public uint Uid;
        public uint Gid;
    }

    [DllImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    private static extern int getsockopt(
        IntPtr socket,
        int level,
        int optionName,
        ref Ucred optionValue,
        ref int optionLength);

    [DllImport("libc", EntryPoint = "getuid", SetLastError = true)]
    private static extern uint getuid();
}
