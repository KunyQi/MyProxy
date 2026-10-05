package com.myproxy.android.xray

import kotlinx.serialization.KSerializer
import kotlinx.serialization.Serializable
import kotlinx.serialization.SerialName
import kotlinx.serialization.descriptors.SerialDescriptor
import kotlinx.serialization.descriptors.buildClassSerialDescriptor
import kotlinx.serialization.encoding.Decoder
import kotlinx.serialization.encoding.Encoder
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonDecoder
import kotlinx.serialization.json.JsonEncoder
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.decodeFromJsonElement
import kotlinx.serialization.json.encodeToJsonElement
import kotlinx.serialization.json.jsonObject

@Serializable
data class XrayConfig(
    val log: LogConfig = LogConfig(),
    val dns: DnsConfig,
    val inbounds: List<InboundConfig>,
    val outbounds: List<OutboundConfig>,
    val routing: RoutingConfig,
    val policy: PolicyConfig = PolicyConfig(),
    val stats: StatsConfig = StatsConfig,
)

/**
 * Registers the core's statistics manager. Without this key the core installs
 * a no-op manager whose counters do not exist, and every counter read
 * answers 0 no matter what the [PolicySystem] flags say -- so the traffic
 * readout depends on both being present.
 */
@Serializable
object StatsConfig

@Serializable
data class LogConfig(
    val access: String = "none",
    @SerialName("dnsLog") val dnsLog: Boolean = false,
    val error: String = "",
    val loglevel: String = "warning",
    @SerialName("maskAddress") val maskAddress: String = "",
)

@Serializable
data class DnsConfig(
    val servers: List<DnsServer> = emptyList(),
    @SerialName("queryStrategy") val queryStrategy: String = "UseIP",
    @SerialName("disableCache") val disableCache: Boolean = false,
    @SerialName("enableParallelQuery") val enableParallelQuery: Boolean = true,
)

@Serializable(with = DnsServerSerializer::class)
sealed interface DnsServer {
    @Serializable
    data class ObjectDns(
        val address: String,
        val domains: List<String> = emptyList(),
        @SerialName("expectIPs") val expectIPs: List<String> = emptyList(),
        @SerialName("skipFallback") val skipFallback: Boolean = false,
    ) : DnsServer

    @Serializable
    data class Plain(
        val value: String,
    ) : DnsServer
}

object DnsServerSerializer : KSerializer<DnsServer> {
    override val descriptor: SerialDescriptor = buildClassSerialDescriptor("DnsServer")

    override fun serialize(encoder: Encoder, value: DnsServer) {
        val jsonEncoder = encoder as? JsonEncoder
            ?: error("DnsServer can only be used with kotlinx.serialization.json.Json")
        val element = when (value) {
            is DnsServer.ObjectDns ->
                Json.encodeToJsonElement(DnsServer.ObjectDns.serializer(), value)
            is DnsServer.Plain -> JsonPrimitive(value.value)
        }
        jsonEncoder.encodeJsonElement(element)
    }

    override fun deserialize(decoder: Decoder): DnsServer {
        val jsonDecoder = decoder as? JsonDecoder
            ?: error("DnsServer can only be used with kotlinx.serialization.json.Json")
        val element = jsonDecoder.decodeJsonElement()
        return when (element) {
            is JsonPrimitive -> DnsServer.Plain(element.content)
            is JsonObject -> Json.decodeFromJsonElement(DnsServer.ObjectDns.serializer(), element)
            else -> error("Unsupported DNS server element: $element")
        }
    }
}

@Serializable
data class InboundConfig(
    val tag: String,
    val protocol: String,
    val listen: String? = null,
    val port: Int? = null,
    val settings: JsonObject = JsonObject(emptyMap()),
    val sniffing: SniffingConfig = SniffingConfig(),
)

/** Credentials for the loopback probe inbound; see [com.myproxy.android.xray.LocalProbeEndpoint]. */
@Serializable
data class HttpAccount(
    val user: String,
    val pass: String,
)

@Serializable
data class HttpInboundSettings(
    val accounts: List<HttpAccount>,
    @SerialName("allowTransparent") val allowTransparent: Boolean = false,
)

@Serializable
data class TunSettings(
    val name: String = "tun0",
    val mtu: Int = 1500,
    val userLevel: Int = 0,
)

@Serializable
data class SniffingConfig(
    val enabled: Boolean = true,
    @SerialName("destOverride") val destOverride: List<String> = listOf("http", "tls", "quic"),
    val metadataOnly: Boolean = false,
    val routeOnly: Boolean = false,
)

@Serializable
data class OutboundConfig(
    val tag: String,
    val protocol: String,
    val settings: JsonObject = JsonObject(emptyMap()),
    @SerialName("streamSettings") val streamSettings: StreamSettings? = null,
)

@Serializable
data class VlessSettings(
    val vnext: List<VlessNext> = emptyList(),
)

@Serializable
data class VlessNext(
    val address: String,
    val port: Int,
    val users: List<VlessUser> = emptyList(),
)

@Serializable
data class VlessUser(
    val id: String,
    val flow: String,
    val encryption: String = "none",
    val level: Int = 0,
)

@Serializable
data class FreedomSettings(
    @SerialName("domainStrategy") val domainStrategy: String = "AsIs",
)

@Serializable
object BlackholeSettings

@Serializable
data class StreamSettings(
    val network: String = "tcp",
    val security: String = "reality",
    @SerialName("realitySettings") val realitySettings: RealitySettings,
)

@Serializable
data class RealitySettings(
    val show: Boolean = false,
    val fingerprint: String,
    @SerialName("serverName") val serverName: String,
    @SerialName("publicKey") val publicKey: String,
    @SerialName("shortId") val shortId: String,
    @SerialName("spiderX") val spiderX: String,
)

@Serializable
data class RoutingConfig(
    @SerialName("domainStrategy") val domainStrategy: String = "AsIs",
    val rules: List<RoutingRule> = emptyList(),
)

@Serializable
data class RoutingRule(
    @SerialName("inboundTag") val inboundTag: List<String>? = null,
    val domain: List<String>? = null,
    val ip: List<String>? = null,
    val protocol: List<String>? = null,
    @SerialName("outboundTag") val outboundTag: String,
    val type: String = "field",
)

@Serializable
data class PolicyConfig(
    val levels: Map<String, PolicyLevel> = mapOf("0" to PolicyLevel()),
    val system: PolicySystem = PolicySystem(),
)

@Serializable
data class PolicyLevel(
    @SerialName("statsUserDownlink") val statsUserDownlink: Boolean = true,
    @SerialName("statsUserOnline") val statsUserOnline: Boolean = true,
    @SerialName("statsUserUplink") val statsUserUplink: Boolean = true,
)

/**
 * The outbound flags are what create the `outbound>>>proxy>>>traffic>>>*`
 * counters the UI reads; neither pair produces a counter unless [StatsConfig]
 * is also in the config.
 *
 * 入站统计**必须关着**，不只是因为没人读它。xray-core 自 #6349（2026-06-24，TUN 入站
 * 流量计数）起，入站统计开启时 TUN 连接会被 `CounterConnection` 包一层，而这层包装藏掉了
 * 按包携带目的地址的原生读写接口：同一个 UDP 关联发往多个目的地时，后续包全部跟着第一个
 * 目的地走（上游 #6747，2026-09-12 修复，晚于本工程所用的 libv2ray v26.9.9）。
 * 关掉入站统计就不会走进那条包装路径。以后升级到含 #6747 的版本也没有理由再打开它。
 */
@Serializable
data class PolicySystem(
    @SerialName("statsInboundDownlink") val statsInboundDownlink: Boolean = false,
    @SerialName("statsInboundUplink") val statsInboundUplink: Boolean = false,
    @SerialName("statsOutboundDownlink") val statsOutboundDownlink: Boolean = true,
    @SerialName("statsOutboundUplink") val statsOutboundUplink: Boolean = true,
)
