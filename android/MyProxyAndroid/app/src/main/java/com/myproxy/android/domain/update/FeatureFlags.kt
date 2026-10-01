package com.myproxy.android.domain.update

import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive

/**
 * Server-supplied feature flags: a flat table of switches.
 *
 * **Flags may only switch code paths that already shipped in this build.**
 * Genuinely new code goes out as a signed release. This is not a style rule:
 * if a switch could make the client do something its binary was not built to
 * do, the Update Plane's signature check has been routed around and the
 * Control Plane is once again a channel for arbitrary behaviour.
 *
 * Values may be string, number, boolean or null. Nested objects are dropped —
 * the server rejects them too, because nesting is the shape you reach for
 * when smuggling structured instructions down a channel meant for switches.
 */
class FeatureFlags private constructor(private val values: Map<String, String>) {

    val size: Int get() = values.size

    val names: Set<String> get() = values.keys

    fun isEnabled(name: String): Boolean = values[name].equals("true", ignoreCase = true)

    fun getString(name: String, fallback: String = ""): String = values[name] ?: fallback

    fun getLong(name: String, fallback: Long = 0L): Long = values[name]?.toLongOrNull() ?: fallback

    override fun toString(): String =
        if (values.isEmpty()) "(none)" else values.entries.joinToString { "${it.key}=${it.value}" }

    companion object {
        val EMPTY = FeatureFlags(emptyMap())

        /**
         * Build from a heartbeat or update response.
         *
         * A malformed entry is **dropped**, not fatal: a switch the server
         * added and this build has never heard of must not stop the switches
         * it does understand from arriving.
         */
        fun fromJson(element: JsonElement?): FeatureFlags {
            val obj = element as? JsonObject ?: return EMPTY
            val parsed = LinkedHashMap<String, String>()
            for ((key, value) in obj) {
                if (key.isEmpty() || key.length > 64) continue
                val primitive = value as? JsonPrimitive ?: continue
                val text = when {
                    primitive is JsonNull -> ""
                    primitive.isString -> primitive.content
                    else -> primitive.content
                }
                parsed[key] = text
            }
            return if (parsed.isEmpty()) EMPTY else FeatureFlags(parsed)
        }
    }
}

/** Flags this build knows about. Register here so names cannot drift. */
object KnownFeatureFlags {
    /**
     * Enable per-service-category usage attribution.
     *
     * Off by default: it needs an extra tagged outbound per category in the
     * generated xray config, which changes the shape of the data plane. That
     * should only happen when an administrator turns it on for a device.
     */
    const val USAGE_CATEGORIES = "usageCategories"

    /** Suppress automatic update checks; a manual check still works. */
    const val DISABLE_AUTO_UPDATE = "disableAutoUpdate"
}
