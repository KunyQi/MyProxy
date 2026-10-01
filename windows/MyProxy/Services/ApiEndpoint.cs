using System.Net.Http;
using MyProxy.Core;

namespace MyProxy.Services;

public interface IApiEndpoint
{
    /// <summary>目标的 base URL。</summary>
    string BaseUrl { get; }

    /// <summary>
    /// 取一个指向目标的客户端。<paramref name="key"/> 用来区分不同超时的调用方
    /// （每个服务一个）。
    /// </summary>
    HttpClient Client(string key, TimeSpan timeout);
}

/// <summary>
/// 进程内唯一的 API 端点。
///
/// <para>
/// 目标是编译期常量，所以这里不再有「现在该连哪台」这个运行态——它存在的理由
/// 只剩一个：<c>SocketsHttpHandler</c> 持有连接池，每次 new 一个
/// <see cref="HttpClient"/> 都会把连接池一起丢掉，所以按 key 缓存一份。
/// </para>
/// </summary>
public sealed class ApiEndpoint : IApiEndpoint, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HttpClient> _clients = new(StringComparer.Ordinal);
    private readonly BindingTarget _target;
    private bool _disposed;

    public ApiEndpoint(BindingTarget? target = null)
        => _target = target ?? BindingTarget.FromBaseUrl(AppInfo.BuiltInTargetBaseUrl);

    public string BaseUrl => _target.BaseUrl;

    public HttpClient Client(string key, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_clients.TryGetValue(key, out HttpClient? existing))
            {
                return existing;
            }

            HttpClient created = HttpClientFactory.Create(_target, timeout);
            _clients[key] = created;
            return created;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (HttpClient client in _clients.Values)
            {
                try
                {
                    client.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // 已经被释放过就算了：这里的目的只是别把连接池漏掉。
                }
            }

            _clients.Clear();
        }
    }
}
