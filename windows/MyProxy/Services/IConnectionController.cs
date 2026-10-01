using MyProxy.Core;
using MyProxy.Models;

namespace MyProxy.Services;

public interface IConnectionController
{
    AppState State { get; }
    ProxyMode Mode { get; }
    void InitializeMode(ProxyMode mode);
    bool IsSwitching { get; }
    int? LatencyMs { get; }
    ErrorCode LastErrorCode { get; }
    string LastErrorMessage { get; }

    string? LastSwitchErrorMessage { get; }

    /// <summary>
    /// 最近一次心跳因服务器证书校验失败而失败，之后还没有成功过。数据面照常可用，
    /// 但配置更新与吊销都到不了这台设备；界面据此在「已连接」下说明原因。
    /// </summary>
    bool ServerIdentityUnverified => false;

    /// <summary>「检测连接」正在进行。UI 据此把按钮置为进行态。</summary>
    bool IsChecking { get; }

    /// <summary>最近一次自检的结论；从未检测过、或已断开后清空时为 null。</summary>
    ConnectionCheckResult? LastCheck { get; }

    void MarkBound();
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task SwitchModeAsync(ProxyMode mode, CancellationToken ct);
    Task RebindAsync(CancellationToken ct);

    /// <summary>
    /// 按需自检：实测延迟并核对出口归属。仅 Connected 且未在切换时可用，
    /// 不参与 LKG 提升——它只报告，不改变任何已验证状态。
    /// </summary>
    Task<ConnectionCheckResult> CheckConnectionAsync(CancellationToken ct);

    event Action? StateChanged;
    event Action? ModeChanged;
    event Action? BindingRequired;

    /// <summary><see cref="IsChecking"/> 或 <see cref="LastCheck"/> 变化。已封送到 UI 线程。</summary>
    event Action? CheckChanged;

    /// <summary>最近一拍的瞬时速率，字节/秒。未连接或采集不可用时为 <see cref="TrafficRate.Zero"/>。</summary>
    TrafficRate TrafficRate { get; }

    /// <summary>最近若干拍的速率样本，从旧到新，供 Sparkline 绘制。</summary>
    IReadOnlyList<TrafficRate> TrafficSamples { get; }

    /// <summary>速率更新。已封送到 UI 线程。</summary>
    event Action? TrafficChanged;

    /// <summary>
    /// 每次心跳成功后触发，带上服务端返回的 release 身份与 feature flags。
    /// Update Plane 的处理挂在这个事件上，控制器自身不认识它。
    /// </summary>
    event Action<HeartbeatResult>? HeartbeatReceived;

    /// <summary>
    /// 挂起/恢复流量采样。界面不可见时必须挂起：每一拍采样都要起一个 xray 子进程，
    /// 而窗口藏进托盘后没有任何人在看那张图。
    ///
    /// 恢复时监视器会被重置——挂起期间计数器仍在增长，留着旧基准会让恢复后的第一拍
    /// 把整段挂起时间的累计量算成一次速率。
    /// </summary>
    void SetTrafficSamplingSuspended(bool suspended);
}
