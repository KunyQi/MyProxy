using MyProxy.Services;

namespace MyProxy;

public sealed class AppServices
{
    public StorageService Storage { get; }

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

    public ISingleInstanceService SingleInstance { get; }

    public ITrayService Tray { get; }

    public IStartupService Startup { get; }

    public ICrashRecoveryService CrashRecovery { get; }

    public IUpdateService Update { get; }

    public IThemeService Theme { get; }

    /// <summary>Update Plane 的编排器：验签、暂存、交换、恢复、上报。</summary>
    public UpdateCoordinator Updates { get; }

    /// <summary>Observability Plane 的客户端侧：按服务类别上报聚合字节数。</summary>
    public IUsageReporter Usage { get; }

    public AppServices(SingleInstanceService? singleInstance = null)
    {
        Storage = new StorageService();
        Log = new LogService(Storage.LogDir);
        Storage.AttachLogger(Log);

        Endpoint = new ApiEndpoint();

        Binding = new BindingService(Storage, Log, Endpoint);
        Config = new ConfigService(Storage, Log, Endpoint);
        Xray = new XrayService(Storage, Log);
        Proxy = new WindowsProxyService(Storage, Log);
        Network = new NetworkService();

        // Update/Updates 必须排在 Connection 之前：控制器拿的是一个每次读都
        // 取当前 flag 值的委托，而 Updates 在那之前就得存在。
        Update = new UpdateService(Endpoint, Storage, Log);
        Updates = new UpdateCoordinator(Update, new UpdateInstaller(Storage, Log), Storage, Log);

        var usageReporter = new UsageReporter(Endpoint, Storage, Log);
        Usage = usageReporter;
        Connection = new ConnectionController(
            Storage, Log, Config, Xray, Proxy, Network,
            heartbeatInterval: null,
            trafficStats: new XrayTrafficStatsService(Log),
            usageReporter: usageReporter,
            // 每次读都取当前值：开关由心跳刷新，快照会让它停在启动那一刻。
            categoryAttributionEnabled: () => Updates.Flags.IsEnabled(Core.KnownFeatureFlags.UsageCategories),
            // 界面封送器由进程注入：控制器本身不认识 WPF（Linux 端注入 Avalonia 的实现，
            // 命令行与 systemd 下不注入，回调就地触发）。
            uiDispatcher: new WpfUiDispatcher());

        SingleInstance = singleInstance ?? new SingleInstanceService();
        Tray = new TrayService(Log);
        Startup = new StartupService();
        CrashRecovery = new CrashRecoveryService(Proxy, Storage, Log);
        Theme = new ThemeService();
    }
}
