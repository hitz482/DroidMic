package com.droidmic.core.net

import com.droidmic.core.protocol.DiscoveryCodec
import com.droidmic.core.protocol.Protocol
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.SocketTimeoutException
import java.security.SecureRandom

data class DiscoveredPc(
    val serverId: String,
    val name: String,
    val host: String,
    val controlPort: Int,
    val fingerprint: String,
)

/**
 * LAN discovery by UDP broadcast (protocol §1). On Android the caller must hold a
 * Wi-Fi MulticastLock while this runs and should pass the Wi-Fi subnet's directed
 * broadcast address, since some routers drop 255.255.255.255.
 */
class DiscoveryClient(private val port: Int = Protocol.DISCOVERY_PORT) {
    fun discover(
        extraBroadcastAddresses: List<InetAddress> = emptyList(),
        timeoutMs: Int = 1500,
        rounds: Int = 3,
    ): List<DiscoveredPc> {
        val found = LinkedHashMap<String, DiscoveredPc>()
        val nonce = ByteArray(Protocol.DISCOVERY_NONCE_LENGTH).also { SecureRandom().nextBytes(it) }
        val request = DiscoveryCodec.buildRequest(nonce)
        val targets = (listOf(InetAddress.getByName("255.255.255.255")) + extraBroadcastAddresses).distinct()

        DatagramSocket().use { socket ->
            socket.broadcast = true
            val buf = ByteArray(2048)
            val perRound = timeoutMs / rounds
            repeat(rounds) {
                for (t in targets) {
                    try {
                        socket.send(DatagramPacket(request, request.size, t, port))
                    } catch (_: Exception) {
                        // Some interfaces refuse broadcast; the others may still work.
                    }
                }
                val deadline = System.nanoTime() + perRound * 1_000_000L
                while (true) {
                    val remaining = ((deadline - System.nanoTime()) / 1_000_000L).toInt()
                    if (remaining <= 0) break
                    socket.soTimeout = remaining
                    val packet = DatagramPacket(buf, buf.size)
                    try {
                        socket.receive(packet)
                    } catch (_: SocketTimeoutException) {
                        break
                    }
                    val reply = DiscoveryCodec.parseReply(packet.data, packet.length, nonce) ?: continue
                    val host = packet.address.hostAddress ?: continue
                    found[reply.serverId] = DiscoveredPc(reply.serverId, reply.name, host, reply.ctl, reply.fp)
                }
            }
        }
        return found.values.toList()
    }
}
