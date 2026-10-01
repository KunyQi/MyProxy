package com.myproxy.android.domain.update

data class VersionCode(
    val major: Int,
    val minor: Int,
    val patch: Int,
) : Comparable<VersionCode> {

    override fun compareTo(other: VersionCode): Int {
        val majorCmp = major.compareTo(other.major)
        if (majorCmp != 0) return majorCmp
        val minorCmp = minor.compareTo(other.minor)
        if (minorCmp != 0) return minorCmp
        return patch.compareTo(other.patch)
    }
}

object VersionParser {

    /**
     * Parse `major.minor.patch`, ignoring any `-prerelease` or `+build`
     * suffix.
     *
     * The suffix is dropped rather than rejected because the server's
     * manifest validator accepts it and the Windows client truncates it the
     * same way. Rejecting it here would mean a `1.2.3-beta.1` release that
     * the server published and Windows installed would be silently invisible
     * on Android — the two clients would no longer be running one protocol.
     *
     * Eligibility for a prerelease is decided by the manifest's `channel`
     * field, not by the shape of the version string.
     */
    fun parse(raw: String): VersionCode? {
        val trimmed = raw.trim().substringBefore('-').substringBefore('+')
        if (trimmed.isEmpty()) return null
        val parts = trimmed.split('.')
        if (parts.size != 3) return null
        val numbers = parts.map { part ->
            if (part.isEmpty() || !part.all { it.isDigit() }) return null
            part.toIntOrNull() ?: return null
        }
        return VersionCode(numbers[0], numbers[1], numbers[2])
    }

    fun isNewer(candidate: String, current: String): Boolean {
        val candidateVersion = parse(candidate) ?: return false
        val currentVersion = parse(current) ?: return false
        return candidateVersion > currentVersion
    }
}
