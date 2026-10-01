package com.myproxy.android.data.api

/** Embedded update signing keys. Empty tables reject every update. Never fetch these trust keys at runtime. */
object ReleaseSigningKeys {
    /**
     * keyId to 32-byte public key. **Do not put a placeholder here**: a fake
     * key verifies nothing, but it does make "we have this configured" look
     * true.
     */
    val TRUSTED: Map<String, ByteArray> = parse("")

    /**
     * Parse `keyId:64hex[,keyId:64hex]`. Any malformed entry throws rather
     * than being skipped — a typo silently dropping a key from the trust
     * table is far worse than failing outright.
     */
    fun parse(spec: String?): Map<String, ByteArray> {
        val keys = LinkedHashMap<String, ByteArray>()
        for (item in (spec ?: "").split(',')) {
            val text = item.trim()
            if (text.isEmpty()) continue

            val separator = text.indexOf(':')
            require(separator > 0 && separator < text.length - 1) {
                "Release signing key entries must be keyId:hex."
            }

            val keyId = text.substring(0, separator).trim()
            val hex = text.substring(separator + 1).trim().lowercase()
            require(keyId.isNotEmpty() && keyId.length <= 64 && hex.length == 64) {
                "Release signing keys must be 32 bytes of hex."
            }

            val raw = ByteArray(32)
            for (index in 0 until 32) {
                val high = Character.digit(hex[index * 2], 16)
                val low = Character.digit(hex[index * 2 + 1], 16)
                require(high >= 0 && low >= 0) { "Release signing keys must be hexadecimal." }
                raw[index] = ((high shl 4) or low).toByte()
            }

            require(keys.put(keyId, raw) == null) { "Release signing key $keyId is declared twice." }
        }
        return keys
    }
}
