package com.myproxy.android.data.api

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ApiErrorMappingTest {
    @Test
    fun `every contract code maps to its own domain`() {
        assertEquals(ErrorCode.PairingInvalid, normalizeApiErrorCode("PairingInvalid", 400))
        assertEquals(ErrorCode.PairingExpired, normalizeApiErrorCode("PairingExpired", 400))
        assertEquals(ErrorCode.TokenInvalid, normalizeApiErrorCode("TokenInvalid", 401))
        assertEquals(ErrorCode.DeviceNotFound, normalizeApiErrorCode("DeviceNotFound", 401))
        assertEquals(ErrorCode.RateLimited, normalizeApiErrorCode("RateLimited", 429))
        assertEquals(ErrorCode.BadRequest, normalizeApiErrorCode("BadRequest", 400))
        assertEquals(ErrorCode.NotFound, normalizeApiErrorCode("NotFound", 404))
        assertEquals(ErrorCode.Conflict, normalizeApiErrorCode("Conflict", 409))
        assertEquals(ErrorCode.ServerError, normalizeApiErrorCode("ServerError", 500))
    }

    @Test
    fun `code matching ignores case only`() {
        assertEquals(ErrorCode.TokenInvalid, normalizeApiErrorCode("tokeninvalid", 401))
        assertEquals(ErrorCode.RateLimited, normalizeApiErrorCode("  RATELIMITED  ", 429))
        // Invented spellings must not masquerade as contract codes; they fall
        // through to the status, which is the only other trustworthy signal.
        assertEquals(ErrorCode.BadRequest, normalizeApiErrorCode("token_invalid", 400))
    }

    @Test
    fun `status decides when the body carries no usable code`() {
        assertEquals(ErrorCode.BadRequest, normalizeApiErrorCode(null, 400))
        assertEquals(ErrorCode.TokenInvalid, normalizeApiErrorCode(null, 401))
        assertEquals(ErrorCode.NotFound, normalizeApiErrorCode(null, 404))
        assertEquals(ErrorCode.Conflict, normalizeApiErrorCode(null, 409))
        assertEquals(ErrorCode.RateLimited, normalizeApiErrorCode(null, 429))
        assertEquals(ErrorCode.ServerError, normalizeApiErrorCode(null, 503))
        assertEquals(ErrorCode.Unknown, normalizeApiErrorCode(null, 418))
    }

    @Test
    fun `a BadRequest is never reported as a pairing problem`() {
        // Heartbeat and config calls carry no pairing code, so blaming the
        // pairing code for a 400 would send the user to re-bind for nothing.
        assertEquals(ErrorCode.BadRequest, normalizeApiErrorCode("BadRequest", 400))
    }

    @Test
    fun `only retryable outcomes are transient`() {
        assertTrue(ErrorCode.RateLimited.isTransient)
        assertTrue(ErrorCode.ServerError.isTransient)
        assertTrue(ErrorCode.ApiUnreachable.isTransient)
        assertFalse(ErrorCode.PairingInvalid.isTransient)
        assertFalse(ErrorCode.TokenInvalid.isTransient)
        assertFalse(ErrorCode.BadRequest.isTransient)
    }
}
