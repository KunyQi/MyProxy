package com.myproxy.android.data.storage

interface CredentialStorage {
    fun save(key: String, value: String)
    fun read(key: String): String?
    fun delete(key: String)
}
