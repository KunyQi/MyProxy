using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyProxy.Models;

public sealed class XrayRootConfig
{
    [JsonPropertyName("log")]
    public LogConfig Log { get; set; } = new();

    [JsonPropertyName("dns")]
    public DnsConfig Dns { get; set; } = new();

    [JsonPropertyName("routing")]
    public RoutingConfig Routing { get; set; } = new();

    [JsonPropertyName("inbounds")]
    public List<InboundConfig> Inbounds { get; set; } = new();

    [JsonPropertyName("outbounds")]
    public List<OutboundConfig> Outbounds { get; set; } = new();

    [JsonPropertyName("policy")]
    public PolicyConfig Policy { get; set; } = new();

    /// <summary>统计开关。xray 要求一个**空对象**来打开计数器；为 null 时整段省略，统计不启用。</summary>
    [JsonPropertyName("stats")]
    public StatsConfig? Stats { get; set; }

    /// <summary>gRPC 查询服务。与 <see cref="Stats"/> 必须同时在或同时不在。</summary>
    [JsonPropertyName("api")]
    public XrayApiConfig? Api { get; set; }
}

/// <summary>
/// 有意为空：`"stats": {}` 是 xray 打开流量计数器的写法，没有任何字段可填。
/// </summary>
public sealed class StatsConfig
{
}

public sealed class XrayApiConfig
{
    [JsonPropertyName("tag")]
    public string Tag { get; set; } = "";

    [JsonPropertyName("services")]
    public List<string> Services { get; set; } = new();
}

public sealed class LogConfig
{
    [JsonPropertyName("access")]
    public string Access { get; set; } = "none";

    [JsonPropertyName("dnsLog")]
    public bool DnsLog { get; set; }

    [JsonPropertyName("error")]
    public string Error { get; set; } = "";

    [JsonPropertyName("loglevel")]
    public string LogLevel { get; set; } = "warning";

    [JsonPropertyName("maskAddress")]
    public string MaskAddress { get; set; } = "";
}

public sealed class DnsConfig
{
    [JsonPropertyName("servers")]
    public List<DnsServer> Servers { get; set; } = new();

    [JsonPropertyName("queryStrategy")]
    public string QueryStrategy { get; set; } = "UseIP";

    [JsonPropertyName("disableCache")]
    public bool DisableCache { get; set; }

    [JsonPropertyName("enableParallelQuery")]
    public bool EnableParallelQuery { get; set; }
}

[JsonConverter(typeof(DnsServerJsonConverter))]
public abstract class DnsServer
{
}

public sealed class DnsServerEntry : DnsServer
{
    [JsonPropertyName("address")]
    public string Address { get; set; } = "";

    [JsonPropertyName("domains")]
    public List<string>? Domains { get; set; }

    [JsonPropertyName("expectIPs")]
    public List<string>? ExpectIPs { get; set; }

    [JsonPropertyName("skipFallback")]
    public bool SkipFallback { get; set; }
}

public sealed class DnsServerAddress : DnsServer
{
    public string Value { get; set; } = "";
}

public sealed class DnsServerJsonConverter : JsonConverter<DnsServer>
{
    public override DnsServer Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new DnsServerAddress { Value = reader.GetString() ?? "" };
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var entryOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            return JsonSerializer.Deserialize<DnsServerEntry>(ref reader, entryOptions) ?? new DnsServerEntry();
        }

        throw new JsonException($"无法反序列化 DNS 服务器项，当前 Token：{reader.TokenType}");
    }

    public override void Write(Utf8JsonWriter writer, DnsServer value, JsonSerializerOptions options)
    {
        if (value is DnsServerAddress address)
        {
            writer.WriteStringValue(address.Value);
            return;
        }

        if (value is DnsServerEntry entry)
        {
            writer.WriteStartObject();
            writer.WriteString("address", entry.Address);

            if (entry.Domains is not null)
            {
                writer.WritePropertyName("domains");
                writer.WriteStartArray();
                foreach (string domain in entry.Domains)
                {
                    writer.WriteStringValue(domain);
                }
                writer.WriteEndArray();
            }

            if (entry.ExpectIPs is not null)
            {
                writer.WritePropertyName("expectIPs");
                writer.WriteStartArray();
                foreach (string expectIp in entry.ExpectIPs)
                {
                    writer.WriteStringValue(expectIp);
                }
                writer.WriteEndArray();
            }

            if (entry.SkipFallback)
            {
                writer.WriteBoolean("skipFallback", true);
            }
            writer.WriteEndObject();
            return;
        }

        throw new JsonException($"不支持的 DnsServer 类型：{value.GetType().FullName}");
    }
}

public sealed class RoutingConfig
{
    [JsonPropertyName("domainStrategy")]
    public string DomainStrategy { get; set; } = "AsIs";

    [JsonPropertyName("rules")]
    public List<RoutingRule> Rules { get; set; } = new();
}

public sealed class RoutingRule
{
    [JsonPropertyName("outboundTag")]
    public string OutboundTag { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "field";

    [JsonPropertyName("domain")]
    public List<string>? Domain { get; set; }

    [JsonPropertyName("ip")]
    public List<string>? Ip { get; set; }

    [JsonPropertyName("protocol")]
    public List<string>? Protocol { get; set; }

    [JsonPropertyName("inboundTag")]
    public List<string>? InboundTag { get; set; }
}

public sealed class InboundConfig
{
    [JsonPropertyName("tag")]
    public string Tag { get; set; } = "";

    [JsonPropertyName("listen")]
    public string Listen { get; set; } = "";

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = "";

    [JsonPropertyName("settings")]
    public InboundSettings Settings { get; set; } = new();

    /// <summary>可空：统计用的 dokodemo-door inbound 不嗅探，整段省略。</summary>
    [JsonPropertyName("sniffing")]
    public SniffingConfig? Sniffing { get; set; } = new();
}

public sealed class InboundSettings
{
    [JsonPropertyName("timeout")]
    public int Timeout { get; set; }

    /// <summary>仅 dokodemo-door 用。http inbound 不设，靠 WhenWritingNull 省略。</summary>
    [JsonPropertyName("address")]
    public string? Address { get; set; }
}

public sealed class SniffingConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("destOverride")]
    public List<string> DestOverride { get; set; } = new();

    [JsonPropertyName("metadataOnly")]
    public bool MetadataOnly { get; set; }

    [JsonPropertyName("routeOnly")]
    public bool RouteOnly { get; set; }
}

public sealed class OutboundConfig
{
    [JsonPropertyName("tag")]
    public string Tag { get; set; } = "";

    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = "";

    [JsonPropertyName("settings")]
    public object? Settings { get; set; }

    [JsonPropertyName("streamSettings")]
    public RealityStreamSettings? StreamSettings { get; set; }
}

public sealed class FreedomSettings
{
    [JsonPropertyName("domainStrategy")]
    public string DomainStrategy { get; set; } = "AsIs";
}

public sealed class BlackholeSettings
{
}

public sealed class VlessOutboundSettings
{
    [JsonPropertyName("vnext")]
    public List<VnextEntry> Vnext { get; set; } = new();
}

public sealed class VnextEntry
{
    [JsonPropertyName("address")]
    public string Address { get; set; } = "";

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("users")]
    public List<VlessUser> Users { get; set; } = new();
}

public sealed class VlessUser
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("flow")]
    public string Flow { get; set; } = "";

    [JsonPropertyName("encryption")]
    public string Encryption { get; set; } = "none";

    [JsonPropertyName("level")]
    public int Level { get; set; }
}

public sealed class RealityStreamSettings
{
    [JsonPropertyName("network")]
    public string Network { get; set; } = "tcp";

    [JsonPropertyName("security")]
    public string Security { get; set; } = "reality";

    [JsonPropertyName("realitySettings")]
    public RealitySettings RealitySettings { get; set; } = new();
}

public sealed class RealitySettings
{
    [JsonPropertyName("show")]
    public bool Show { get; set; }

    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; set; } = "";

    [JsonPropertyName("serverName")]
    public string ServerName { get; set; } = "";

    [JsonPropertyName("publicKey")]
    public string PublicKey { get; set; } = "";

    [JsonPropertyName("shortId")]
    public string ShortId { get; set; } = "";

    [JsonPropertyName("spiderX")]
    public string SpiderX { get; set; } = "";
}

public sealed class PolicyConfig
{
    [JsonPropertyName("levels")]
    public Dictionary<string, PolicyLevel> Levels { get; set; } = new();

    [JsonPropertyName("system")]
    public PolicySystem System { get; set; } = new();
}

public sealed class PolicyLevel
{
    [JsonPropertyName("statsUserDownlink")]

    public bool StatsUserDownlink { get; set; }

    [JsonPropertyName("statsUserOnline")]
    public bool StatsUserOnline { get; set; }

    [JsonPropertyName("statsUserUplink")]
    public bool StatsUserUplink { get; set; }
}

public sealed class PolicySystem
{
    [JsonPropertyName("statsInboundDownlink")]
    public bool StatsInboundDownlink { get; set; }

    [JsonPropertyName("statsInboundUplink")]
    public bool StatsInboundUplink { get; set; }

    [JsonPropertyName("statsOutboundDownlink")]
    public bool StatsOutboundDownlink { get; set; }

    [JsonPropertyName("statsOutboundUplink")]
    public bool StatsOutboundUplink { get; set; }
}
