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
import java.net.NetworkInterface

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

    /**
     * Directed broadcast address of every up IPv4 interface. java.net is used because the
     * USB-tethering interface (rndis/ncm) is not exposed as a ConnectivityManager Network.
     */
    private fun subnetBroadcasts(): List<InetAddress> = try {
        NetworkInterface.getNetworkInterfaces().toList()
            .filter { it.isUp && !it.isLoopback }
            .flatMap { nic -> nic.interfaceAddresses.mapNotNull { it.broadcast } }
            .distinct()
    } catch (e: Exception) {
        emptyList()
    }

    /** IPv4 address of an active USB tethering interface, if any. */
    private fun usbTetherAddress(): String? = try {
        NetworkInterface.getNetworkInterfaces().toList()
            .filter { it.isUp && (it.name.startsWith("rndis") || it.name.startsWith("ncm") || it.name.startsWith("usb")) }
            .flatMap { it.inetAddresses.toList() }
            .firstOrNull { it is Inet4Address }?.hostAddress
    } catch (e: Exception) {
        null
    }

    fun currentStatus(): NetworkStatus {
        usbTetherAddress()?.let { return NetworkStatus(LinkType.USB_TETHER, "USB tethering", it) }
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
