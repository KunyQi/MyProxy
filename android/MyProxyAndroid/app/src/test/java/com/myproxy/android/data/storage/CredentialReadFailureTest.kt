package com.myproxy.android.data.storage

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.security.InvalidAlgorithmParameterException
import java.security.InvalidKeyException
import java.security.KeyStoreException
import java.security.ProviderException
import javax.crypto.AEADBadTagException
import javax.crypto.IllegalBlockSizeException

class CredentialReadFailureTest {

    @Test
    fun `a tag that does not verify means the value is gone for good`() {
        assertTrue(isPermanentReadFailure(AEADBadTagException("tag mismatch")))
    }

    @Test
    fun `malformed stored bytes are gone for good`() {
        assertTrue(isPermanentReadFailure(IllegalArgumentException("bad base64")))
        assertTrue(isPermanentReadFailure(InvalidAlgorithmParameterException("bad iv")))
    }

    @Test
    fun `a keystore that fails to answer is not mistaken for a missing credential`() {
        // Reading these as "absent" is what used to unbind a working device.
        listOf(
            KeyStoreException("keystore busy"),
            ProviderException("Keystore operation failed"),
            InvalidKeyException("Keystore operation failed"),
            IllegalBlockSizeException("wrapped keystore failure"),
            IllegalStateException("binder died"),
        ).forEach { failure ->
            assertFalse(failure.javaClass.simpleName, isPermanentReadFailure(failure))
        }
    }
}
