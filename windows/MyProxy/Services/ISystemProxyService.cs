namespace MyProxy.Services;

/// <summary>
/// 系统代理的所有权：抓取改动前的设置、把自己写进去、再原样恢复。
///
/// <para>
/// 与 <see cref="IXrayService"/> 一样，这是「每个平台一份实现」的那一层。名称里没有
/// Windows，因为 Windows 端与 Linux 端实现的是同一件事：把系统级代理指向本机 xray
/// 的入站端口，并在停止时<b>只</b>恢复自己改过的那部分。
/// </para>
///
/// <para>
/// 两端的失败顺序是同一条规矩：<b>先恢复系统代理，只有恢复成功才停 xray</b>。
/// 恢复失败时故意保留 xray 与标记文件，因为系统代理指向一个已死的本地端口
/// 意味着整机断网（见 <c>ConnectionController.RollbackAsync</c>）。
/// </para>
/// </summary>
public interface ISystemProxyService
{
    /// <summary>连接前抓取当前设置。已存在上次崩溃遗留的备份时，以那份备份为权威。</summary>
    void CaptureCurrentSettings();

    Task EnableAsync(string host, int port, CancellationToken ct);

    Task RestoreAsync(CancellationToken ct);

    /// <summary>当前系统代理是否指向本程序的候选端口之一。</summary>
    bool IsManagedByMyProxy { get; }

    /// <summary>把快照落盘，供崩溃恢复使用。</summary>
    void PersistSnapshotForCrashRecovery();

    /// <summary>启动时处理上次崩溃留下的代理。返回本次是否真的恢复/清理过残留。</summary>
    bool TryRecoverFromCrash();
}
