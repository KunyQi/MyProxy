using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MyProxy.Core;

namespace MyProxy.Services;

/// <summary>
/// 控制通道的客户端：CLI 与 GUI 都用它跟守护进程说话。
///
/// <para>
/// 每次调用建一条连接、发一行、收一行、关掉。守护进程不在时返回一个明确的
/// 「没在跑」而不是抛异常——「没在跑」是这个协议里最常见的正常状态，
/// 调用方（<c>myproxy status</c>、托盘程序启动时）都要能平静地处理它。
/// </para>
/// </summary>
public sealed class ControlClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(60);

    private readonly string _socketPath;

    public ControlClient(string? socketPath = null)
        => _socketPath = socketPath ?? DefaultSocketPath();

    public static string DefaultSocketPath()
        => Path.Combine(LinuxPaths.RuntimeDir, "control.sock");

    public string SocketPath => _socketPath;

    /// <summary>守护进程是否在跑（能连上控制套接字）。</summary>
    public bool IsRunning()
    {
        try
        {
            using var socket = Connect(ConnectTimeout);
            return socket is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public Task<ControlResponse> SendAsync(string command, string argument = "", CancellationToken ct = default)
        => SendAsync(new ControlRequest { Command = command, Argument = argument }, ct);

    public async Task<ControlResponse> SendAsync(ControlRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Socket? socket;
        try
        {
            socket = Connect(ConnectTimeout);
        }
        catch (Exception ex)
        {
            return ControlResponse.Failure(
                ErrorCode.ApiUnreachable,
                $"无法连接守护进程：{ex.GetType().Name}");
        }

        if (socket is null)
        {
            return ControlResponse.Failure(
                ErrorCode.ApiUnreachable,
                "客户端没有在运行（控制套接字不存在）。");
        }

        using (socket)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ExchangeTimeout);
            CancellationToken token = timeout.Token;

            try
            {
                string payload = JsonSerializer.Serialize(request, ControlJson.Options) + "\n";
                await socket.SendAsync(Encoding.UTF8.GetBytes(payload), SocketFlags.None, token)
                    .ConfigureAwait(false);

                string? line = await ReadLineAsync(socket, token).ConfigureAwait(false);
                if (line is null)
                {
                    return ControlResponse.Failure(ErrorCode.ApiUnreachable, "守护进程没有回应。");
                }

                ControlResponse? response = JsonSerializer.Deserialize<ControlResponse>(line, ControlJson.Options);
                return response ?? ControlResponse.Failure(ErrorCode.ApiUnreachable, "守护进程的回应无法解析。");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return ControlResponse.Failure(ErrorCode.ApiUnreachable, "等待守护进程回应超时。");
            }
            catch (Exception ex)
            {
                return ControlResponse.Failure(ErrorCode.ApiUnreachable, $"控制通道出错：{ex.GetType().Name}");
            }
        }
    }

    private Socket? Connect(TimeSpan timeout)
    {
        if (!File.Exists(_socketPath))
        {
            return null;
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            // 连接超时用异步等待实现：UNIX 套接字的 connect 在 backlog 满时会阻塞，
            // 而这里不能无限期地卡住一个 CLI 命令。
            Task connect = socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath));
            if (!connect.Wait(timeout))
            {
                socket.Dispose();
                return null;
            }

            return socket;
        }
        catch (Exception)
        {
            socket.Dispose();
            return null;
        }
    }

    private static async Task<string?> ReadLineAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var accumulated = new StringBuilder();

        while (true)
        {
            int read = await socket.ReceiveAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return accumulated.Length == 0 ? null : accumulated.ToString();
            }

            for (int index = 0; index < read; index++)
            {
                if (buffer[index] == (byte)'\n')
                {
                    return accumulated.ToString();
                }

                accumulated.Append((char)buffer[index]);
            }
        }
    }
}
