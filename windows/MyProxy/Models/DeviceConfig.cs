using System.Text.Json.Serialization;
using MyProxy.Core;

namespace MyProxy.Models;

public sealed class DeviceConfig
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = "";

    [JsonPropertyName("deviceToken")]
    public string DeviceToken { get; set; } = "";

    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = "";

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "windows";

    [JsonPropertyName("clientVersion")]
    public string ClientVersion { get; set; } = AppInfo.Version;

    [JsonPropertyName("boundAt")]
    public DateTimeOffset BoundAt { get; set; }
}
