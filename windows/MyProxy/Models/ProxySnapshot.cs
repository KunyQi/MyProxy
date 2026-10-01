using System.Text.Json.Serialization;

namespace MyProxy.Models;

public sealed class ProxySnapshot
{
    [JsonPropertyName("capturedAt")]
    public DateTimeOffset CapturedAt { get; set; }

    [JsonPropertyName("proxyEnable")]
    public int ProxyEnable { get; set; }

    [JsonPropertyName("proxyServer")]
    public string ProxyServer { get; set; } = "";

    [JsonPropertyName("proxyOverride")]
    public string ProxyOverride { get; set; } = "";

    [JsonPropertyName("autoConfigURL")]
    public string AutoConfigUrl { get; set; } = "";

    [JsonPropertyName("autoDetect")]
    public bool AutoDetect { get; set; }
}
