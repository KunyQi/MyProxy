using MyProxy.Core;

namespace MyProxy.Gui.ViewModels;

/// <summary>
/// A small, UI-independent description of why the last status poll did not provide a snapshot.
/// The last daemon-reported state remains available internally, but the UI must not present it as
/// current while the daemon cannot be queried.
/// </summary>
internal sealed class DaemonAvailabilityState
{
    public bool IsUnavailable { get; private set; }

    public string StatusText { get; private set; } = "";

    public string Hint { get; private set; } = "";

    public AppState EffectiveState(AppState lastReportedState)
        => IsUnavailable ? AppState.Error : lastReportedState;

    public bool ShouldShowInFlightActivity(bool operationInFlight)
        => !IsUnavailable && operationInFlight;

    public void MarkUnavailable(DaemonUnavailablePresentation presentation)
    {
        IsUnavailable = true;
        StatusText = presentation.StatusText;
        Hint = presentation.Hint;
    }

    /// <summary>A valid status snapshot is the only event that clears the unavailable state.</summary>
    public void RestoreAfterValidStatus()
    {
        IsUnavailable = false;
        StatusText = "";
        Hint = "";
    }
}

internal readonly record struct DaemonUnavailablePresentation(string StatusText, string Hint);

internal enum PollCancellationDisposition
{
    IgnoreForGuiShutdown,
    MarkUnavailableAsTimeout
}

/// <summary>Pure presentation and cancellation rules used by the polling path and fault tests.</summary>
internal static class PollStatusFailurePolicy
{
    public static PollCancellationDisposition ClassifyCancellation(bool guiShutdownRequested)
        => guiShutdownRequested
            ? PollCancellationDisposition.IgnoreForGuiShutdown
            : PollCancellationDisposition.MarkUnavailableAsTimeout;

    public static DaemonUnavailablePresentation Timeout()
        => new(
            "连接状态未知",
            "后台服务状态轮询超时，连接状态未知。请稍后重试。");

    public static DaemonUnavailablePresentation Response(ErrorCode errorCode, string detail)
        => errorCode == ErrorCode.ApiUnreachable
            ? new(
                "后台不可用",
                "后台服务未运行。请先启动后台服务：systemctl --user start myproxy.service，或在终端执行 myproxy start。")
            : new("后台不可用", $"后台服务没有回应：{detail}");

    public static DaemonUnavailablePresentation UnexpectedFailure()
        => new(
            "连接状态未知",
            "后台服务状态查询失败，连接状态未知。请稍后重试。");
}
