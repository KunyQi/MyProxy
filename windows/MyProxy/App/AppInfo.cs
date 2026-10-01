using MyProxy.Core;

namespace MyProxy;

public static class AppInfo
{
    public const string ProductName = "MyProxy";
    public const string Version = "0.1.0";
    public const string Platform = "windows";
    public static string ClientUserAgent => $"MyProxy/{Version} (Windows)";
    public static string BuiltInTargetBaseUrl
    {
        get
        {
#if DEBUG
            return "http://127.0.0.1:8090";
#else
            return DeploymentConfiguration.ApiBaseUrl;
#endif
        }
    }
}
