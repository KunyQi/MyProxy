package com.myproxy.android.domain.model

import kotlinx.serialization.Serializable

@Serializable
data class ConfigCacheEntry(
    val schemaVersion: Int = 1,
    val configVersion: Int? = null,
    val fetchedAt: String? = null,
    val verified: Boolean = false,
    val profile: ServerProfile? = null,
    val capabilities: List<String> = emptyList(),
    val featureFlags: Map<String, Boolean> = emptyMap(),
)
