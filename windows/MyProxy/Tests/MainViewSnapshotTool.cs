using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Core;
using MyProxy.Models;
using MyProxy.Services;
using MyProxy.ViewModels;
using MyProxy.Views;
using System.Windows.Controls;

namespace MyProxy.Tests;

/// <summary>
/// 视觉验收工具，不是断言测试：把 MainView 的各状态渲染成 PNG 供目视复核。
///
/// 默认是 no-op——只有设置了 <c>MYPROXY_SNAPSHOT_DIR</c> 才会真正跑。这条守卫是必需的，
/// 因为截图必须创建 <see cref="Application"/> 实例（<c>{StaticResource}</c> 要沿逻辑树找到
/// <c>Application.Resources</c>），而 <c>Application.Current</c> 一旦存在，
/// <see cref="MainViewModel"/> 就会改走 <c>Dispatcher.InvokeAsync</c> 异步刷新，
/// 同进程里的 GuiWorkflowTests 会因此变成时序敏感的。所以本工具只能单独进程运行。
/// </summary>
[TestClass]
public sealed class MainViewSnapshotTool
{
    private const int SettleMs = 700;

    [TestMethod]
    public void CaptureMainViewStates()
    {
        string? outDir = Environment.GetEnvironmentVariable("MYPROXY_SNAPSHOT_DIR");
        if (string.IsNullOrWhiteSpace(outDir))
        {
            return;
        }

        Directory.CreateDirectory(outDir);

        RunSta(() =>
        {
            var app = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                new Uri("/MyProxy;component/Themes/DesignTokens.xaml", UriKind.Relative)));

            Capture(outDir, "01-disconnected", AppState.Disconnected, null);
            Capture(outDir, "02-connecting", AppState.Connecting, null);
            Capture(outDir, "03-connected", AppState.Connected, null);
            Capture(outDir, "04-connected-verified", AppState.Connected, new ConnectionCheckResult
            {
                Reachable = true,
                LatencyMs = 38,
                BestLatencyMs = 31,
                SampleCount = 5,
                Egress = EgressVerdict.Verified,
                Error = ErrorCode.ConnectTestFailed
            });
            Capture(outDir, "05-connected-bypassed", AppState.Connected, new ConnectionCheckResult
            {
                Reachable = true,
                LatencyMs = 12,
                BestLatencyMs = 9,
                SampleCount = 5,
                Egress = EgressVerdict.Bypassed,
                Error = ErrorCode.ConnectTestFailed
            });
            Capture(outDir, "06-connected-global", AppState.Connected, null, ProxyMode.Global);
            Capture(outDir, "07-error", AppState.Error, null);

            // 另外两页也要出图：「所有 UI 必须对齐」不能只验一页。
            CaptureBind(outDir, "08-bind", null);
            CaptureBind(outDir, "09-bind-error", "配对码无效，请检查后重试");
            CaptureSettings(outDir, "10-settings", bound: false);
            CaptureSettings(outDir, "11-settings-bound", bound: true);
        });
    }

    /// <summary>
    /// 三套皮肤各渲一轮，用于目视复核「同骨不同皮」是否真的成立：
    /// 版面应该逐像素对得上，只有材质不同。
    /// 同样默认 no-op，设 <c>MYPROXY_THEME_SNAPSHOT_DIR</c> 才跑。
    /// </summary>
    [TestMethod]
    public void CaptureThemeGallery()
    {
        string? outDir = Environment.GetEnvironmentVariable("MYPROXY_THEME_SNAPSHOT_DIR");
        if (string.IsNullOrWhiteSpace(outDir))
        {
            return;
        }

        Directory.CreateDirectory(outDir);

        RunSta(() =>
        {
            var app = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            app.Resources.MergedDictionaries.Add(LoadTheme(UiTheme.Classic));

            foreach (UiTheme theme in UiThemes.All)
            {
                // 与 ThemeService 同一个动作：就地替换第 0 项。
                app.Resources.MergedDictionaries[0] = LoadTheme(theme);
                string tag = theme.ToString().ToLowerInvariant();

                Capture(outDir, $"{tag}-1-disconnected", AppState.Disconnected, null);
                Capture(outDir, $"{tag}-2-connected", AppState.Connected, null);
                CaptureBind(outDir, $"{tag}-3-bind", null);
                CaptureSettings(outDir, $"{tag}-4-settings", bound: true, theme: theme);
            }
        });
    }

    private static ResourceDictionary LoadTheme(UiTheme theme)
        => (ResourceDictionary)Application.LoadComponent(new Uri(
            $"/MyProxy;component/Themes/{UiThemes.DictionaryFileName(theme)}", UriKind.Relative));

    private static void Capture(
        string outDir,
        string name,
        AppState state,
        ConnectionCheckResult? check,
        ProxyMode mode = ProxyMode.Rule)
    {
        var controller = new SnapshotController();
        controller.InitializeMode(mode);
        var model = new MainViewModel(controller, new SnapshotLog());
        controller.SetState(state);
        if (state == AppState.Connected)
        {
            controller.LatencyMs = 46;
            controller.TrafficSamples = SyntheticTraffic();
            controller.TrafficRate = controller.TrafficSamples[^1];
        }

        controller.SetCheck(check);

        Shoot(outDir, name, new MainView { DataContext = model });
    }

    /// <summary>把任意一页渲染成 PNG。三页共用同一套窗口与沉降设置。</summary>
    private static void Shoot(string outDir, string name, FrameworkElement view)
    {
        var window = new Window
        {
            Width = 400,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            // 屏幕外：窗口必须真的 Show() 才有合成时钟，但不该在验收时糊到用户脸上。
            Left = -4000,
            Top = -4000,
            Background = (Brush)Application.Current.Resources["Brush.Background"],
            Content = view
        };

        window.Show();
        PumpFor(SettleMs);

        var bitmap = new RenderTargetBitmap(
            (int)window.ActualWidth * 2,
            (int)window.ActualHeight * 2,
            192, 192,
            PixelFormats.Pbgra32);
        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string path = Path.Combine(outDir, $"{name}.png");
        using (FileStream fs = File.Create(path))
        {
            encoder.Save(fs);
        }

        window.Close();
        PumpFor(60);
        Console.WriteLine($"snapshot -> {path}");
    }

    private static void CaptureBind(string outDir, string name, string? error)
    {
        var model = new BindViewModel(new SnapshotBinding(), new SnapshotController(), new SnapshotLog());
        if (error is not null)
        {
            model.Reset(error);
        }

        Shoot(outDir, name, new BindView { DataContext = model });
    }

    /// <param name="bound">写入一条设备记录再渲染：设备组只在有绑定时出现，两条路都要出图。</param>
    private static void CaptureSettings(
        string outDir, string name, bool bound, UiTheme theme = UiTheme.Classic)
    {
        string dataRoot = Path.Combine(
            Path.GetTempPath(), "MyProxy.Snapshot", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            var storage = new StorageService(dataRootOverride: dataRoot);
            if (bound)
            {
                storage.SaveDeviceAsync(
                    new DeviceConfig
                    {
                        DeviceId = "snapshot",
                        DeviceToken = "snapshot",
                        DeviceName = "Demo-PC",
                        BoundAt = new DateTimeOffset(2026, 9, 14, 6, 31, 0, TimeSpan.FromHours(10))
                    },
                    CancellationToken.None).GetAwaiter().GetResult();
            }

            var model = new SettingsViewModel(
                storage, new SnapshotStartup(), new SnapshotUpdate(),
                new SnapshotController(), new SnapshotLog(), new SnapshotTheme(theme));

            Shoot(outDir, name, new SettingsView { DataContext = model });
        }
        finally
        {
            try { Directory.Delete(dataRoot, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>一段确定性的合成波形，只为让 Sparkline 在静态截图里有形可看。</summary>
    private static IReadOnlyList<TrafficRate> SyntheticTraffic()
    {
        var samples = new List<TrafficRate>(TrafficRateMonitor.DefaultCapacity);
        for (int i = 0; i < TrafficRateMonitor.DefaultCapacity; i++)
        {
            double phase = i / 6.0;
            double down = (Math.Sin(phase) + 1.2) * 420 * 1024 * (0.35 + (i / 120.0));
            double up = (Math.Cos(phase * 0.7) + 1.1) * 60 * 1024 * (0.4 + (i / 200.0));
            samples.Add(new TrafficRate(Math.Max(up, 0), Math.Max(down, 0)));
        }

        return samples;
    }

    private static void PumpFor(int milliseconds)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }

    /// <summary>只报当前皮肤，不碰资源树（资源由画廊循环自己换）。</summary>
    private sealed class SnapshotTheme(UiTheme current) : IThemeService
    {
        public UiTheme Current { get; } = current;

        public void Apply(UiTheme theme) { }

        // 显式空访问器：存桩永远不会触发它，写成字段式事件会吃一个 CS0067。
        public event Action<UiTheme>? ThemeChanged { add { } remove { } }
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(90)), "snapshot tool timed out");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <summary>OrbFrameProbe 也用它：两个诊断工具需要的是同一份「什么都不做的」控制器。</summary>
    internal sealed class SnapshotController : IConnectionController
    {
        public AppState State { get; private set; } = AppState.Disconnected;
        public ProxyMode Mode { get; private set; } = ProxyMode.Rule;
        public bool IsSwitching => false;
        public int? LatencyMs { get; set; }
        public ErrorCode LastErrorCode => ErrorCode.ConnectTestFailed;
        public string LastErrorMessage => ErrorCodeMessages.Get(ErrorCode.ConnectTestFailed);
        public string? LastSwitchErrorMessage => null;
        public bool IsChecking => false;
        public ConnectionCheckResult? LastCheck { get; private set; }

        public event Action? StateChanged;
        public event Action? ModeChanged;
        public event Action? BindingRequired { add { } remove { } }
        public event Action? CheckChanged;

        internal void SetState(AppState state) { State = state; StateChanged?.Invoke(); }
        internal void SetCheck(ConnectionCheckResult? check) { LastCheck = check; CheckChanged?.Invoke(); }

        public void MarkBound() => SetState(AppState.Disconnected);
        public void InitializeMode(ProxyMode mode) { Mode = mode; ModeChanged?.Invoke(); }
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task SwitchModeAsync(ProxyMode mode, CancellationToken ct) => Task.CompletedTask;
        public Task RebindAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<ConnectionCheckResult> CheckConnectionAsync(CancellationToken ct)
            => Task.FromResult(LastCheck ?? new ConnectionCheckResult());

        public void SetTrafficSamplingSuspended(bool suspended) { }

        public TrafficRate TrafficRate { get; set; } = TrafficRate.Zero;
        public IReadOnlyList<TrafficRate> TrafficSamples { get; set; } = Array.Empty<TrafficRate>();
        public event Action? TrafficChanged { add { } remove { } }
        public event Action<HeartbeatResult>? HeartbeatReceived { add { } remove { } }
    }

    private sealed class SnapshotBinding : IBindingService
    {
        public Task<BindResult> BindAsync(string pairingCode, CancellationToken ct)
            => Task.FromResult(new BindResult());
    }

    private sealed class SnapshotStartup : IStartupService
    {
        public bool IsAutoStartEnabled() => true;
        public void SetAutoStartEnabled(bool enabled) { }
        public void RemoveLegacyAutoStart() { }
    }

    private sealed class SnapshotUpdate : IUpdateService
    {
        public Task<UpdateCheckResult> CheckAsync(CancellationToken ct)
            => Task.FromResult(new UpdateCheckResult(UpdateCheckStatus.Failed));

        public Task<AssignedUpdate?> FetchAssignedAsync(CancellationToken ct)
            => Task.FromResult<AssignedUpdate?>(null);

        public Task ReportInstallAsync(string releaseId, string status, string detail, CancellationToken ct)
            => Task.CompletedTask;
    }

    internal sealed class SnapshotLog : ILogService
    {
        public void RegisterSensitiveValue(string value) { }
        public void Info(string scope, string message) { }
        public void Warn(string scope, string message) { }
        public void Error(string scope, string message, Exception? ex = null) { }
    }
}
