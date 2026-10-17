package com.droidmic.app.net

import android.content.Context
import android.net.ConnectivityManager
import android.net.LinkProperties
import android.net.Network
import android.net.NetworkCapabilities
import android.net.wifi.WifiManager
import com.droidmic.core.net.DiscoveredPc
import com.droidmic.core.net.DiscoveryClient
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.channels.awaitClose
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.callbackFlow
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.withContext
import java.net.Inet4Address
import java.net.InetAddress

enum class LinkType { WIFI, USB_TETHER, ETHERNET, OTHER, NONE }

data class NetworkStatus(val type: LinkType, val description: String, val localAddress: String?)

/** Android-specific networking: discovery with the multicast lock, and link status. */
class AndroidNetwork(private val context: Context) {
    private val cm: ConnectivityManager = checkNotNull(context.getSystemService(ConnectivityManager::class.java))

    /** Broadcasts a discovery request on every local IPv4 subnet (Wi-Fi and USB tethering). */
    suspend fun discover(): List<DiscoveredPc> = withContext(Dispatchers.IO) {
        val wifi = context.applicationContext.getSystemService(WifiManager::class.java)
        val lock = wifi?.createMulticastLock("droidmic-discovery")?.apply { setReferenceCounted(false); acquire() }
        try {
            DiscoveryClient().discover(extraBroadcastAddresses = subnetBroadcasts())
        } finally {
            lock?.release()
        }
    }

    private fun subnetBroadcasts(): List<InetAddress> =
        cm.allNetworksCompat().mapNotNull { cm.getLinkProperties(it) }.flatMap { lp ->
            lp.linkAddresses.mapNotNull { la ->
                val a = la.address as? Inet4Address ?: return@mapNotNull null
                val prefix = la.prefixLength
                if (prefix !in 8..30) return@mapNotNull null
                val ip = a.address.fold(0) { acc, b -> (acc shl 8) or (b.toInt() and 0xFF) }
                val mask = -1 shl (32 - prefix)
                val bcast = ip or mask.inv()
                InetAddress.getByAddress(byteArrayOf((bcast ushr 24).toByte(), (bcast ushr 16).toByte(), (bcast ushr 8).toByte(), bcast.toByte()))
            }
        }

    @Suppress("DEPRECATION") // allNetworks is the only way to see the USB tether interface alongside Wi-Fi.
    private fun ConnectivityManager.allNetworksCompat(): List<Network> = allNetworks.toList()

    fun currentStatus(): NetworkStatus {
        // USB tethering shows up as a local link that is not the default network.
        for (n in cm.allNetworksCompat()) {
            val lp = cm.getLinkProperties(n) ?: continue
            if (lp.interfaceName?.startsWith("rndis") == true || lp.interfaceName?.startsWith("usb") == true || lp.interfaceName?.startsWith("ncm") == true) {
                return NetworkStatus(LinkType.USB_TETHER, "USB tethering", ipv4(lp))
            }
        }
        val active = cm.activeNetwork ?: return NetworkStatus(LinkType.NONE, "No network", null)
        val caps = cm.getNetworkCapabilities(active)
        val lp = cm.getLinkProperties(active)
        return when {
            caps == null -> NetworkStatus(LinkType.NONE, "No network", null)
            caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) -> NetworkStatus(LinkType.WIFI, "Wi-Fi", lp?.let(::ipv4))
            caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) -> NetworkStatus(LinkType.ETHERNET, "Ethernet", lp?.let(::ipv4))
            caps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) -> NetworkStatus(LinkType.OTHER, "Mobile data only (connect to the PC's Wi-Fi)", lp?.let(::ipv4))
            else -> NetworkStatus(LinkType.OTHER, "Other network", lp?.let(::ipv4))
        }
    }

    /** Emits whenever connectivity changes, so the UI and session can react. */
    fun statusFlow(): Flow<NetworkStatus> = callbackFlow {
        val cb = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) { trySend(currentStatus()) }
            override fun onLost(network: Network) { trySend(currentStatus()) }
            override fun onLinkPropertiesChanged(network: Network, lp: LinkProperties) { trySend(currentStatus()) }
        }
        trySend(currentStatus())
        cm.registerNetworkCallback(android.net.NetworkRequest.Builder().build(), cb)
        awaitClose { cm.unregisterNetworkCallback(cb) }
    }.distinctUntilChanged()

    private fun ipv4(lp: LinkProperties): String? =
        lp.linkAddresses.map { it.address }.firstOrNull { it is Inet4Address && !it.isLoopbackAddress }?.hostAddress
}
