package com.myproxy.android.xray

import com.myproxy.android.domain.model.ProxyMode
import com.myproxy.android.domain.model.ServerProfile
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.boolean
import kotlinx.serialization.json.int
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class XrayConfigGeneratorTest {

    private val validProfile = ServerProfile(
        server = "v2.example.com",
        uuid = "123e4567-e89b-12d3-a456-426614174000",
        publicKey = "public-key-value",
        shortId = "abcd1234",
        sni = "sni.example.com",
        fingerprint = "chrome",
        flow = "xtls-rprx-vision",
        spiderX = "spider-value",
        port = 443,
        security = "reality",
    )

    private val json = Json { ignoreUnknownKeys = true }

    @Test
    fun `generates valid JSON with a tun inbound and a guarded probe inbound`() {
        val config = XrayConfigGenerator.generate(validProfile, ProxyMode.RULE)
        val root = json.parseToJsonElement(config).jsonObject

        val inbounds = root.getValue("inbounds").jsonArray
        assertEquals(2, inbounds.size)
        val tun = inbounds[0].jsonObject
        assertEquals("tun-in", tun.getValue("tag").jsonPrimitive.content)
        assertEquals("tun", tun.getValue("protocol").jsonPrimitive.content)
        assertEquals(1500, tun.getValue("settings").jsonObject.getValue("mtu").jsonPrimitive.int)

        // The probe inbound must stay on loopback and must stay credentialed:
        // any app on the device can reach a loopback listener, so an open one
        // would hand the tunnel to every installed app.
        val probe = inbounds[1].jsonObject
        assertEquals("probe-in", probe.getValue("tag").jsonPrimitive.content)
        assertEquals("http", probe.getValue("protocol").jsonPrimitive.content)
        assertEquals("127.0.0.1", probe.getValue("listen").jsonPrimitive.content)
        assertTrue(probe.getValue("port").jsonPrimitive.int > 0)
        val accounts = probe.getValue("settings").jsonObject.getValue("accounts").jsonArray
        assertEquals(1, accounts.size)
        val account = accounts[0].jsonObject
        assertTrue(account.getValue("user").jsonPrimitive.content.isNotBlank())
        assertTrue(account.getValue("pass").jsonPrimitive.content.isNotBlank())
        assertFalse(probe.getValue("sniffing").jsonObject.getValue("enabled").jsonPrimitive.boolean)
    }

    @Test
    fun `proxy is the first outbound`() {
        val config = XrayConfigGenerator.generate(validProfile, ProxyMode.RULE)
        val outbounds = json.parseToJsonElement(config).jsonObject.getValue("outbounds").jsonArray

        assertEquals(3, outbounds.size)
        val proxy = outbounds[0].jsonObject
        assertEquals("proxy", proxy.getValue("tag").jsonPrimitive.content)
        assertEquals("vless", proxy.getValue("protocol").jsonPrimitive.content)
        assertEquals("direct", outbounds[1].jsonObject.getValue("tag").jsonPrimitive.content)
        assertEquals("blocked", outbounds[2].jsonObject.getValue("tag").jsonPrimitive.content)
    }

    @Test
    fun `rule and global routing differ correctly`() {
        val ruleConfig = json.parseToJsonElement(
            XrayConfigGenerator.generate(validProfile, ProxyMode.RULE)
        ).jsonObject
        val globalConfig = json.parseToJsonElement(
            XrayConfigGenerator.generate(validProfile, ProxyMode.GLOBAL)
        ).jsonObject

        val ruleRules = ruleConfig.getValue("routing").jsonObject.getValue("rules").jsonArray
            .filterNot { rule ->
                rule.jsonObject["inboundTag"]?.jsonArray?.any {
                    it.jsonPrimitive.content == XrayConfigGenerator.PROBE_INBOUND_TAG
                } == true
            }
        val globalRules = globalConfig.getValue("routing").jsonObject.getValue("rules").jsonArray
            .filterNot { rule ->
                rule.jsonObject["inboundTag"]?.jsonArray?.any {
                    it.jsonPrimitive.content == XrayConfigGenerator.PROBE_INBOUND_TAG
                } == true
            }

        // Probe allow/deny rules are checked separately; compare only rules
        // that can match ordinary traffic in the selected routing mode.
        assertEquals(4, ruleRules.size)
        assertEquals(2, globalRules.size)
        assertEquals("geosite:cn", ruleRules[0].jsonObject.getValue("domain").jsonArray[0].jsonPrimitive.content)
        assertEquals("geoip:cn", ruleRules[1].jsonObject.getValue("ip").jsonArray[0].jsonPrimitive.content)
        assertEquals("geoip:private", globalRules[0].jsonObject.getValue("ip").jsonArray[0].jsonPrimitive.content)
        assertEquals("bittorrent", globalRules[1].jsonObject.getValue("protocol").jsonArray[0].jsonPrimitive.content)
    }

    @Test
    fun `all profile fields are placed in proxy outbound`() {
        val config = XrayConfigGenerator.generate(validProfile, ProxyMode.RULE)
        val text = config
        assertTrue(text.contains("\"address\""))
        assertTrue(text.contains("v2.example.com"))
        assertTrue(text.contains("443"))
        assertTrue(text.contains(validProfile.uuid))
        assertTrue(text.contains(validProfile.flow))
        assertTrue(text.contains("\"encryption\": \"none\""))
        assertTrue(text.contains(validProfile.fingerprint))
        assertTrue(text.contains(validProfile.sni))
        assertTrue(text.contains(validProfile.publicKey))
        assertTrue(text.contains(validProfile.shortId))
        assertTrue(text.contains(validProfile.spiderX))

        val root = json.parseToJsonElement(config).jsonObject
        val proxy = root.getValue("outbounds").jsonArray[0].jsonObject
        val stream = proxy.getValue("streamSettings").jsonObject
        assertEquals("tcp", stream.getValue("network").jsonPrimitive.content)
        assertEquals("reality", stream.getValue("security").jsonPrimitive.content)
        assertEquals(
            "sni.example.com",
            stream.getValue("realitySettings").jsonObject.getValue("serverName").jsonPrimitive.content,
        )
    }

    @Test
    fun `config never contains private key`() {
        val ruleText = XrayConfigGenerator.generate(validProfile, ProxyMode.RULE)
        val globalText = XrayConfigGenerator.generate(validProfile, ProxyMode.GLOBAL)
        assertFalse(ruleText.contains("privateKey", ignoreCase = true))
        assertFalse(globalText.contains("privateKey", ignoreCase = true))
    }

    @Test
    fun `json is parseable for both modes`() {
        val rule = json.parseToJsonElement(XrayConfigGenerator.generate(validProfile, ProxyMode.RULE))
        val global = json.parseToJsonElement(XrayConfigGenerator.generate(validProfile, ProxyMode.GLOBAL))
        assertTrue(rule is JsonObject)
        assertTrue(global is JsonObject)
    }

    @Test
    fun `generation is deterministic`() {
        val first = XrayConfigGenerator.generate(validProfile, ProxyMode.RULE)
        val second = XrayConfigGenerator.generate(validProfile, ProxyMode.RULE)
        assertEquals(first, second)
        assertNotEquals(first, XrayConfigGenerator.generate(validProfile, ProxyMode.GLOBAL))
    }

    @Test
    fun `invalid profile throws`() {
        val invalid = validProfile.copy(port = 70000)
        assertThrows(IllegalArgumentException::class.java) {
            XrayConfigGenerator.generate(invalid, ProxyMode.RULE)
        }
    }

    @Test
    fun `the probe always takes the tunnel, ahead of every other rule`() {
        // The probe URL is a Full entry in geosite:cn. Behind the "domestic
        // sites go direct" rule it measured a direct connection, and a profile
        // whose tunnel never worked was promoted to LastKnownGood.
        for (mode in ProxyMode.entries) {
            val rules = json.parseToJsonElement(XrayConfigGenerator.generate(validProfile, mode))
                .jsonObject.getValue("routing").jsonObject.getValue("rules").jsonArray
            val first = rules[0].jsonObject
            assertEquals(
                mode.name,
                listOf(XrayConfigGenerator.PROBE_INBOUND_TAG),
                first.getValue("inboundTag").jsonArray.map { it.jsonPrimitive.content },
            )
            assertEquals(
                mode.name,
                XrayConfigGenerator.PROXY_OUTBOUND_TAG,
                first.getValue("outboundTag").jsonPrimitive.content,
            )
            val probeRules = rules.map { it.jsonObject }.filter { it.containsKey("inboundTag") }
            assertEquals("blocked", probeRules.last().getValue("outboundTag").jsonPrimitive.content)
            assertTrue(mode.name, rules.drop(probeRules.size).none { it.jsonObject.containsKey("inboundTag") })
        }

        // And the tag the rule names is the probe inbound's actual tag.
        val inbounds = json.parseToJsonElement(XrayConfigGenerator.generate(validProfile, ProxyMode.RULE))
            .jsonObject.getValue("inbounds").jsonArray
        assertEquals(
            XrayConfigGenerator.PROBE_INBOUND_TAG,
            inbounds[1].jsonObject.getValue("tag").jsonPrimitive.content,
        )
    }

    @Test
    fun `all configured domain and IP probe destinations precede the deny rule`() {
        val rules = json.parseToJsonElement(XrayConfigGenerator.generate(validProfile, ProxyMode.RULE,
            probeUrls = listOf("https://check.example.invalid/204", "https://203.0.113.4:8443/ping", "https://[2001:db8::1]/204")))
            .jsonObject.getValue("routing").jsonObject.getValue("rules").jsonArray
        val domainRule = rules[0].jsonObject
        assertEquals(listOf("full:check.example.invalid"), domainRule.getValue("domain").jsonArray.map { it.jsonPrimitive.content })
        val ipRule = rules[1].jsonObject
        assertEquals(listOf("203.0.113.4", "2001:db8::1"), ipRule.getValue("ip").jsonArray.map { it.jsonPrimitive.content })
        assertEquals("proxy", ipRule.getValue("outboundTag").jsonPrimitive.content)
        assertEquals("blocked", rules[2].jsonObject.getValue("outboundTag").jsonPrimitive.content)
    }

    @Test
    fun `traffic counters are switched on for the proxy outbound`() {
        // The readout needs both halves: the stats app has to be registered,
        // and the outbound policy flags are what create the counters the UI
        // reads. Either one missing and every query answers 0.
        val root = json.parseToJsonElement(
            XrayConfigGenerator.generate(validProfile, ProxyMode.RULE)
        ).jsonObject

        assertTrue("stats app is not registered", root.containsKey("stats"))

        val system = root.getValue("policy").jsonObject.getValue("system").jsonObject
        assertTrue(system.getValue("statsOutboundUplink").jsonPrimitive.boolean)
        assertTrue(system.getValue("statsOutboundDownlink").jsonPrimitive.boolean)

        // The counters are named after the outbound tag, so the constant the
        // stats reader uses must match the tag actually emitted.
        assertEquals(
            XrayConfigGenerator.PROXY_OUTBOUND_TAG,
            root.getValue("outbounds").jsonArray[0].jsonObject.getValue("tag").jsonPrimitive.content,
        )
    }

    @Test
    fun `inbound traffic counters stay off`() {
        // With inbound stats on, xray wraps every TUN connection in a byte
        // counter that hides the per-packet destination of UDP (upstream #6747):
        // on a multi-destination UDP association every later packet follows the
        // first destination. Nothing reads inbound counters, so they stay off.
        for (mode in ProxyMode.entries) {
            val system = json.parseToJsonElement(XrayConfigGenerator.generate(validProfile, mode))
                .jsonObject.getValue("policy").jsonObject.getValue("system").jsonObject
            assertFalse(mode.name, system.getValue("statsInboundUplink").jsonPrimitive.boolean)
            assertFalse(mode.name, system.getValue("statsInboundDownlink").jsonPrimitive.boolean)
        }
    }

    @Test
    fun `dns has five servers with expected strategy`() {
        val config = XrayConfigGenerator.generate(validProfile, ProxyMode.RULE)
        val dns = json.parseToJsonElement(config).jsonObject.getValue("dns").jsonObject
        val servers = dns.getValue("servers").jsonArray
        assertEquals(5, servers.size)
        assertEquals("UseIP", dns.getValue("queryStrategy").jsonPrimitive.content)
        assertEquals(false, dns.getValue("disableCache").jsonPrimitive.content.toBooleanStrictOrNull())
    }
}
