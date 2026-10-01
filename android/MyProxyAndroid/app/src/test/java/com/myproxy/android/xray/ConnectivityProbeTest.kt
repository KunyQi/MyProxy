package com.myproxy.android.xray

import java.net.ServerSocket
import java.net.Socket
import java.net.Proxy
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.TimeUnit
import kotlin.concurrent.thread
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import okhttp3.OkHttpClient
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test

class ConnectivityProbeTest {
    private fun client() = OkHttpClient.Builder().proxy(Proxy.NO_PROXY)
        .followRedirects(false).followSslRedirects(false)
        .callTimeout(5, TimeUnit.SECONDS).build()

    @Test
    fun `failed endpoint falls back and healthy endpoint is reused`() = runBlocking {
        Stub { path -> if (path == "/bad") response(503) else response(204) }.use { stub ->
            val probe = ConnectivityProbe(urls = listOf(stub.url("/bad"), stub.url("/good")), clientOverride = client())
            assertTrue(probe.measure() >= 0)
            assertTrue(probe.measure() >= 0)
            assertEquals(listOf("/bad", "/good", "/good"), stub.paths.toList())
        }
    }

    @Test
    fun `redirect is not followed and next configured endpoint can succeed`() = runBlocking {
        Stub { path -> if (path == "/bad") response(302, "Location: /redirected\r\n") else response(204) }.use { stub ->
            val probe = ConnectivityProbe(urls = listOf(stub.url("/bad"), stub.url("/good")), clientOverride = client())
            assertTrue(probe.measure() >= 0)
            assertEquals(listOf("/bad", "/good"), stub.paths.toList())
        }
    }

    @Test
    fun `portal replies and nonempty 204 cannot verify a tunnel`() = runBlocking {
        for (reply in listOf(response(200), "HTTP/1.1 204 No Content\r\nContent-Length: 1\r\nConnection: close\r\n\r\nx")) {
            Stub { reply }.use { stub ->
                val probe = ConnectivityProbe(urls = listOf(stub.url("/one"), stub.url("/two")), clientOverride = client())
                try { probe.measure(); fail("All rejected replies must fail the probe") }
                catch (_: IllegalStateException) { }
                assertEquals(listOf("/one", "/two"), stub.paths.toList())
            }
        }
    }

    @Test
    fun `cancelling an active call closes it and does not try another candidate`() = runBlocking {
        Stub { null }.use { stub ->
            val probe = ConnectivityProbe(urls = listOf(stub.url("/slow"), stub.url("/second")), clientOverride = client())
            val task = launch(Dispatchers.Default) { probe.measure() }
            withTimeout(2_000) { stub.accepted.await() }
            withTimeout(2_000) { task.cancelAndJoin() }
            assertTrue(task.isCancelled)
            assertEquals(listOf("/slow"), stub.paths.toList())
        }
    }

    @Test
    fun `shared budget leaves time for the last candidate after three timeouts`() = runBlocking {
        Stub { path -> if (path == "/good") response(204) else null }.use { stub ->
            val probe = ConnectivityProbe(urls = listOf(stub.url("/slow1"), stub.url("/slow2"),
                stub.url("/slow3"), stub.url("/good")), clientOverride = client())
            assertTrue(probe.measure(budgetMs = 2_000) >= 0)
            assertEquals(listOf("/slow1", "/slow2", "/slow3", "/good"), stub.paths.toList())
        }
    }

    private fun response(status: Int, extra: String = "") =
        "HTTP/1.1 $status Test\r\n${extra}Content-Length: 0\r\nConnection: close\r\n\r\n"

    private class Stub(private val answer: (String) -> String?) : AutoCloseable {
        private val server = ServerSocket(0, 8, java.net.InetAddress.getByName("127.0.0.1"))
        private val sockets = CopyOnWriteArrayList<Socket>()
        val paths = CopyOnWriteArrayList<String>()
        val accepted = CompletableDeferred<Unit>()
        init {
            thread(isDaemon = true) {
                try {
                    while (!server.isClosed) {
                        val socket = server.accept()
                        sockets += socket
                        socket.use {
                            val reader = it.getInputStream().bufferedReader(Charsets.US_ASCII)
                            val path = reader.readLine().split(' ')[1]
                            while (!reader.readLine().isNullOrEmpty()) { }
                            paths += path
                            accepted.complete(Unit)
                            val reply = answer(path)
                            if (reply == null) reader.read() else it.getOutputStream().apply {
                                write(reply.toByteArray(Charsets.US_ASCII)); flush()
                            }
                        }
                        sockets -= socket
                    }
                } catch (_: java.io.IOException) { }
            }
        }
        fun url(path: String) = "http://127.0.0.1:${server.localPort}$path"
        override fun close() { server.close(); sockets.forEach { it.close() } }
    }
}
