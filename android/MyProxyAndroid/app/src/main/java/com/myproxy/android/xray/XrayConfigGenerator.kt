package com.myproxy.android.xray

import com.myproxy.android.data.api.ApiConfig
import java.net.URI
import kotlinx.serialization.ExperimentalSerializationApi
import com.myproxy.android.domain.model.ProxyMode
import com.myproxy.android.domain.observability.ServiceCategories
import com.myproxy.android.domain.model.ServerProfile
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.encodeToJsonElement
import kotlinx.serialization.json.jsonObject

/**
 * Generates the Xray JSON configuration consumed by libv2ray.
 *
 * This is a pure function: no Android dependencies, no I/O, and no private key
 * material is ever included. The output is deterministic for the same inputs.
 */
@OptIn(ExperimentalSerializationApi::class)
object XrayConfigGenerator {

    /**
     * Tag of the outbound that carries proxied traffic. The core names its
     * traffic counters after it, so [com.myproxy.android.xray.XrayManager]
     * reads statistics through this same constant.
     */
    const val PROXY_OUTBOUND_TAG = "proxy"

    /** Tag of the loopback inbound the connectivity probe sends through. */
    const val PROBE_INBOUND_TAG = "probe-in"

    private val domesticMicrosoftDomains = listOf(
        "domain:azure.cn", "domain:chinacloudapi.cn", "domain:microsoftonline.cn",
        "domain:microsoft.com.cn", "domain:office365.cn", "full:cn.bing.com",
    )

    private val serviceProxyDomains = listOf(
        "domain:github.com", "domain:githubusercontent.com", "domain:githubassets.com", "domain:github.io",
        "domain:ghcr.io", "domain:githubcopilot.com", "domain:githubcopilot.net",
        "domain:google.com", "domain:googleapis.com", "domain:googleusercontent.com", "domain:gstatic.com",
        "domain:ggpht.com", "domain:googlevideo.com", "domain:googleadservices.com", "domain:googlesyndication.com",
        "domain:google-analytics.com", "domain:googleblog.com", "domain:blogspot.com", "domain:withgoogle.com",
        "domain:microsoft.com", "domain:microsoftonline.com", "domain:microsoft365.com", "domain:cloud.microsoft",
        "domain:office.com", "domain:office.net", "domain:office365.com", "domain:outlook.com",
        "domain:outlook.office365.com", "domain:live.com", "domain:onedrive.com", "domain:sharepoint.com",
        "domain:sharepointonline.com", "domain:teams.microsoft.com", "domain:msauth.net", "domain:msftauth.net",
        "domain:msauthimages.net", "domain:msftauthimages.net", "domain:azure.com", "domain:azure.net",
        "domain:windows.net", "domain:azureedge.net", "domain:azurefd.net", "domain:visualstudio.com",
        "domain:vsassets.io", "domain:bing.com", "domain:msn.com",
        "domain:openai.com", "domain:chatgpt.com", "domain:oaistatic.com", "domain:oaiusercontent.com",
        "domain:oaistatsig.com", "domain:openaimerge.com", "full:challenges.cloudflare.com", "full:cdn.workos.com",
        "full:setup.workos.com", "full:forwarder.workos.com", "full:images.workoscdn.com", "full:workos.imgix.net",
    )

    private val videoProxyDomains = listOf(
        "geosite:youtube", "domain:youtube.com", "domain:youtu.be", "domain:ytimg.com", "domain:googlevideo.com",
    )

    private val socialProxyDomains = listOf(
        "geosite:twitter", "domain:x.com", "domain:twitter.com", "domain:twimg.com", "domain:t.co",
    )

    private val json = Json {
        prettyPrint = true
        prettyPrintIndent = "  "
        encodeDefaults = true
        explicitNulls = false
        ignoreUnknownKeys = true
    }

    /**
     * @param categoryAttribution whether to emit one extra tagged outbound per
     * service category, for Observability Plane attribution.
     *
     * **Off by default, and the config it produces is byte-identical to what
     * it produced before this parameter existed.** Every extra tag is another
     * connection pool to the same server, which is a change to the shape of
     * the data plane; it should only happen when an administrator turns the
     * `usageCategories` flag on for a device. The category outbounds carry
     * identical VLESS settings and differ only in their tag, so where the
     * traffic goes does not change — only which counter it lands in.
     */
    fun generate(
        profile: ServerProfile,
        mode: ProxyMode,
        categoryAttribution: Boolean = false,
        probeUrls: List<String> = ApiConfig.connectivityCheckUrls,
    ): String {
        val error = profile.validationError()
        require(error == null) { error ?: "invalid server profile" }

        val outbounds = mutableListOf(proxyOutbound(profile))
        if (categoryAttribution) {
            for (routed in ServiceCategories.ROUTED) {
                outbounds += proxyOutbound(profile).copy(tag = routed.tag)
            }
        }
        outbounds += directOutbound()
        outbounds += blockedOutbound()

        val config = XrayConfig(
            dns = windowsDns(),
            inbounds = listOf(tunInbound(), probeInbound()),
            outbounds = outbounds,
            routing = RoutingConfig(
                // Resolve unmatched domains so the CN IP rule also covers sites
                // missing from geosite:cn. Global mode keeps domain routing as-is.
                domainStrategy = if (mode == ProxyMode.RULE) "IPIfNonMatch" else "AsIs",
                rules = routingRules(mode, categoryAttribution, probeUrls),
            ),
        )
        return json.encodeToString(XrayConfig.serializer(), config)
    }

    private fun tunInbound(): InboundConfig = InboundConfig(
        tag = "tun-in",
        protocol = "tun",
        settings = json.encodeToJsonElement(TunSettings()).jsonObject,
    )

    /**
     * Loopback inbound the connectivity probe sends through. It carries no user
     * traffic, so sniffing is off, and it is credential-protected because any
     * app on the device can reach a loopback listener.
     */
    private fun probeInbound(): InboundConfig = InboundConfig(
        tag = PROBE_INBOUND_TAG,
        protocol = "http",
        listen = LocalProbeEndpoint.HOST,
        port = LocalProbeEndpoint.port,
        settings = json.encodeToJsonElement(
            HttpInboundSettings(
                accounts = listOf(
                    HttpAccount(
                        user = LocalProbeEndpoint.username,
                        pass = LocalProbeEndpoint.password,
                    ),
                ),
            ),
        ).jsonObject,
        sniffing = SniffingConfig(enabled = false),
    )

    private fun windowsDns(): DnsConfig = DnsConfig(
        servers = listOf(
            DnsServer.ObjectDns(
                address = "223.5.5.5",
                domains = listOf("geosite:cn"),
                expectIPs = listOf("geoip:cn"),
                skipFallback = true,
            ),
            DnsServer.ObjectDns(
                address = "119.29.29.29",
                domains = listOf("geosite:cn"),
                expectIPs = listOf("geoip:cn"),
                skipFallback = true,
            ),
            DnsServer.ObjectDns(
                address = "https://1.1.1.1/dns-query",
                domains = listOf("geosite:geolocation-!cn"),
            ),
            DnsServer.ObjectDns(
                address = "https://8.8.8.8/dns-query",
                domains = listOf("geosite:geolocation-!cn"),
            ),
        ),
    )

    private fun proxyOutbound(profile: ServerProfile): OutboundConfig {
        val settings = json.encodeToJsonElement(
            VlessSettings(
                vnext = listOf(
                    VlessNext(
                        address = profile.server,
                        port = profile.port,
                        users = listOf(
                            VlessUser(
                                id = profile.uuid,
                                flow = profile.flow,
                                encryption = "none",
                                level = 0,
                            ),
                        ),
                    ),
                ),
            ),
        ).jsonObject

        return OutboundConfig(
            tag = PROXY_OUTBOUND_TAG,
            protocol = "vless",
            settings = settings,
            streamSettings = StreamSettings(
                network = "tcp",
                security = profile.security,
                realitySettings = RealitySettings(
                    show = false,
                    fingerprint = profile.fingerprint,
                    serverName = profile.sni,
                    publicKey = profile.publicKey,
                    shortId = profile.shortId,
                    spiderX = profile.spiderX,
                ),
            ),
        )
    }

    private fun directOutbound(): OutboundConfig {
        val settings = json.encodeToJsonElement(FreedomSettings()).jsonObject
        return OutboundConfig(
            tag = "direct",
            protocol = "freedom",
            settings = settings,
        )
    }

    private fun blockedOutbound(): OutboundConfig {
        val settings = json.encodeToJsonElement(BlackholeSettings).jsonObject
        return OutboundConfig(
            tag = "blocked",
            protocol = "blackhole",
            settings = settings,
        )
    }

    private fun routingRules(
        mode: ProxyMode,
        categoryAttribution: Boolean = false,
        probeUrls: List<String>,
    ): List<RoutingRule> {
        val rules = mutableListOf<RoutingRule>()
        // Only configured probe destinations can use this inbound; all are forced
        // through the tunnel before smart-routing and private-address rules.
        val hosts = probeUrls.map { URI(it).host.removeSurrounding("[", "]") }.distinct()
        val (ips, domains) = hosts.partition { ':' in it || it.matches(Regex("[0-9]+(?:\\.[0-9]+){3}")) }
        if (domains.isNotEmpty()) rules += RoutingRule(
            inboundTag = listOf(PROBE_INBOUND_TAG), domain = domains.map { "full:$it" }, outboundTag = PROXY_OUTBOUND_TAG,
        )
        if (ips.isNotEmpty()) rules += RoutingRule(
            inboundTag = listOf(PROBE_INBOUND_TAG), ip = ips, outboundTag = PROXY_OUTBOUND_TAG,
        )
        rules += RoutingRule(inboundTag = listOf(PROBE_INBOUND_TAG), outboundTag = "blocked")
        // Reject private addresses before a CN result can make a mixed address
        // set direct. Protocol restrictions also precede all user traffic rules.
        rules += RoutingRule(
            ip = listOf("geoip:private"),
            outboundTag = "blocked",
        )
        rules += RoutingRule(
            protocol = listOf("bittorrent"),
            outboundTag = "blocked",
        )
        if (mode == ProxyMode.RULE) {
            rules += RoutingRule(
                domain = listOf("geosite:cn") + domesticMicrosoftDomains,
                outboundTag = "direct",
            )
        }
        // Preserve domestic domain routing and category counters. Service
        // domains take the tunnel before the CN IP fallback, without a local
        // DNS lookup on the first request.
        if (categoryAttribution) {
            for (routed in ServiceCategories.ROUTED) {
                val extraDomains = when (routed.category) {
                    ServiceCategories.VIDEO -> videoProxyDomains
                    ServiceCategories.SOCIAL -> socialProxyDomains
                    else -> emptyList()
                }
                rules += RoutingRule(
                    domain = (routed.geosites + extraDomains).distinct(),
                    outboundTag = routed.tag,
                )
            }
        }
        if (mode == ProxyMode.RULE) {
            rules += RoutingRule(
                domain = (serviceProxyDomains + videoProxyDomains + socialProxyDomains +
                    "geosite:geolocation-!cn").distinct(),
                outboundTag = PROXY_OUTBOUND_TAG,
            )
            // Xray accepts a match against any resolved address. A foreign
            // result must win over a CN result in a mixed A/AAAA response.
            rules += RoutingRule(
                ip = listOf("!geoip:cn"),
                outboundTag = PROXY_OUTBOUND_TAG,
            )
            rules += RoutingRule(
                ip = listOf("geoip:cn"),
                outboundTag = "direct",
            )
        }
        return rules
    }
}
