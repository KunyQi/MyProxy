package com.myproxy.android.data.storage

import com.myproxy.android.domain.model.ConfigCacheEntry
import kotlinx.serialization.json.Json
import java.io.File
import java.io.FileOutputStream
import java.io.IOException

/**
 * File-backed JSON cache for the last known good server configuration.
 * The directory is injectable so unit tests can use a temporary folder.
 */
class ConfigCacheStore(private val directory: File) {

    private val targetFile: File
        get() = File(directory, FILE_NAME)

    private val tmpFile: File
        get() = File(directory, "$FILE_NAME.tmp")

    private val backupFile: File
        get() = File(directory, "$FILE_NAME.bak")

    private val json = Json {
        ignoreUnknownKeys = true
        encodeDefaults = true
        prettyPrint = false
    }

    @Synchronized
    fun read(): ConfigCacheEntry? {
        decode(targetFile)?.let { return it }
        val recovered = decode(backupFile) ?: return null
        restoreBackupBestEffort()
        return recovered
    }

    @Synchronized
    fun write(entry: ConfigCacheEntry) {
        val text = json.encodeToString(ConfigCacheEntry.serializer(), entry)
        if (!directory.exists() && !directory.mkdirs()) {
            throw IOException("Unable to create config cache directory: $directory")
        }

        recoverInterruptedWrite()
        writeAndSync(tmpFile, text)

        if (backupFile.exists() && !backupFile.delete()) {
            tmpFile.delete()
            throw IOException("Unable to remove stale config cache backup")
        }
        if (targetFile.exists() && !targetFile.renameTo(backupFile)) {
            tmpFile.delete()
            throw IOException("Unable to preserve existing config cache file")
        }
        if (!tmpFile.renameTo(targetFile)) {
            restoreBackupBestEffort()
            throw IOException("Unable to move temporary config cache into place")
        }
        backupFile.delete()
    }

    @Synchronized
    fun clear() {
        targetFile.delete()
        tmpFile.delete()
        backupFile.delete()
    }

    private fun decode(file: File): ConfigCacheEntry? {
        if (!file.exists()) return null
        return runCatching {
            json.decodeFromString<ConfigCacheEntry>(file.readText(Charsets.UTF_8))
        }.getOrNull()
    }

    private fun recoverInterruptedWrite() {
        when {
            decode(targetFile) != null -> backupFile.delete()
            decode(backupFile) != null -> restoreBackupBestEffort()
            targetFile.exists() -> targetFile.delete()
        }
        tmpFile.delete()
    }

    private fun restoreBackupBestEffort() {
        if (!backupFile.exists()) return
        if (targetFile.exists() && !targetFile.delete()) return
        backupFile.renameTo(targetFile)
    }

    private fun writeAndSync(file: File, text: String) {
        FileOutputStream(file).use { output ->
            output.write(text.toByteArray(Charsets.UTF_8))
            output.flush()
            output.fd.sync()
        }
    }

    private companion object {
        const val FILE_NAME = "config-cache.json"
    }
}
