package com.myproxy.android.domain.connection

import com.myproxy.android.domain.model.AppState
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class ConnectionStateMachineTest {

    @Test
    fun `all legal transitions`() {
        val machine = ConnectionStateMachine(AppState.UNBOUND)

        machine.bindSuccess()
        assertEquals(AppState.DISCONNECTED, machine.state)

        machine.connect()
        assertEquals(AppState.CONNECTING, machine.state)

        machine.connectSuccess()
        assertEquals(AppState.CONNECTED, machine.state)

        machine.disconnect()
        assertEquals(AppState.DISCONNECTING, machine.state)

        machine.disconnectDone()
        assertEquals(AppState.DISCONNECTED, machine.state)

        machine.connect()
        machine.connectFailure()
        assertEquals(AppState.ERROR, machine.state)

        machine.connect()
        machine.connectSuccess()
        assertEquals(AppState.CONNECTED, machine.state)

        machine.rebind()
        assertEquals(AppState.UNBOUND, machine.state)
    }

    @Test
    fun `connect from unbound is illegal`() {
        val machine = ConnectionStateMachine(AppState.UNBOUND)
        assertThrows(IllegalStateException::class.java) { machine.connect() }
    }

    @Test
    fun `connectSuccess from disconnected is illegal`() {
        val machine = ConnectionStateMachine(AppState.UNBOUND)
        machine.bindSuccess()
        assertThrows(IllegalStateException::class.java) { machine.connectSuccess() }
    }

    @Test
    fun `disconnect from disconnected is illegal`() {
        val machine = ConnectionStateMachine(AppState.UNBOUND)
        machine.bindSuccess()
        assertThrows(IllegalStateException::class.java) { machine.disconnect() }
    }

    @Test
    fun `disconnectDone from connected is illegal`() {
        val machine = ConnectionStateMachine(AppState.UNBOUND)
        machine.bindSuccess()
        machine.connect()
        machine.connectSuccess()
        assertThrows(IllegalStateException::class.java) { machine.disconnectDone() }
    }

    @Test
    fun `rebind from unbound is illegal`() {
        val machine = ConnectionStateMachine(AppState.UNBOUND)
        assertThrows(IllegalStateException::class.java) { machine.rebind() }
    }
}
