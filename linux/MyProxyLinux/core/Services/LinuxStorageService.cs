using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyProxy.Core;
using MyProxy.Models;

namespace MyProxy.Services;

/// <summary>
/// Linux 端的 <see cref="IStorageService"/>：设置、LastKnownGood 配置缓存、设备凭据。
///
/// <para>
/// 与 Windows 端 <c>StorageService</c> <b>行为逐条对应</b>（文件名、原子写、
/// 损坏时的降级、<see cref="ClearBindingAsync"/> 连缓存 feature flags 一起清），
/// 差别只在两处平台事实：
/// </para>
/// <list type="number">
/// <item>目录来自 XDG（<see cref="LinuxPaths"/>），不是 <c>%LocalAppData%</c>；</item>
/// <item>凭据交给 <see cref="LinuxSecureStorage"/>（0600 文件），不是 DPAPI。</item>
/// </list>
///
/// <para>
/// 分开写而不是共用一份的理由：这个类型的职责就是「平台上的秘密放在哪」，
/// 它<b>是</b>平台层。共享的是契约（<see cref="IStorageService"/>）与数据形状
/// （<c>Models/</c>），那两样两边编译的是同一份源码。
/// </para>
/// </summary>
public sealed class LinuxStorageService : IStorageService
{
    private const string SettingsFileName = "settings.json";
    private const string ConfigCacheFileName = "config-cache.json";
    private const string DeviceSecureName = "device";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _dataRoot;
    private readonly ISecureStorage _secureStorage;
    private readonly SemaphoreSlim _settingsLock = new(1, 1);
    private ILogService? _log;

    public LinuxStorageService(
        string? dataRootOverride = null,
        ILogService? logService = null,
        ISecureStorage? secureStorage = null)
    {
        _dataRoot = dataRootOverride ?? LinuxPaths.DataRoot;
        _log = logService;

        LinuxFileSecurity.EnsurePrivateDirectory(_dataRoot);
        LinuxFileSecurity.EnsurePrivateDirectory(RuntimeDir);
        LinuxFileSecurity.EnsurePrivateDirectory(LogDir);

        _secureStorage = secureStorage ?? new LinuxSecureStorage(_dataRoot, logService);
    }

    public string DataRoot => _dataRoot;

    public string RuntimeDir => Path.Combine(_dataRoot, "runtime");

    public string LogDir => Path.Combine(_dataRoot, "logs");

    public void AttachLogger(ILogService logService)
    {
        _log = logService;

        // 凭据存储是它的一部分（在同一个数据根下），日志也要接过去：
        // 「权限曾经宽过」「读不出来」这类话只有它说得出来。
        if (_secureStorage is LinuxSecureStorage linux)
        {
            linux.AttachLogger(logService);
        }
    }

    public AppSettings LoadSettings()
    {
        _settingsLock.Wait();
        try
        {
            return LoadSettingsCore();
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _settingsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            WriteSettingsCore(settings);
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    public async Task UpdateSettingsAsync(Action<AppSettings> update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _settingsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            AppSettings settings = LoadSettingsCore();
            update(settings);
            WriteSettingsCore(settings);
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    public async Task<string> GetOrCreateClientInstanceIdAsync(CancellationToken ct)
    {
        await _settingsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            AppSettings settings = LoadSettingsCore();
            if (!Guid.TryParseExact(settings.ClientInstanceId, "D", out Guid clientInstanceId))
            {
                // claim 的幂等靠这个值：同一个实例重放配对码时服务端能推导出同一个
                // Device Token，不会多建一台设备（见 server 端 _replay_claim）。
                clientInstanceId = Guid.NewGuid();
                settings.ClientInstanceId = clientInstanceId.ToString("D");
                WriteSettingsCore(settings);
            }

            return clientInstanceId.ToString("D");
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    public ConfigCacheEntry? LoadConfigCache()
    {
        string path = Path.Combine(_dataRoot, ConfigCacheFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ConfigCacheEntry>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            _log?.Warn(nameof(LinuxStorageService), "config-cache.json 损坏，视为无缓存");
            System.Diagnostics.Debug.WriteLine($"LoadConfigCache failed: {ex.Message}");
            return null;
        }
    }

    public Task SaveConfigCacheAsync(ConfigCacheEntry entry, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(entry);

        string json = JsonSerializer.Serialize(entry, JsonOptions);
        LinuxFileSecurity.WriteAtomic(Path.Combine(_dataRoot, ConfigCacheFileName), json);
        return Task.CompletedTask;
    }

    public DeviceConfig? LoadDevice()
    {
        try
        {
            string? json = _secureStorage
                .ReadAsync(DeviceSecureName, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonSerializer.Deserialize<DeviceConfig>(json, JsonOptions);
        }
        catch (SecureStorageReadException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 内容损坏（反序列化失败）与读不出来是两回事：前者确实没有可用绑定，
            // 后者必须向上抛，否则一次瞬时 IOException 就会把用户送回配对页，
            // 而 SaveDeviceAsync 会覆盖掉仍然有效的 device.json。
            _log?.Warn(nameof(LinuxStorageService), "device.json 内容无效，视为未绑定");
            System.Diagnostics.Debug.WriteLine($"LoadDevice failed: {ex.Message}");
            return null;
        }
    }

    public async Task SaveDeviceAsync(DeviceConfig device, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        string json = JsonSerializer.Serialize(device, JsonOptions);
        await _secureStorage.SaveAsync(DeviceSecureName, json, ct).ConfigureAwait(false);
    }

    public Task DeleteDeviceAsync(CancellationToken ct)
        => _secureStorage.DeleteAsync(DeviceSecureName, ct);

    public async Task ClearBindingAsync(CancellationToken ct)
    {
        Exception? firstError = null;

        try
        {
            await DeleteDeviceAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            firstError = ex;
            _log?.Error(nameof(LinuxStorageService), "删除 device.json 失败", ex);
        }

        try
        {
            string cachePath = Path.Combine(_dataRoot, ConfigCacheFileName);
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }
        }
        catch (Exception ex)
        {
            firstError ??= ex;
            _log?.Error(nameof(LinuxStorageService), "删除 config-cache.json 失败", ex);
        }

        try
        {
            // 缓存的 feature flags 属于这台设备：重新绑定成另一台设备后，在它自己的
            // 第一次心跳之前不该沿用（否则第一次连接就按上一台设备的开关生成数据面）。
            // 清不掉只影响那一次连接，所以不让它把解绑本身判成失败。
            await UpdateSettingsAsync(settings => settings.FeatureFlags.Clear(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Warn(nameof(LinuxStorageService), $"清除缓存的 feature flags 失败：{ex.GetType().Name}");
        }

        if (firstError is not null)
        {
            throw firstError;
        }
    }

    private AppSettings LoadSettingsCore()
    {
        string path = Path.Combine(_dataRoot, SettingsFileName);
        if (!File.Exists(path))
        {
            return WriteDefaultSettings(path);
        }

        try
        {
            string json = File.ReadAllText(path);
            AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            return settings ?? WriteDefaultSettings(path);
        }
        catch (Exception ex)
        {
            _log?.Warn(nameof(LinuxStorageService), "settings.json 损坏，已使用默认值覆盖");
            System.Diagnostics.Debug.WriteLine($"LoadSettings failed: {ex.Message}");
            return WriteDefaultSettings(path);
        }
    }

    private void WriteSettingsCore(AppSettings settings)
        => LinuxFileSecurity.WriteAtomic(
            Path.Combine(_dataRoot, SettingsFileName),
            JsonSerializer.Serialize(settings, JsonOptions));

    private AppSettings WriteDefaultSettings(string path)
    {
        AppSettings defaults = new();
        LinuxFileSecurity.WriteAtomic(path, JsonSerializer.Serialize(defaults, JsonOptions));
        return defaults;
    }
}
