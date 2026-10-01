package com.myproxy.android.data.api

import java.io.IOException
import java.security.cert.CertificateException
import javax.net.ssl.SSLException
import javax.net.ssl.SSLPeerUnverifiedException

/**
 * Device API error taxonomy.
 *
 * The first group mirrors the server contract (`ErrorDetail.code` in
 * `server/docs/openapi.yaml`) one-for-one. `AdminUnauthorized` is deliberately
 * absent: device clients never call the Admin API, so a client that can name
 * that code would be a decoupling violation.
 *
 * Transport, deployment and certificate errors are client-local outcomes.
 */
enum class ErrorCode {
    PairingInvalid,
    PairingExpired,
    TokenInvalid,
    DeviceNotFound,
    RateLimited,
    BadRequest,
    NotFound,
    Conflict,
    ServerError,

    /** The request never produced a response (DNS, TLS, timeout, no route). */
    ApiUnreachable,
    ServerNotConfigured,

    /**
     * 服务器证书链、有效期或主机名未通过系统验证。
     */
    ServerUntrusted,
    Unknown,
}

/**
 * Maps a server error code to [ErrorCode], falling back to the HTTP status.
 *
 * Matching is case-insensitive but otherwise exact: the server emits the
 * contract spelling, so accepting invented aliases would only let a
 * mis-spelled code masquerade as a real one.
 */
internal fun normalizeApiErrorCode(raw: String?, status: Int): ErrorCode {
    when (raw?.trim()?.lowercase()) {
        "pairinginvalid" -> return ErrorCode.PairingInvalid
        "pairingexpired" -> return ErrorCode.PairingExpired
        "tokeninvalid" -> return ErrorCode.TokenInvalid
        "devicenotfound" -> return ErrorCode.DeviceNotFound
        "ratelimited" -> return ErrorCode.RateLimited
        "badrequest" -> return ErrorCode.BadRequest
        "notfound" -> return ErrorCode.NotFound
        "conflict" -> return ErrorCode.Conflict
        "servererror" -> return ErrorCode.ServerError
    }
    return when (status) {
        400 -> ErrorCode.BadRequest
        401 -> ErrorCode.TokenInvalid
        404 -> ErrorCode.NotFound
        409 -> ErrorCode.Conflict
        429 -> ErrorCode.RateLimited
        in 500..599 -> ErrorCode.ServerError
        else -> ErrorCode.Unknown
    }
}

/** cause 链的遍历上限；见 [tlsFailureCode]。 */
private const val MAX_CAUSE_DEPTH = 8

/** Certificate and hostname failures identify an untrusted server; other TLS errors describe the transport. */
internal fun tlsFailureCode(exception: SSLException): ErrorCode {
    if (exception is SSLPeerUnverifiedException) return ErrorCode.ServerUntrusted
    var cause: Throwable? = exception
    var depth = 0
    while (cause != null && depth < MAX_CAUSE_DEPTH) {
        if (cause is CertificateException) return ErrorCode.ServerUntrusted
        cause = cause.cause
        depth++
    }
    return ErrorCode.ApiUnreachable
}

/** True when the outcome may succeed later without any user action. */
val ErrorCode.isTransient: Boolean
    get() = this == ErrorCode.RateLimited ||
        this == ErrorCode.ServerError ||
        this == ErrorCode.ApiUnreachable

class ApiException(
    val code: ErrorCode,
    val status: Int,
    message: String,
    /** Token used by the rejected request; enables compare-and-clear after a concurrent rebind. */
    val rejectedCredentialToken: String? = null,
) : IOException(message)
