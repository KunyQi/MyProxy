package com.myproxy.android.domain.observability

/**
 * Observability Plane service categories, and how they map to xray outbound
 * tags.
 *
 * **No hostname or URL is ever recorded or reported.** Attribution works by
 * giving a few coarse categories their own outbound tag — same server, same
 * VLESS settings, different tag — letting routing split traffic by geosite
 * list, and then reading only each tag's **byte counters**. The client
 * therefore never needs to know, let alone send, where anyone went; what it
 * sends is "video moved 5 MB this hour".
 *
 * The vocabulary must match `server/myproxy_server/observability.py`'s
 * `SERVICE_CATEGORIES` and the Windows `ServiceCategories` word for word.
 * The server **rejects** a category outside the list rather than folding it
 * into `other`, so one extra name here means the whole report is refused.
 */
object ServiceCategories {
    const val VIDEO = "video"
    const val SOCIAL = "social"
    const val MESSAGING = "messaging"
    const val WEB = "web"
    const val DOWNLOAD = "download"
    const val OTHER = "other"

    /** The whole vocabulary, in the same order as the server's. */
    val ALL: List<String> = listOf(VIDEO, SOCIAL, MESSAGING, WEB, DOWNLOAD, OTHER)

    /**
     * The vocabulary's name for "the rest of the web". The default `proxy`
     * outbound is deliberately *not* attributed to it ([categoryForTag]
     * returns "" for it, and the tests pin that): unmatched proxied traffic is
     * web and downloads and games alike, and calling all of it "web" would be
     * a guess. Its bytes are already in the server's total.
     */
    const val FALLBACK_CATEGORY = WEB

    data class Routed(val category: String, val tag: String, val geosites: List<String>)

    /**
     * Categories that get their own outbound tag.
     *
     * Only three, and coarse: every extra tag is another connection pool to
     * the same server, and the value of attribution is in "roughly what",
     * not in fine detail. `download` and `other` have no dependable geosite
     * basis here and are left for the rest of the vocabulary's users.
     */
    val ROUTED: List<Routed> = listOf(
        Routed(VIDEO, "cat-video", listOf("geosite:youtube", "geosite:netflix", "geosite:disney", "geosite:bilibili")),
        Routed(SOCIAL, "cat-social", listOf("geosite:tiktok", "geosite:facebook", "geosite:twitter", "geosite:instagram")),
        Routed(MESSAGING, "cat-messaging", listOf("geosite:telegram", "geosite:whatsapp", "geosite:signal")),
    )

    /** Outbound tag to category; an unknown tag yields an empty string. */
    fun categoryForTag(tag: String): String =
        ROUTED.firstOrNull { it.tag == tag }?.category ?: ""

    fun isKnown(category: String): Boolean = category in ALL
}
