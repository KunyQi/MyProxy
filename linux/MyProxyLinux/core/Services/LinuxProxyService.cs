using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyProxy.Core;
using MyProxy.Models;

namespace MyProxy.Services;

/// <summary>
/// Linux 端的系统代理所有权，与 Windows 端 <c>WindowsProxyService</c> 逐条对应。
///
/// <para>
/// Windows 改的是注册表里的四个值，这里改的是两套桌面设置（GNOME 的 gsettings、
/// KDE 的 kioslaverc）外加两个环境变量文件。<b>形状不同，规矩相同</b>：
/// </para>
///
/// <list type="number">
/// <item>连接前先把原设置抓成快照落盘（<c>proxy-backup.json</c>），再写下
/// 「本轮是我们改的」标记（<c>runtime/proxy-applied.marker</c>）；</item>
/// <item>停止时<b>只</b>恢复自己改过的那部分——连接期间第三方（企业 VPN、用户手动
/// 改设置）动过的值一律原样保留；</item>
/// <item>恢复的顺序是「先写回设置，再删标记」，<b>删除证据永远在最后一步</b>。
/// 反过来的话，写回失败时证据已经没了，用户的代理就永远留在那个死掉的本地端口上；</item>
/// <item>没有快照可恢复时降级为「关掉代理」，而不是「什么都不做」。</item>
/// </list>
///
/// <para>
/// 与 Windows 的一处<b>必然差别</b>：那边 <c>InternetSetOption</c> 之后设置才生效，
/// 所以恢复流程里有一次显式的 <c>RefreshWinInet()</c>；这里 <c>gsettings set</c> 自己
/// 就是即时生效的，不需要刷新动作。快照与标记文件的读写顺序因此完全一致，
/// 唯一被删掉的是那个刷新步骤。
/// </para>
/// </summary>
public sealed class LinuxProxyService : ISystemProxyService
{
    private const string ProxyHost = "127.0.0.1";
    private const string GnomeBackend = "gnome";
    private const string KdeBackend = "kde";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly IStorageService _storage;
    private readonly ILogService? _log;
    private readonly ILinuxProxyPlatform _platform;
    private readonly string _configHome;
    private LinuxProxySnapshot? _snapshot;

    /// <summary>本轮实际写出去的后端与端口。恢复时按它判断「现在的值还是不是我们的」。</summary>
    private List<string> _appliedBackends = new();
    private int _appliedPort;

    public LinuxProxyService(IStorageService storage, ILogService? log = null)
        : this(storage, new LinuxProxyPlatform(), log, configHomeOverride: null)
    {
    }

    internal LinuxProxyService(
        IStorageService storage,
        ILinuxProxyPlatform platform,
        ILogService? log = null,
        string? configHomeOverride = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _log = log;
        // 环境变量文件必须落在 $XDG_CONFIG_HOME 下（systemd 只读那里），而测试不能
        // 往真实的家目录里写东西，所以位置可注入。
        _configHome = configHomeOverride ?? LinuxPaths.XdgConfigHome;
    }

    private string BackupFilePath => Path.Combine(_storage.DataRoot, "proxy-backup.json");

    private string MarkerFilePath => Path.Combine(_storage.RuntimeDir, "proxy-applied.marker");

    /// <summary>systemd 用户会话的环境变量文件（新启动的用户服务与图形会话会读它）。</summary>
    private string EnvironmentDropInPath => Path.Combine(
        _configHome, "environment.d", $"{LinuxPaths.AppName}.conf");

    /// <summary>给当前 shell 直接 <c>source</c> 用的一份。</summary>
    private string ShellEnvironmentPath => Path.Combine(
        _configHome, LinuxPaths.AppName, "proxy.env");

    public bool IsManagedByMyProxy
    {
        get
        {
            foreach (int port in ConnectionController.CandidatePorts)
            {
                if (GnomeAvailable() && GnomeProxyCommands.PointsAt(ReadGnome(), ProxyHost, port))
                {
                    return true;
                }

                if (KdeAvailable() && KdeProxyCommands.PointsAt(ReadKde(), ProxyHost, port))
                {
                    return true;
                }

                if (EnvironmentFilePointsAt(port))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public void CaptureCurrentSettings()
    {
        string backupPath = BackupFilePath;
        if (File.Exists(backupPath))
        {
            // 备份已存在（上次崩溃遗留）时，权威原件是这份文件而不是当前设置——
            // 当前值很可能还是 MyProxy 自己写进去的。与 Windows 端同一处理。
            _snapshot = ReadValidBackup(backupPath);
            WarnAboutUntouchedSocksProxy(_snapshot);
            return;
        }

        LinuxProxySnapshot snapshot = ReadCurrentSnapshot();
        WriteJsonAtomic(backupPath, snapshot);
        _snapshot = ReadValidBackup(backupPath);
        WarnAboutUntouchedSocksProxy(snapshot);
    }

    /// <summary>
    /// 用户本来配了 SOCKS 代理时如实提醒一次。
    ///
    /// <para>
    /// 我们不写桌面的 SOCKS 设置（理由见 <see cref="GnomeProxyCommands.EnableCommands"/>），
    /// 所以读那个设置的程序在连接期间<b>不会</b>经过隧道。沉默地让它发生是最坏的选择：
    /// 用户以为「全局代理」把所有流量都收进去了。日志里说清楚，是这套限制里我们
    /// 唯一还能做的事。
    /// </para>
    /// </summary>
    private void WarnAboutUntouchedSocksProxy(LinuxProxySnapshot snapshot)
    {
        GnomeProxyState? gnome = snapshot.Gnome;
        bool gnomeSocks = gnome is not null
            && gnome.SocksPort > 0
            && gnome.SocksHost.Length > 0;

        bool kdeSocks = snapshot.Kde is { } kde
            && kde.TryGetValue("socksProxy", out string? socks)
            && socks.Length > 0;

        if (gnomeSocks || kdeSocks)
        {
            _log?.Warn(
                nameof(LinuxProxyService),
                "桌面配置了 SOCKS 代理：本程序只提供 HTTP 入站，不会改动它——"
                + "读该设置的程序（例如 Firefox）不会经过隧道");
        }
    }

    public Task EnableAsync(string host, int port, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        Directory.CreateDirectory(_storage.RuntimeDir);

        List<string> backends = _snapshot?.Backends is { Count: > 0 } captured
            ? new List<string>(captured)
            : DetectBackends();

        // 标记先落盘：它写着「系统代理现在指着我们」。写设置失败时这份证据还在，
        // 下一次启动的崩溃恢复据此把设置清干净。
        try
        {
            WriteJsonAtomic(MarkerFilePath, new LinuxAppliedMarker
            {
                Pid = Environment.ProcessId,
                Port = port,
                AppliedAt = DateTimeOffset.UtcNow,
                Backends = backends
            });
        }
        catch (Exception ex)
        {
            throw new MyProxyException(
                ErrorCodeMessages.Get(ErrorCode.ProxyApplyFailed), ErrorCode.ProxyApplyFailed, ex);
        }

        try
        {
            foreach (string backend in backends)
            {
                switch (backend)
                {
                    case GnomeBackend:
                        ApplyGnome(host, port);
                        break;
                    case KdeBackend:
                        ApplyKde(host, port);
                        break;
                }
            }

            WriteEnvironmentFiles(host, port);
        }
        catch (Exception ex)
        {
            throw new MyProxyException(
                ErrorCodeMessages.Get(ErrorCode.ProxyApplyFailed), ErrorCode.ProxyApplyFailed, ex);
        }

        _appliedBackends = backends;
        _appliedPort = port;
        return Task.CompletedTask;
    }

    public Task RestoreAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string backupPath = BackupFilePath;
        string markerPath = MarkerFilePath;

        if (!File.Exists(backupPath))
        {
            if (File.Exists(markerPath))
            {
                LinuxAppliedMarker? marker = ReadMarker(markerPath);
                if (IsCurrentProxyOwnedByMarker(marker))
                {
                    // 运行期间备份丢失时，使用内存里已校验的快照恢复。
                    // 这里传 null 会走「关掉代理」，把用户原来的公司代理清空（Windows 端
                    // 踩过这个坑，见 WindowsProxyService 的注释）。
                    RestoreSnapshotOrDisableFallback(
                        IsValidSnapshot(_snapshot) ? _snapshot : null, marker, markerPath, backupPath);
                }
                else
                {
                    RemoveEnvironmentFilesIfOurs();
                    DeleteFileNoThrow(markerPath);
                }
            }

            return Task.CompletedTask;
        }

        LinuxProxySnapshot? snapshot;
        try
        {
            snapshot = ReadJsonFile<LinuxProxySnapshot>(backupPath);
            if (!IsValidSnapshot(snapshot))
            {
                snapshot = null;
            }
        }
        catch (Exception ex)
        {
            _log?.Warn(nameof(LinuxProxyService), "proxy-backup.json 损坏，将执行安全回退");
            System.Diagnostics.Debug.WriteLine($"RestoreAsync backup corrupt: {ex.Message}");
            snapshot = null;
        }

        snapshot ??= IsValidSnapshot(_snapshot) ? _snapshot : null;

        LinuxAppliedMarker? currentMarker = ReadMarker(markerPath);

        // 写回快照同样需要所有权检查：连接期间第三方改动（企业 VPN 换 PAC、用户自己
        // 改设置）不属于本程序，Stop 时静默覆盖它们是错的。
        // 但那两个环境变量文件**永远**是我们的（固定路径、固定文件名），所有权判断管的是
        // 桌面设置，不管它们：留下它们，下次登录整个会话都会带着指向死端口的 http_proxy。
        if (!IsCurrentProxyOwnedByMarker(currentMarker))
        {
            RemoveEnvironmentFilesIfOurs();
            DeleteFileNoThrow(markerPath);
            DeleteFileNoThrow(backupPath);
            return Task.CompletedTask;
        }

        RestoreSnapshotOrDisableFallback(snapshot, currentMarker, markerPath, backupPath);
        return Task.CompletedTask;
    }

    public void PersistSnapshotForCrashRecovery()
    {
        string backupPath = BackupFilePath;
        if (File.Exists(backupPath))
        {
            _snapshot = ReadValidBackup(backupPath);
            return;
        }

        LinuxProxySnapshot snapshot = _snapshot ?? ReadCurrentSnapshot();
        WriteJsonAtomic(backupPath, snapshot);
        _snapshot = ReadValidBackup(backupPath);
    }

    public bool TryRecoverFromCrash()
    {
        string backupPath = BackupFilePath;
        string markerPath = MarkerFilePath;

        bool markerExists = File.Exists(markerPath);
        bool backupExists = File.Exists(backupPath);

        // 启动时本进程还没有连接，那两个环境变量文件若还在，只可能是上一次留下的：
        // 下面凡是不走 RestoreSnapshotOrDisableFallback（它自己会删）的分支都要删掉它们。
        if (!markerExists)
        {
            RemoveEnvironmentFilesIfOurs();
            if (backupExists)
            {
                DeleteFileNoThrow(backupPath);
            }

            return false;
        }

        LinuxAppliedMarker? marker = ReadMarker(markerPath);
        if (!IsCurrentProxyOwnedByMarker(marker))
        {
            // 桌面代理已不是我们设置的值：marker/backup 均已失去对应关系。
            RemoveEnvironmentFilesIfOurs();
            DeleteFileNoThrow(markerPath);
            DeleteFileNoThrow(backupPath);
            return false;
        }

        LinuxProxySnapshot? snapshot = null;
        if (backupExists)
        {
            try
            {
                snapshot = ReadJsonFile<LinuxProxySnapshot>(backupPath);
                if (!IsValidSnapshot(snapshot))
                {
                    snapshot = null;
                }
            }
            catch (Exception ex)
            {
                _log?.Warn(nameof(LinuxProxyService), "proxy-backup.json 损坏，将禁用 MyProxy 系统代理");
                System.Diagnostics.Debug.WriteLine($"TryRecoverFromCrash backup corrupt: {ex.Message}");
            }
        }

        RestoreSnapshotOrDisableFallback(snapshot, marker, markerPath, backupPath);
        return true;
    }

    /// <summary>
    /// 恢复的最后一步。每条设置<b>只在它当前仍然指向我们时</b>才写回：
    /// 连接期间被第三方改过的项保持不动。
    /// </summary>
    private void RestoreSnapshotOrDisableFallback(
        LinuxProxySnapshot? snapshot,
        LinuxAppliedMarker? marker,
        string markerPath,
        string backupPath)
    {
        if (snapshot is null)
        {
            _log?.Warn(nameof(LinuxProxyService), "无有效代理备份，将关闭 MyProxy 写入的代理设置");
        }

        int port = marker?.Port ?? _appliedPort;
        List<string> backends = marker?.Backends is { Count: > 0 } fromMarker
            ? fromMarker
            : (_appliedBackends.Count > 0 ? _appliedBackends : DetectBackends());

        try
        {
            foreach (string backend in backends)
            {
                switch (backend)
                {
                    case GnomeBackend:
                        RestoreGnome(snapshot?.Gnome, port);
                        break;
                    case KdeBackend:
                        RestoreKde(snapshot?.Kde, port);
                        break;
                }
            }

            RemoveEnvironmentFilesIfOurs();
        }
        catch (Exception ex)
        {
            // 恢复失败：**保留标记与备份**，让下一次启动继续尝试。宁可留下证据，
            // 也不要让系统代理指向一个已经死掉的本地端口。
            _log?.Error(nameof(LinuxProxyService), "恢复系统代理失败，保留标记供下次重试", ex);
            throw;
        }

        // 设置已经写回去了，这时才允许删除证据。
        DeleteFileNoThrow(markerPath);
        DeleteFileNoThrow(backupPath);
        _snapshot = null;
        _appliedBackends = new List<string>();
        _appliedPort = 0;
    }

    private void ApplyGnome(string host, int port)
    {
        string command = GsettingsCommand();
        foreach (string[] arguments in GnomeProxyCommands.EnableCommands(host, port))
        {
            RunChecked(command, arguments, IsEssential(arguments));
        }
    }

    private void RestoreGnome(GnomeProxyState? target, int port)
    {
        if (!GnomeAvailable())
        {
            return;
        }

        // 现在指向的已经不是我们了：那是别人写的值，不动它。逐段的判断在
        // GnomeProxyCommands.RestoreCommands 里，这里只负责把计划执行掉。
        GnomeProxyState current = ReadGnome();
        string command = GsettingsCommand();
        foreach (string[] arguments in GnomeProxyCommands.RestoreCommands(current, target, ProxyHost, port))
        {
            RunChecked(command, arguments, IsEssential(arguments));
        }
    }

    /// <summary>
    /// 这条命令是不是「必须成功」的。
    ///
    /// <para>
    /// 判断标准是<b>「失败了用户还能不能上网」</b>：把流量指向本地端口的
    /// <c>mode</c> 与 http/https 段是必须的；<c>ignore-hosts</c>、<c>use-same-proxy</c>、
    /// <c>autoconfig-url</c> 属于「最好是」——某些发行版的 schema 里这些键
    /// 根本不存在，为了它们让整个连接失败，是把一个可用的网络换成一次报错。
    /// （没有 socks 段：它根本不会被写，见 <see cref="GnomeProxyCommands.EnableCommands"/>。）
    /// </para>
    /// </summary>
    private static bool IsEssential(IReadOnlyList<string> arguments)
    {
        if (arguments.Count < 3)
        {
            return false;
        }

        string schema = arguments[1];
        string key = arguments[2];

        if (string.Equals(key, "mode", StringComparison.Ordinal))
        {
            return true;
        }

        bool essentialSection =
            string.Equals(schema, $"{GnomeProxyCommands.Schema}.http", StringComparison.Ordinal) ||
            string.Equals(schema, $"{GnomeProxyCommands.Schema}.https", StringComparison.Ordinal);

        return essentialSection && key is "host" or "port";
    }

    private void ApplyKde(string host, int port)
    {
        string command = KwriteCommand();
        foreach (string[] arguments in KdeProxyCommands.EnableCommands(host, port))
        {
            // ProxyType 与两个地址键是必须成功的；NoProxyFor 写不进去只记日志。
            // socks 不在这里：EnableCommands 从来不发它。
            bool required = arguments.Contains("ProxyType")
                || arguments.Contains("httpProxy")
                || arguments.Contains("httpsProxy");
            RunChecked(command, arguments, required);
        }
    }

    private void RestoreKde(IReadOnlyDictionary<string, string>? target, int port)
    {
        if (!KdeAvailable())
        {
            return;
        }

        IReadOnlyDictionary<string, string> current = ReadKde();
        string command = KwriteCommand();

        foreach (string[] arguments in KdeProxyCommands.RestoreCommands(current, target, ProxyHost, port))
        {
            RunChecked(command, arguments, required: true);
        }
    }

    private void WriteEnvironmentFiles(string host, int port)
    {
        string contents = LinuxProxyPlatform.BuildEnvironmentFile(
            host, port, GnomeProxyCommands.DefaultIgnoreHosts);

        _platform.WriteEnvironmentFile(EnvironmentDropInPath, contents);
        _platform.WriteEnvironmentFile(ShellEnvironmentPath, contents);

        if (_snapshot is not null)
        {
            _snapshot.EnvironmentFileWritten = true;
        }
    }

    private void RemoveEnvironmentFilesIfOurs()
    {
        // 只删自己写的那两个文件：别的程序往 environment.d 里放的东西与我们无关。
        _platform.DeleteEnvironmentFile(EnvironmentDropInPath);
        _platform.DeleteEnvironmentFile(ShellEnvironmentPath);

        if (_snapshot is not null)
        {
            _snapshot.EnvironmentFileWritten = false;
        }
    }

    private bool EnvironmentFilePointsAt(int port)
    {
        string? contents = _platform.ReadEnvironmentFile(EnvironmentDropInPath)
            ?? _platform.ReadEnvironmentFile(ShellEnvironmentPath);
        return contents is not null
            && contents.Contains($"{ProxyHost}:{port}", StringComparison.Ordinal);
    }

    private LinuxProxySnapshot ReadCurrentSnapshot()
    {
        List<string> backends = DetectBackends();
        return new LinuxProxySnapshot
        {
            CapturedAt = DateTimeOffset.UtcNow,
            Backends = backends,
            Gnome = backends.Contains(GnomeBackend) ? ReadGnome() : null,
            Kde = backends.Contains(KdeBackend) ? new Dictionary<string, string>(ReadKde()) : null,
            EnvironmentFileWritten = File.Exists(EnvironmentDropInPath)
                || File.Exists(ShellEnvironmentPath)
        };
    }

    private List<string> DetectBackends()
    {
        var backends = new List<string>();
        if (GnomeAvailable())
        {
            backends.Add(GnomeBackend);
        }

        if (KdeAvailable())
        {
            backends.Add(KdeBackend);
        }

        return backends;
    }

    private bool GnomeAvailable() => _platform.CommandExists(GsettingsCommand());

    private bool KdeAvailable() => _platform.CommandExists(KwriteCommand());

    /// <summary>gsettings 在 PATH 里的名字是固定的，不存在版本分歧。</summary>
    private static string GsettingsCommand() => "gsettings";

    /// <summary>KDE 5 与 6 的工具名不同，优先用 6。</summary>
    private string KwriteCommand()
        => _platform.CommandExists("kwriteconfig6") ? "kwriteconfig6" : "kwriteconfig5";

    private string KreadCommand()
        => _platform.CommandExists("kreadconfig6") ? "kreadconfig6" : "kreadconfig5";

    private GnomeProxyState ReadGnome()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string command = GsettingsCommand();

        foreach ((string schema, string key, _) in GnomeProxyCommands.ReadSpecs)
        {
            var result = _platform.Run(
                command, new[] { "get", schema, key }, TimeSpan.FromSeconds(5));

            values[GnomeProxyCommands.Key(schema, key)] =
                result is { ExitCode: 0 } success ? success.StandardOutput.Trim() : "";
        }

        return GnomeProxyCommands.Parse(values);
    }

    private IReadOnlyDictionary<string, string> ReadKde()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string command = KreadCommand();

        foreach (string key in KdeProxyCommands.Keys)
        {
            var result = _platform.Run(
                command,
                new[] { "--file", KdeProxyCommands.File, "--group", KdeProxyCommands.Group, "--key", key },
                TimeSpan.FromSeconds(5));

            if (result is { ExitCode: 0 } success)
            {
                string? normalized = KdeProxyCommands.NormalizeReadValue(success.StandardOutput);
                if (normalized is not null)
                {
                    values[key] = normalized;
                }
            }
        }

        return values;
    }

    /// <summary>
    /// 执行一条系统代理命令。<paramref name="required"/> 为真时失败<b>抛出</b>——
    /// 那会让连接流程回滚、或者让恢复流程保留标记文件；
    /// 为假时只记日志，因为「直连名单没写进去」不该让一次连接失败。
    /// </summary>
    private void RunChecked(string command, string[] arguments, bool required)
    {
        var result = _platform.Run(command, arguments, TimeSpan.FromSeconds(5));
        string rendered = $"{command} {string.Join(' ', arguments)}";

        if (result is null)
        {
            if (required)
            {
                throw new MyProxyException(
                    ErrorCodeMessages.Get(ErrorCode.ProxyApplyFailed),
                    ErrorCode.ProxyApplyFailed,
                    new InvalidOperationException($"系统代理命令未能执行：{rendered}"));
            }

            _log?.Warn(nameof(LinuxProxyService), $"系统代理命令未能执行：{rendered}");
            return;
        }

        if (result.Value.ExitCode != 0)
        {
            if (required)
            {
                throw new MyProxyException(
                    ErrorCodeMessages.Get(ErrorCode.ProxyApplyFailed),
                    ErrorCode.ProxyApplyFailed,
                    new InvalidOperationException($"系统代理命令返回 {result.Value.ExitCode}：{rendered}"));
            }

            _log?.Warn(
                nameof(LinuxProxyService),
                $"系统代理命令返回 {result.Value.ExitCode}：{rendered}");
        }
    }

    private LinuxAppliedMarker? ReadMarker(string markerPath)
    {
        try
        {
            LinuxAppliedMarker? marker = ReadJsonFile<LinuxAppliedMarker>(markerPath);
            return marker is null || marker.Port is < 1 or > 65535 ? null : marker;
        }
        catch (Exception ex)
        {
            _log?.Warn(nameof(LinuxProxyService), "proxy-applied.marker 损坏，将按当前代理值恢复");
            System.Diagnostics.Debug.WriteLine($"ReadMarker failed: {ex.Message}");
            return null;
        }
    }

    private bool IsCurrentProxyOwnedByMarker(LinuxAppliedMarker? marker)
    {
        if (marker is null)
        {
            return IsManagedByMyProxy;
        }

        if (GnomeAvailable() && GnomeProxyCommands.PointsAt(ReadGnome(), ProxyHost, marker.Port))
        {
            return true;
        }

        if (KdeAvailable() && KdeProxyCommands.PointsAt(ReadKde(), ProxyHost, marker.Port))
        {
            return true;
        }

        return marker.Backends.Count == 0 && EnvironmentFilePointsAt(marker.Port);
    }

    private static bool IsValidSnapshot(LinuxProxySnapshot? snapshot)
        => snapshot is not null &&
           snapshot.CapturedAt != default &&
           snapshot.Backends is not null &&
           snapshot.Backends.All(backend => backend is GnomeBackend or KdeBackend) &&
           snapshot.Backends.Contains(GnomeBackend) == (snapshot.Gnome is not null) &&
           snapshot.Backends.Contains(KdeBackend) == (snapshot.Kde is not null) &&
           (snapshot.Gnome is null ||
            (snapshot.Gnome.Mode is not null &&
             snapshot.Gnome.HttpHost is not null &&
             snapshot.Gnome.HttpsHost is not null &&
             snapshot.Gnome.SocksHost is not null &&
             snapshot.Gnome.IgnoreHosts is not null &&
             snapshot.Gnome.IgnoreHosts.All(host => host is not null) &&
             snapshot.Gnome.AutoconfigUrl is not null)) &&
           (snapshot.Kde is null || snapshot.Kde.All(pair => pair.Key is not null && pair.Value is not null));

    private static LinuxProxySnapshot ReadValidBackup(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        if (!HasProperties(
                document.RootElement,
                "capturedAt",
                "backends",
                "gnome",
                "kde",
                "environmentFileWritten"))
        {
            throw new InvalidDataException("系统代理备份缺少必要字段。");
        }

        LinuxProxySnapshot? snapshot = document.RootElement.Deserialize<LinuxProxySnapshot>(JsonOptions);
        if (!IsValidSnapshot(snapshot))
        {
            throw new InvalidDataException("系统代理备份内容无效。");
        }

        return snapshot!;
    }

    private static bool HasProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        HashSet<string> present = element.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return names.All(present.Contains);
    }

    private static void WriteJsonAtomic<T>(string path, T value)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        LinuxFileSecurity.WriteAtomic(path, JsonSerializer.Serialize(value, JsonOptions));
    }

    private static T? ReadJsonFile<T>(string path)
        => JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);

    private static void DeleteFileNoThrow(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理失败不阻断启动/停止流程。
        }
    }
}
