package com.myproxy.android.domain.update

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.math.BigInteger
import java.security.MessageDigest

/**
 * A test-only Ed25519 signer.
 *
 * It exists only here: the shipped app **verifies but never signs**, and
 * keeping the private-key half out of the APK is what makes "a client cannot
 * forge an update it would itself accept" true rather than merely intended.
 */
internal object TestSigner {
    private val P: BigInteger = BigInteger.valueOf(2).pow(255) - BigInteger.valueOf(19)
    private val L: BigInteger = BigInteger.valueOf(2).pow(252) +
        BigInteger("27742317777372353535851937790883648493")
    private val D: BigInteger = mod(
        BigInteger.valueOf(-121665) * BigInteger.valueOf(121666).modPow(P - BigInteger.valueOf(2), P),
    )
    private val SQRT_M1: BigInteger =
        BigInteger.valueOf(2).modPow((P - BigInteger.ONE) / BigInteger.valueOf(4), P)
    private val IDENTITY = arrayOf(BigInteger.ZERO, BigInteger.ONE, BigInteger.ONE, BigInteger.ZERO)
    private val BASE = buildBase()

    fun publicKey(secret: ByteArray): ByteArray {
        val (scalar, _) = expand(secret)
        return compress(multiply(scalar, BASE))
    }

    fun sign(secret: ByteArray, message: ByteArray): ByteArray {
        val (scalar, prefix) = expand(secret)
        val encodedA = publicKey(secret)

        val r = littleEndian(sha512(prefix + message)).mod(L)
        val encodedR = compress(multiply(r, BASE))
        val k = littleEndian(sha512(encodedR + encodedA + message)).mod(L)
        val s = (r + k * scalar).mod(L)

        return encodedR + toLittleEndian(s, 32)
    }

    private fun expand(secret: ByteArray): Pair<BigInteger, ByteArray> {
        val digest = sha512(secret)
        var scalar = littleEndian(digest.copyOfRange(0, 32))
        scalar = scalar.and(BigInteger.valueOf(2).pow(254) - BigInteger.valueOf(8))
        scalar = scalar.or(BigInteger.valueOf(2).pow(254))
        return scalar to digest.copyOfRange(32, 64)
    }

    private fun sha512(data: ByteArray): ByteArray =
        MessageDigest.getInstance("SHA-512").digest(data)

    private fun mod(value: BigInteger): BigInteger = value.mod(P)

    private fun littleEndian(data: ByteArray): BigInteger = BigInteger(1, data.reversedArray())

    private fun toLittleEndian(value: BigInteger, size: Int): ByteArray {
        val raw = value.toByteArray().reversedArray()
        val out = ByteArray(size)
        System.arraycopy(raw, 0, out, 0, minOf(raw.size, size))
        return out
    }

    private fun buildBase(): Array<BigInteger> {
        val y = mod(BigInteger.valueOf(4) * BigInteger.valueOf(5).modPow(P - BigInteger.valueOf(2), P))
        val x = recoverX(y)
        return arrayOf(x, y, BigInteger.ONE, mod(x * y))
    }

    private fun recoverX(y: BigInteger): BigInteger {
        val x2 = mod(
            (y * y - BigInteger.ONE) *
                mod(D * y * y + BigInteger.ONE).modPow(P - BigInteger.valueOf(2), P),
        )
        var x = x2.modPow((P + BigInteger.valueOf(3)) / BigInteger.valueOf(8), P)
        if (mod(x * x - x2).signum() != 0) x = mod(x * SQRT_M1)
        if (x.testBit(0)) x = P - x
        return x
    }

    private fun compress(point: Array<BigInteger>): ByteArray {
        val zInverse = point[2].modPow(P - BigInteger.valueOf(2), P)
        val x = mod(point[0] * zInverse)
        val y = mod(point[1] * zInverse)
        val encoded = if (x.testBit(0)) y.setBit(255) else y
        return toLittleEndian(encoded, 32)
    }

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
}

class Ed25519Test {

    private val rfcSecret = hex("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60")
    private val rfcPublic = hex("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a")
    private val rfcSignature = hex(
        "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e06522490155" +
            "5fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b",
    )

    /**
     * Round-tripping against our own signer would also pass on a subtly wrong
     * curve, so pin the published vector: the same secret must produce the
     * same public key and the same signature bytes as RFC 8032. The server
     * and the Windows client pin the very same vector, which is what keeps
     * the three implementations from drifting apart.
     */
    @Test
    fun `rfc 8032 vector 1 matches byte for byte`() {
        assertArrayEquals(rfcPublic, TestSigner.publicKey(rfcSecret))
        assertArrayEquals(rfcSignature, TestSigner.sign(rfcSecret, ByteArray(0)))
        assertTrue(Ed25519.verify(rfcPublic, ByteArray(0), rfcSignature))
    }

    @Test
    fun `malformed inputs return false instead of throwing`() {
        val secret = ByteArray(32) { it.toByte() }
        val other = ByteArray(32) { (it + 32).toByte() }
        val message = "payload".toByteArray()
        val good = TestSigner.sign(secret, message)
        val publicKey = TestSigner.publicKey(secret)

        assertFalse(Ed25519.verify(publicKey.copyOfRange(0, 31), message, good))
        assertFalse(Ed25519.verify(publicKey, message, good.copyOfRange(0, 63)))
        assertFalse(Ed25519.verify(publicKey, "other".toByteArray(), good))
        assertFalse(Ed25519.verify(TestSigner.publicKey(other), message, good))

        val flipped = good.copyOf()
        flipped[0] = (flipped[0].toInt() xor 1).toByte()
        assertFalse(Ed25519.verify(publicKey, message, flipped))
    }

    @Test
    fun `non canonical S is rejected`() {
        // S >= L encodes an equivalent scalar. Reducing instead of rejecting
        // would let one signature have two valid byte encodings.
        val secret = ByteArray(32) { it.toByte() }
        val message = "payload".toByteArray()
        val good = TestSigner.sign(secret, message)

        val l = BigInteger.valueOf(2).pow(252) + BigInteger("27742317777372353535851937790883648493")
        val s = BigInteger(1, good.copyOfRange(32, 64).reversedArray())
        val mutatedTail = (s + l).toByteArray().reversedArray()
        val mutated = good.copyOf()
        java.util.Arrays.fill(mutated, 32, 64, 0.toByte())
        System.arraycopy(mutatedTail, 0, mutated, 32, minOf(mutatedTail.size, 32))

        assertFalse(Ed25519.verify(TestSigner.publicKey(secret), message, mutated))
    }

    private fun hex(value: String): ByteArray =
        ByteArray(value.length / 2) { index ->
            ((Character.digit(value[index * 2], 16) shl 4) or
                Character.digit(value[index * 2 + 1], 16)).toByte()
        }
}
