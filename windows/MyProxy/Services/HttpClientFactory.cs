using System.Net.Http;
using System.Net.Security;
using MyProxy.Core;

namespace MyProxy.Services;

public static class HttpClientFactory
{
    public static HttpClient Create(BindingTarget target, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.IsConfigured)
        {
            throw new MyProxyException("尚未配置服务器，请联系部署管理员。", ErrorCode.ServerNotConfigured);
        }
#if !DEBUG
        if (!target.IsHttps)
        {
            throw new MyProxyException(ErrorCodeMessages.Get(ErrorCode.ApiUnreachable), ErrorCode.ApiUnreachable);
        }
#endif
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = timeout,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            AllowAutoRedirect = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = StandardCertificateCallback()
            }
        };
        var http = new HttpClient(handler) { Timeout = timeout };
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.ClientUserAgent);
        return http;
    }

    /// <summary>Use platform chain, validity and hostname checks while preserving the identity error.</summary>
    internal static RemoteCertificateValidationCallback StandardCertificateCallback()
        => (_, certificate, _, errors) => certificate is not null && errors == SslPolicyErrors.None
            ? true
            : throw new ServerCertificateRejectedException();

    public static HttpClient CreateForDownload(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            AllowAutoRedirect = false
        };
        var http = new HttpClient(handler) { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.ClientUserAgent);
        return http;
    }
}
