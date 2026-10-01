package com.myproxy.android.domain.update

import java.math.BigInteger
import java.security.MessageDigest

/**
 * Ed25519 signature verification (RFC 8032). Verify only, never sign.
 *
 * Written out here rather than taken from a library for two reasons. The
 * platform has no Ed25519 below API 33 and this app supports API 24, so a
 * library would be the only alternative; and the private-key half must not
 * exist in the shipped APK at all, because "a client cannot forge an update
 * it would itself accept" is the property the whole Update Plane rests on.
 *
 * The constants and point arithmetic are the same as
 * `server/myproxy_server/release.py` and `windows/MyProxy/Core/Ed25519.cs`.
 * All three pin RFC 8032 test vector 1 byte for byte, which is what keeps
 * them from drifting apart: a manifest that verifies on one must verify on
 * all three, and the only way to be sure of that is for every implementation
 * to agree with the published vector rather than merely with each other.
 *
 * Pure JVM: no Android framework types, so it runs in the unit-test source
 * set (which has neither Robolectric nor `returnDefaultValues`).
 */
object Ed25519 {
    // BigInteger.valueOf(2) rather than BigInteger.TWO throughout: TWO is
    // Java 9 and this app supports API 24, where it does not exist. It is
    // not covered by desugaring either, so using it would compile fine and
    // then throw NoSuchFieldError on an old device -- the worst kind of
    // failure, because CI would never see it.
    private val P: BigInteger = BigInteger.valueOf(2).pow(255) - BigInteger.valueOf(19)

    /** Group order L. Signatures with S >= L are rejected, not reduced. */
    private val L: BigInteger = BigInteger.valueOf(2).pow(252) +
        BigInteger("27742317777372353535851937790883648493")

    private val D: BigInteger = mod(
        BigInteger.valueOf(-121665) * BigInteger.valueOf(121666).modPow(P - BigInteger.valueOf(2), P),
    )

    private val SQRT_M1: BigInteger =
        BigInteger.valueOf(2).modPow((P - BigInteger.ONE) / BigInteger.valueOf(4), P)

    private val IDENTITY = arrayOf(BigInteger.ZERO, BigInteger.ONE, BigInteger.ONE, BigInteger.ZERO)

    private val BASE: Array<BigInteger> = buildBase()

    /**
     * Returns true when [signature] is a valid signature of [message].
     *
     * Every malformed input returns false instead of throwing: a caller that
     * cannot tell "bad key encoding" from "wrong signature" cannot leak the
     * difference either.
     */
    fun verify(publicKey: ByteArray, message: ByteArray, signature: ByteArray): Boolean {
        if (publicKey.size != 32 || signature.size != 64) return false

        val pointA = decompress(publicKey) ?: return false
        val encodedR = signature.copyOfRange(0, 32)
        val pointR = decompress(encodedR) ?: return false

        val s = littleEndian(signature.copyOfRange(32, 64))
        // Non-canonical S encodes an equivalent scalar; reducing instead of
        // rejecting would let one signature have two byte encodings.
        if (s >= L) return false

        val digest = MessageDigest.getInstance("SHA-512")
        digest.update(encodedR)
        digest.update(publicKey)
        digest.update(message)
        val k = littleEndian(digest.digest()).mod(L)

        return pointEquals(multiply(s, BASE), add(pointR, multiply(k, pointA)))
    }

    private fun buildBase(): Array<BigInteger> {
        val y = mod(BigInteger.valueOf(4) * BigInteger.valueOf(5).modPow(P - BigInteger.valueOf(2), P))
        val x = recoverX(y, 0) ?: BigInteger.ZERO
        return arrayOf(x, y, BigInteger.ONE, mod(x * y))
    }

    private fun mod(value: BigInteger): BigInteger = value.mod(P)

    private fun inverse(value: BigInteger): BigInteger = mod(value).modPow(P - BigInteger.valueOf(2), P)

    private fun littleEndian(data: ByteArray): BigInteger =
        BigInteger(1, data.reversedArray())

    private fun recoverX(y: BigInteger, sign: Int): BigInteger? {
        if (y >= P) return null

        val x2 = mod((y * y - BigInteger.ONE) * inverse(D * y * y + BigInteger.ONE))
        if (x2.signum() == 0) return if (sign != 0) null else BigInteger.ZERO

        var x = x2.modPow((P + BigInteger.valueOf(3)) / BigInteger.valueOf(8), P)
        if (mod(x * x - x2).signum() != 0) x = mod(x * SQRT_M1)
        if (mod(x * x - x2).signum() != 0) return null
        if (x.testBit(0) != (sign == 1)) x = P - x
        return x
    }

    private fun decompress(data: ByteArray): Array<BigInteger>? {
        if (data.size != 32) return null
        val value = littleEndian(data)
        val sign = if (value.testBit(255)) 1 else 0
        val y = value.clearBit(255)
        val x = recoverX(y, sign) ?: return null
        return arrayOf(x, y, BigInteger.ONE, mod(x * y))
    }

    // Extended homogeneous coordinates (X, Y, Z, T); RFC 8032 section 5.1.
    private fun add(p: Array<BigInteger>, q: Array<BigInteger>): Array<BigInteger> {
        val a = mod((p[1] - p[0]) * (q[1] - q[0]))
        val b = mod((p[1] + p[0]) * (q[1] + q[0]))
        val c = mod(BigInteger.valueOf(2) * p[3] * q[3] * D)
        val d = mod(BigInteger.valueOf(2) * p[2] * q[2])
        val e = b - a
        val f = d - c
        val g = d + c
        val h = b + a
        return arrayOf(mod(e * f), mod(g * h), mod(f * g), mod(e * h))
    }

    private fun multiply(scalar: BigInteger, point: Array<BigInteger>): Array<BigInteger> {
        var result = IDENTITY
        var addend = point
        var remaining = scalar
        while (remaining.signum() > 0) {
            if (remaining.testBit(0)) result = add(result, addend)
            addend = add(addend, addend)
            remaining = remaining.shiftRight(1)
        }
        return result
    }

    private fun pointEquals(p: Array<BigInteger>, q: Array<BigInteger>): Boolean {
        // Projective coordinates are not unique; cross-multiply instead of
        // comparing components.
        if (mod(p[0] * q[2] - q[0] * p[2]).signum() != 0) return false
        return mod(p[1] * q[2] - q[1] * p[2]).signum() == 0
    }
}
