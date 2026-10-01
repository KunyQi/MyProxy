using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyProxy;
using MyProxy.Core;
using MyProxy.Models;

namespace MyProxy.Services;

public sealed class StorageService : IStorageService
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
    private readonly DpapiSecureStorage _secureStorage;
    private readonly SemaphoreSlim _settingsLock = new(1, 1);
    private ILogService? _log;

    public StorageService(string? dataRootOverride = null, ILogService? logService = null)
    {
        _dataRoot = dataRootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyProxy");
        _log = logService;

        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(RuntimeDir);
        Directory.CreateDirectory(LogDir);

        _secureStorage = new DpapiSecureStorage(_dataRoot, logService);
    }

    public string DataRoot => _dataRoot;

    public string RuntimeDir => Path.Combine(_dataRoot, "runtime");

    public string LogDir => Path.Combine(_dataRoot, "logs");

    public void AttachLogger(ILogService logService)
    {
        _log = logService;
        _secureStorage.AttachLogger(logService);
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
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            await WriteTextAtomicAsync(Path.Combine(_dataRoot, SettingsFileName), json, ct).ConfigureAwait(false);
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
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            await WriteTextAtomicAsync(Path.Combine(_dataRoot, SettingsFileName), json, ct).ConfigureAwait(false);
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
                clientInstanceId = Guid.NewGuid();
                settings.ClientInstanceId = clientInstanceId.ToString("D");
                string json = JsonSerializer.Serialize(settings, JsonOptions);
                await WriteTextAtomicAsync(Path.Combine(_dataRoot, SettingsFileName), json, ct).ConfigureAwait(false);
            }

            return clientInstanceId.ToString("D");
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    private AppSettings LoadSettingsCore()
    {
        string path = Path.Combine(_dataRoot, SettingsFileName);
        if (!File.Exists(path))
        {
            return new AppSettings();
        }

        try
        {
            string json = File.ReadAllText(path);
            AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (settings is null)
            {
                return WriteDefaultSettings(path);
            }

            return settings;
        }
        catch (Exception ex)
        {
            _log?.Warn(nameof(StorageService), "settings.json 损坏，已使用默认值覆盖");
            System.Diagnostics.Debug.WriteLine($"LoadSettings failed: {ex.Message}");
            return WriteDefaultSettings(path);
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
            _log?.Warn(nameof(StorageService), "config-cache.json 损坏，视为无缓存");
            System.Diagnostics.Debug.WriteLine($"LoadConfigCache failed: {ex.Message}");
            return null;
        }
    }

    public async Task SaveConfigCacheAsync(ConfigCacheEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string json = JsonSerializer.Serialize(entry, JsonOptions);
        await WriteTextAtomicAsync(Path.Combine(_dataRoot, ConfigCacheFileName), json, ct).ConfigureAwait(false);
    }

    public DeviceConfig? LoadDevice()
    {
        try
        {
            string? json = _secureStorage.ReadAsync(DeviceSecureName, CancellationToken.None)
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
            // 而 SaveDeviceAsync 会覆盖掉仍然有效的 device.dat，
            // 旧设备在服务端变成孤儿 active 占用名额。
            _log?.Warn(nameof(StorageService), "device.dat 内容无效，视为未绑定");
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

    public async Task DeleteDeviceAsync(CancellationToken ct)
    {
        await _secureStorage.DeleteAsync(DeviceSecureName, ct).ConfigureAwait(false);
    }

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
            _log?.Error(nameof(StorageService), "删除 device.dat 失败", ex);
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
            _log?.Error(nameof(StorageService), "删除 config-cache.json 失败", ex);
        }

        try
        {
            // 缓存的 feature flags 属于这台设备：重新绑定成另一台设备后，在它自己的第一次心跳
            // 之前不该沿用（否则第一次连接就按上一台设备的开关生成数据面）。清不掉只影响那一次
            // 连接，所以不让它把解绑本身判成失败。
            await UpdateSettingsAsync(settings => settings.FeatureFlags.Clear(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Warn(nameof(StorageService), $"清除缓存的 feature flags 失败：{ex.GetType().Name}");
        }

        if (firstError is not null)
        {
            throw firstError;
        }
    }

    private AppSettings WriteDefaultSettings(string path)
    {
        AppSettings defaults = new();
        string json = JsonSerializer.Serialize(defaults, JsonOptions);
        WriteTextAtomic(path, json);
        return defaults;
    }

    private static void WriteTextAtomic(string path, string json)
    {
        string tmpPath = path + ".tmp";
        File.WriteAllText(tmpPath, json, new UTF8Encoding(false));
        File.Move(tmpPath, path, overwrite: true);
    }

    private static async Task WriteTextAtomicAsync(string path, string json, CancellationToken ct)
    {
        string tmpPath = path + ".tmp";
        await File.WriteAllTextAsync(tmpPath, json, new UTF8Encoding(false), ct).ConfigureAwait(false);
        File.Move(tmpPath, path, overwrite: true);
    }
}
