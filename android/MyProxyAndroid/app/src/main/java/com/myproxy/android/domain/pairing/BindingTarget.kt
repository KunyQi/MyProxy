package com.myproxy.android.domain.pairing

import kotlinx.serialization.Serializable
import java.net.URI

/** Fixed API origin, shared with the desktop clients. HTTP is for local debugging. */
@Serializable
data class BindingTarget(
    val host: String,
    val port: Int = DEFAULT_PORT,
    val scheme: String = HTTPS_SCHEME,
) {
    val baseUrl: String get() = "$scheme://$host:$port"
    val isHttps: Boolean get() = scheme == HTTPS_SCHEME
    val isConfigured: Boolean get() = !host.equals("invalid", ignoreCase = true) && !host.trim('[', ']').endsWith(".invalid", ignoreCase = true)

    companion object {
        const val DEFAULT_PORT = 443
        const val HTTPS_SCHEME = "https"

        fun fromBaseUrl(baseUrl: String?): BindingTarget? {
            val value = baseUrl ?: return null
            if (value.isEmpty() || value.any { it.isWhitespace() } || '\\' in value) return null
            return try {
                val uri = URI(value)
                val scheme = uri.scheme?.lowercase() ?: return null
                if (scheme != "http" && scheme != HTTPS_SCHEME) return null
                if (uri.userInfo != null || uri.rawQuery != null || uri.rawFragment != null ||
                    uri.rawPath !in listOf("", "/") || uri.rawAuthority?.endsWith(':') == true) return null
                val host = uri.host?.takeIf { it.isNotEmpty() } ?: return null
                val authority = uri.rawAuthority ?: return null
                val rawHost = if (authority.startsWith('[')) authority.substringBefore(']') + "]" else authority.substringBefore(':')
                if (!isCanonicalHost(rawHost)) return null
                val port = if (uri.port == -1) { if (scheme == HTTPS_SCHEME) 443 else 80 } else uri.port
                if (port !in 1..65535) return null
                BindingTarget(host, port, scheme)
            } catch (_: IllegalArgumentException) {
                null
            } catch (_: java.net.URISyntaxException) {
                null
            }
        }

        private fun isCanonicalHost(host: String): Boolean {
            if (host.startsWith('[')) return host.matches(Regex("\\[[0-9a-fA-F:.]+]"))
            if (host.length !in 1..253 || host.endsWith('.')) return false
            val labels = host.split('.')
            if (labels.all { label -> label.all { it in '0'..'9' } }) {
                return labels.size == 4 && labels.all { label ->
                    val value = label.toIntOrNull()
                    value != null && value in 0..255 && value.toString() == label
                }
            }
            if (labels.all { it.matches(Regex("(?i)(0x[0-9a-f]+|[0-9]+)")) }) return false
            return labels.all { it.matches(Regex("[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?")) }
        }

        fun normalizeSha256(sha256: String?): String {
            val value = sha256.orEmpty().replace(":", "").trim().lowercase()
            if (value.length != 64 || !value.all { it in '0'..'9' || it in 'a'..'f' }) return ""
            return value
        }
    }
}
