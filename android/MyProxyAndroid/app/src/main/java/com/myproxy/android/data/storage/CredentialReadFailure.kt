package com.myproxy.android.data.storage

import java.security.InvalidAlgorithmParameterException
import javax.crypto.BadPaddingException

/**
 * A stored value exists but cannot be read right now: the keystore failed,
 * not the value.
 *
 * Callers must not read this as "no credential". That path stops the tunnel,
 * moves to UNBOUND and sends the user to the pairing page -- to spend a new
 * one-time code while the credential is still on disk. Background readers
 * skip their work; the connection flow reports a retryable error.
 */
class CredentialUnreadableException(cause: Throwable) :
    IllegalStateException("stored credential is temporarily unreadable", cause)

/**
 * Whether a failure to decrypt a stored value means it can never be read
 * again, so it is as good as absent.
 *
 * Only failures about the bytes themselves count: a GCM tag that does not
 * verify ([BadPaddingException], which AEADBadTagException extends), or a
 * stored IV or encoding that is malformed. A keystore that fails to answer
 * surfaces as KeyStoreException, ProviderException, InvalidKeyException or an
 * IllegalBlockSizeException wrapping one of them, and those can succeed on
 * the next attempt.
 */
internal fun isPermanentReadFailure(failure: Throwable): Boolean =
    failure is BadPaddingException ||
        failure is InvalidAlgorithmParameterException ||
        failure is IllegalArgumentException
