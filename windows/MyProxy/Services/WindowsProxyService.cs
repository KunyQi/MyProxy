using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyProxy.Core;
using MyProxy.Models;

namespace MyProxy.Services;

public sealed class WindowsProxyService : ISystemProxyService
{
    private const string ProxyHost = "127.0.0.1";
    private const string LocalBypass = "<local>";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly IStorageService _storage;
    private readonly ILogService? _log;
    private readonly IWindowsProxyPlatform _platform;
    private ProxySnapshot? _snapshot;

    public WindowsProxyService(IStorageService storage, ILogService? log = null)
        : this(storage, WindowsProxyPlatform.Default, log)
    {
    }

    internal WindowsProxyService(
        IStorageService storage,
        IWindowsProxyPlatform platform,
        ILogService? log = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _log = log;
    }

    public bool IsManagedByMyProxy
    {
        get
        {
            (int proxyEnable, string proxyServer) = ReadCurrentProxyValues();
            if (proxyEnable != 1)
            {
                return false;
            }

            foreach (int port in ConnectionController.CandidatePorts)
            {
                if (string.Equals(proxyServer, $"{ProxyHost}:{port}", StringComparison.OrdinalIgnoreCase))
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
            // 备份已存在（上次崩溃遗留）时，权威原件是这个文件而不是当前注册表 ——
            // 当前值很可能还是 MyProxy 自己写进去的。备份读不出来时必须停止连接，
            // 不要把可能已被本程序修改的当前值冒充成用户原设置。
            _snapshot = ReadValidBackup(backupPath);
            return;
        }

        ProxySnapshot snapshot = ReadCurrentSnapshot();
        WriteJsonAtomic(backupPath, snapshot);
        _snapshot = ReadValidBackup(backupPath);
    }

    public async Task EnableAsync(string host, int port, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask.ConfigureAwait(false);

        string markerPath = MarkerFilePath;
        Directory.CreateDirectory(_storage.RuntimeDir);

        try
        {
            WriteJsonAtomic(markerPath, new AppliedMarker
            {
                Pid = Environment.ProcessId,
                Port = port,
                AppliedAt = DateTimeOffset.UtcNow
            });
        }
        catch (Exception ex)
        {
            throw new MyProxyException(
                ErrorCodeMessages.Get(ErrorCode.ProxyApplyFailed),
                ErrorCode.ProxyApplyFailed,
                ex);
        }

        try
        {
            WriteProxyValues(
                proxyEnable: 1,
                proxyServer: $"{host}:{port}",
                proxyOverride: LocalBypass,
                autoConfigUrl: "",
                autoDetect: false);
            RefreshWinInet();
        }
        catch (Exception ex)
        {
            throw new MyProxyException(
                ErrorCodeMessages.Get(ErrorCode.ProxyApplyFailed),
                ErrorCode.ProxyApplyFailed,
                ex);
        }
    }

    public async Task RestoreAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask.ConfigureAwait(false);

        string backupPath = BackupFilePath;
        string markerPath = MarkerFilePath;

        if (!File.Exists(backupPath))
        {
            if (File.Exists(markerPath))
            {
                if (IsCurrentProxyOwnedByMarker(markerPath))
                {
                    // 备份在运行期间丢失时，优先用内存里已经校验过的快照恢复。
                    RestoreSnapshotOrDisableFallback(
                        IsValidSnapshot(_snapshot) ? _snapshot : null, markerPath, backupPath);
                }
                else
                {
                    // A previous restore may have changed the registry but failed
                    // to refresh WinINET. Keep recovery evidence until refresh succeeds.
                    RefreshWinInet();
                    DeleteFileNoThrow(markerPath);
                }
            }

            return;
        }

        ProxySnapshot? snapshot;
        try
        {
            snapshot = ReadJsonFile<ProxySnapshot>(backupPath);
            if (!IsValidSnapshot(snapshot))
            {
                snapshot = null;
            }
        }
        catch (Exception ex)
        {
            _log?.Warn(nameof(WindowsProxyService), "proxy-backup.json 损坏，将执行安全回退");
            System.Diagnostics.Debug.WriteLine($"RestoreAsync backup corrupt: {ex.Message}");
            snapshot = null;
        }

        snapshot ??= IsValidSnapshot(_snapshot) ? _snapshot : null;

        // 写回快照同样需要所有权检查（与下面的分支和 TryRecoverFromCrash 一致）：
        // 连接期间第三方改动（企业 VPN 写 AutoConfigURL、用户自己改代理设置）
        // 不属于本程序，Stop 时静默覆盖它们是错的。
        if (!IsCurrentProxyOwnedByMarker(markerPath))
        {
            // Registry ownership alone does not prove that WinINET refreshed.
            RefreshWinInet();
            DeleteFileNoThrow(markerPath);
            DeleteFileNoThrow(backupPath);
            return;
        }

        RestoreSnapshotOrDisableFallback(snapshot, markerPath, backupPath);
    }

    public void PersistSnapshotForCrashRecovery()
    {
        string backupPath = BackupFilePath;
        if (File.Exists(backupPath))
        {
            _snapshot = ReadValidBackup(backupPath);
            return;
        }

        ProxySnapshot snapshot = _snapshot ?? ReadCurrentSnapshot();
        WriteJsonAtomic(backupPath, snapshot);
        _snapshot = ReadValidBackup(backupPath);
    }

    public bool TryRecoverFromCrash()
    {
        string backupPath = BackupFilePath;
        string markerPath = MarkerFilePath;

        bool markerExists = File.Exists(markerPath);
        bool backupExists = File.Exists(backupPath);

        if (!markerExists)
        {
            if (backupExists)
            {
                DeleteFileNoThrow(backupPath);
            }

            return false;
        }

        AppliedMarker? marker;
        try
        {
            marker = ReadJsonFile<AppliedMarker>(markerPath);
            if (marker is null || marker.Port is < 1 or > 65535)
            {
                marker = null;
            }
        }
        catch (Exception ex)
        {
            _log?.Warn(nameof(WindowsProxyService), "proxy-applied.marker 损坏，将按当前代理值恢复");
            System.Diagnostics.Debug.WriteLine($"TryRecoverFromCrash marker corrupt: {ex.Message}");
            marker = null;
        }

        (int proxyEnable, string proxyServer) = ReadCurrentProxyValues();
        bool proxyIsOurs = marker is null
            ? IsManagedByMyProxy
            : proxyEnable == 1 && string.Equals(
                proxyServer,
                $"{ProxyHost}:{marker.Port}",
                StringComparison.OrdinalIgnoreCase);

        if (!proxyIsOurs)
        {
            // 代理已不是我们设置的值：marker/backup 均已失去对应关系。
            // 上次回滚可能已写回注册表但刷新失败；再次确认 WinINET 已接收当前值。
            RefreshWinInet();
            DeleteFileNoThrow(markerPath);
            DeleteFileNoThrow(backupPath);
            return false;
        }

        ProxySnapshot? snapshot = null;
        if (backupExists)
        {
            try
            {
                snapshot = ReadJsonFile<ProxySnapshot>(backupPath);
                if (!IsValidSnapshot(snapshot))
                {
                    snapshot = null;
                }
            }
            catch (Exception ex)
            {
                _log?.Warn(nameof(WindowsProxyService), "proxy-backup.json 损坏，将禁用系统代理");
                System.Diagnostics.Debug.WriteLine($"TryRecoverFromCrash backup corrupt: {ex.Message}");
            }
        }

        RestoreSnapshotOrDisableFallback(snapshot, markerPath, backupPath);
        return true;
    }

    private string BackupFilePath => Path.Combine(_storage.DataRoot, "proxy-backup.json");

    private string MarkerFilePath => Path.Combine(_storage.RuntimeDir, "proxy-applied.marker");

    private ProxySnapshot ReadCurrentSnapshot() => _platform.ReadSnapshot();

    private static ProxySnapshot ReadValidBackup(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        if (!HasProperties(
                document.RootElement,
                "capturedAt",
                "proxyEnable",
                "proxyServer",
                "proxyOverride",
                "autoConfigURL",
                "autoDetect"))
        {
            throw new InvalidDataException("系统代理备份缺少必要字段。");
        }

        ProxySnapshot? snapshot = document.RootElement.Deserialize<ProxySnapshot>(JsonOptions);
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

    private (int ProxyEnable, string ProxyServer) ReadCurrentProxyValues()
    {
        ProxySnapshot snapshot = _platform.ReadSnapshot();
        return (snapshot.ProxyEnable, snapshot.ProxyServer);
    }

    private void WriteProxyValues(
        int proxyEnable,
        string proxyServer,
        string proxyOverride,
        string autoConfigUrl,
        bool autoDetect)
    {
        _platform.WriteSnapshot(new ProxySnapshot
        {
            ProxyEnable = proxyEnable,
            ProxyServer = proxyServer,
            ProxyOverride = proxyOverride,
            AutoConfigUrl = autoConfigUrl,
            AutoDetect = autoDetect
        });
    }

    private bool IsCurrentProxyOwnedByMarker(string markerPath)
    {
        if (!File.Exists(markerPath))
        {
            return false;
        }

        try
        {
            AppliedMarker? marker = ReadJsonFile<AppliedMarker>(markerPath);
            if (marker is null || marker.Port is < 1 or > 65535)
            {
                return IsManagedByMyProxy;
            }

            (int proxyEnable, string proxyServer) = ReadCurrentProxyValues();
            return proxyEnable == 1 && string.Equals(
                proxyServer,
                $"{ProxyHost}:{marker.Port}",
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return IsManagedByMyProxy;
        }
    }

    private void RestoreSnapshotOrDisableFallback(
        ProxySnapshot? snapshot,
        string markerPath,
        string backupPath)
    {
        if (snapshot is null)
        {
            _log?.Warn(nameof(WindowsProxyService), "无有效代理备份，将禁用 MyProxy 系统代理");
        }

        // Restore each auxiliary setting only while it still matches our applied
        // value. Other software may change PAC, WPAD or bypass independently of
        // the manual proxy, which still needs to be removed before xray stops.
        ProxySnapshot current = ReadCurrentSnapshot();
        WriteProxyValues(
            snapshot?.ProxyEnable ?? 0,
            snapshot?.ProxyServer ?? "",
            current.ProxyOverride == LocalBypass
                ? snapshot?.ProxyOverride ?? LocalBypass : current.ProxyOverride,
            current.AutoConfigUrl == ""
                ? snapshot?.AutoConfigUrl ?? "" : current.AutoConfigUrl,
            current.AutoDetect || (snapshot?.AutoDetect ?? false));

        // 只有注册表变更已通知 WinINET 后，才能删除崩溃恢复证据。
        RefreshWinInet();
        DeleteFileNoThrow(markerPath);
        DeleteFileNoThrow(backupPath);
        _snapshot = null;
    }

    private static bool IsValidSnapshot(ProxySnapshot? snapshot)
    {
        return snapshot is not null &&
               snapshot.CapturedAt != default &&
               snapshot.ProxyEnable is 0 or 1 &&
               snapshot.ProxyServer is not null &&
               snapshot.ProxyOverride is not null &&
               snapshot.AutoConfigUrl is not null;
    }

    private void RefreshWinInet() => _platform.RefreshWinInet();

    private static void WriteJsonAtomic<T>(string path, T value)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(value, JsonOptions);
        string tmpPath = path + ".tmp";
        File.WriteAllText(tmpPath, json, new UTF8Encoding(false));
        File.Move(tmpPath, path, overwrite: true);
    }

    private static T? ReadJsonFile<T>(string path)
    {
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

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

    private sealed class AppliedMarker
    {
        [JsonPropertyName("pid")]
        public int Pid { get; set; }

        [JsonPropertyName("port")]
        public int Port { get; set; }

        [JsonPropertyName("appliedAt")]
        public DateTimeOffset AppliedAt { get; set; }
    }
}
