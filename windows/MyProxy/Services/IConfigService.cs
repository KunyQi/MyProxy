using MyProxy.Models;

namespace MyProxy.Services;

public interface IConfigService
{
    Task<ConfigCacheEntry> GetConfigAsync(CancellationToken ct);
    Task<HeartbeatResult> HeartbeatAsync(CancellationToken ct);
    Task PromoteCurrentConfigAsync(ServerProfile profile, long configVersion, CancellationToken ct);
}
