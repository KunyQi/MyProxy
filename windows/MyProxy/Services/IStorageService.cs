using MyProxy.Models;

namespace MyProxy.Services;

public interface IStorageService
{
    AppSettings LoadSettings();
    Task SaveSettingsAsync(AppSettings settings, CancellationToken ct);
    Task UpdateSettingsAsync(Action<AppSettings> update, CancellationToken ct);
    Task<string> GetOrCreateClientInstanceIdAsync(CancellationToken ct);
    ConfigCacheEntry? LoadConfigCache();
    Task SaveConfigCacheAsync(ConfigCacheEntry entry, CancellationToken ct);
    DeviceConfig? LoadDevice();
    Task SaveDeviceAsync(DeviceConfig device, CancellationToken ct);
    Task DeleteDeviceAsync(CancellationToken ct);
    Task ClearBindingAsync(CancellationToken ct);
    string DataRoot { get; }
    string RuntimeDir { get; }
    string LogDir { get; }
}
