using MyProxy.Services;

namespace MyProxy;

/// <summary>
/// Linux 端的组装根：手工装配单例，没有 DI 容器（与 Windows 端 <c>App/AppServices.cs</c>
/// 同一个套路，顺序同样不能随便动）。
///
/// <para>
/// <b>连接只有一个持有者。</b>守护进程（<c>myproxy run</c>）用它持有内核与系统代理；
/// CLI 与托盘 GUI 只持有 <see cref="Client"/>（控制通道的客户端），通过它读写状态。
/// 这样系统代理、心跳与 LastKnownGood 就不会出现两个进程各改一半的情况。
/// </para>
/// </summary>
public sealed class LinuxAppServices
{
    public LinuxStorageService Storage { get; }

    public LogService Log { get; }

    /// <summary>
    /// 进程内唯一的 API 客户端持有者。目标是编译期常量，这里没有「换目标」这件事；
    /// 它存在是为了按调用方缓存 HttpClient，让连接池能复用。
    /// </summary>
    public ApiEndpoint Endpoint { get; }

    public IBindingService Binding { get; }

    public IConfigService Config { get; }

    public IXrayService Xray { get; }

    public ISystemProxyService Proxy { get; }

    public INetworkService Network { get; }

    public IConnectionController Connection { get; }

    public IUpdateService Update { get; }

    /// <summary>Update Plane 的编排器：验签、暂存、交换、恢复、上报。</summary>
    public LinuxUpdateCoordinator Updates { get; }

    /// <summary>Observability Plane 的客户端侧：按服务类别上报聚合字节数。</summary>
    public IUsageReporter Usage { get; }

    public LinuxStartupService Startup { get; }

    public ICrashRecoveryService CrashRecovery { get; }

    /// <summary>控制通道客户端。守护进程自己不用它（它是对端），CLI 与 GUI 用它。</summary>
    public ControlClient Client { get; }

    public LinuxAppServices(
        string? dataRootOverride = null,
        string? controlSocketPath = null,
        ILogService? logOverride = null,
        string singleInstanceName = "instance")
    {
        Storage = new LinuxStorageService(dataRootOverride, logOverride);
        Log = new LogService(Storage.LogDir);
        Storage.AttachLogger(Log);

        Endpoint = new ApiEndpoint();

        Binding = new BindingService(Storage, Log, Endpoint);
        Config = new ConfigService(Storage, Log, Endpoint);
        Xray = new LinuxXrayService(Storage, Log);
        Proxy = new LinuxProxyService(Storage, Log);
        Network = new NetworkService();

        // Update/Updates 必须排在 Connection 之前：控制器拿的是一个每次读都取
        // 当前 flag 值的委托，而 Updates 在那之前就得存在。
        Update = new UpdateService(Endpoint, Storage, Log);
        Updates = new LinuxUpdateCoordinator(Update, new LinuxUpdateInstaller(Storage, Log), Storage, Log);

        var usageReporter = new UsageReporter(Endpoint, Storage, Log);
        Usage = usageReporter;

        Connection = new ConnectionController(
            Storage, Log, Config, Xray, Proxy, Network,
            heartbeatInterval: null,
            trafficStats: new XrayTrafficStatsService(Log),
            usageReporter: usageReporter,
            // 每次读都取当前值：开关由心跳刷新，快照会让它停在启动那一刻。
            categoryAttributionEnabled: () => Updates.Flags.IsEnabled(Core.KnownFeatureFlags.UsageCategories));

        Startup = new LinuxStartupService(Log);
        CrashRecovery = new LinuxCrashRecoveryService(Proxy, Storage, Log);
        Client = new ControlClient(controlSocketPath);

        // 单实例名字由调用方给：守护进程用 "instance"，托盘 GUI 用自己的名字
        // （两者都能各有一份，互不干扰）。
        SingleInstanceName = singleInstanceName;
    }

    public string SingleInstanceName { get; }
}
