package com.myproxy.android.domain.model

import kotlinx.serialization.Serializable
import java.util.UUID

@Serializable
data class ServerProfile(
    val server: String,
    val uuid: String,
    val publicKey: String,
    val shortId: String,
    val sni: String,
    val fingerprint: String,
    val flow: String,
    val spiderX: String,
    val port: Int,
    val security: String = "reality",
) {
    fun validationError(): String? {
        if (server.isBlank() ||
            publicKey.isBlank() ||
            shortId.isBlank() ||
            sni.isBlank() ||
            fingerprint.isBlank() ||
            flow.isBlank() ||
            spiderX.isBlank()
        ) {
            return "server profile contains empty required field"
        }
        if (port !in 1..65535) {
            return "port must be in 1..65535"
        }
        if (!isValidUuid(uuid)) {
            return "uuid is not a valid UUID"
        }
        if (security != "reality") {
            return "security must be reality"
        }
        return null
    }

    fun isValid(): Boolean = validationError() == null

    private fun isValidUuid(value: String): Boolean = try {
        UUID.fromString(value).toString().equals(value, ignoreCase = true)
    } catch (_: IllegalArgumentException) {
        false
    }
}
