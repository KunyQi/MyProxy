using System.Text.Json.Serialization;

namespace MyProxy.Models;

public sealed class ServerProfile
{
    [JsonPropertyName("server")]
    public string Server { get; set; } = "";

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("uuid")]
    public string Uuid { get; set; } = "";

    [JsonPropertyName("security")]
    public string Security { get; set; } = "reality";

    [JsonPropertyName("publicKey")]
    public string PublicKey { get; set; } = "";

    [JsonPropertyName("shortId")]
    public string ShortId { get; set; } = "";

    [JsonPropertyName("sni")]
    public string Sni { get; set; } = "";

    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; set; } = "chrome";

    [JsonPropertyName("flow")]
    public string Flow { get; set; } = "xtls-rprx-vision";

    [JsonPropertyName("spiderX")]
    public string SpiderX { get; set; } = "/";
}
