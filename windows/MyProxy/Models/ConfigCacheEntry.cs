using System.Text.Json.Serialization;

namespace MyProxy.Models;

public sealed class ConfigCacheEntry
{
    [JsonPropertyName("configVersion")]
    public long ConfigVersion { get; set; }

    [JsonPropertyName("fetchedAt")]
    public DateTimeOffset FetchedAt { get; set; }

    [JsonPropertyName("verified")]
    public bool Verified { get; set; }

    [JsonPropertyName("profile")]
    public ServerProfile Profile { get; set; } = new();
}
