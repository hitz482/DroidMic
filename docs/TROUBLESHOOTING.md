# Troubleshooting

## The phone can't find or reach the PC

1. **Same network?** The phone must be on the same Wi-Fi (or USB-tethered) as the
   PC. Mobile data won't work.
2. **Guest Wi-Fi / client isolation.** Many guest networks, hotel and campus Wi-Fi,
   and some mesh routers with "AP isolation" block traffic between devices.
   Discovery then finds nothing and manual connection times out. Use your main
   network, turn off isolation in the router, or use **USB tethering**.
3. **Windows Firewall.** Allow DroidMic when Windows asks, or use DroidMic →
   Diagnostics → *Allow DroidMic through firewall*. If your network is set to
   **Public**, Windows applies stricter rules. Set your home network to Private
   (Settings → Network & Internet → Wi-Fi → your network).
4. **Discovery blocked but connection works?** Some routers drop broadcast. Use
   *Enter address manually* with the address shown under *Pair phone* on the PC.
5. **VPN on either device** can route LAN traffic away. Disconnect it or allow LAN access.

## USB connection

1. Connect the phone by USB and turn on **USB tethering** (Settings → Network → Hotspot & tethering).
2. On the PC, *Pair phone* lists an address on a "Remote NDIS" adapter
   (often 192.168.42.x or 192.168.x.x). Enter that address on the phone, or let
   discovery find it.
3. Audio then flows over the USB cable with ordinary sockets. (`adb reverse` is
   not supported because it does not forward UDP.)

## Pairing

| Message on the phone | Fix |
|---|---|
| *Pairing is not open on the PC* | Click **Pair phone → Show PIN and QR code** on the PC first. |
| *Wrong PIN* | Re-enter. After 5 wrong PINs, pairing locks; open it again for a new PIN. |
| *This phone is no longer paired* | It was removed on the PC; pair again. |
| *Security warning: identity does not match* | The PC's identity changed (DroidMic reinstalled, or a different PC with the same address). Re-pair only if you know why. |
| *Another phone is already connected* | One phone at a time. Disconnect the other phone on the PC. |

## Sound problems

| Symptom | Fix |
|---|---|
| PC meter moves, apps hear nothing | Select **DroidMic Microphone** in the app. Check it is not muted in Sound settings → Input → DroidMic Microphone → volume. Check Settings → Privacy → Microphone allows desktop apps. |
| No "DroidMic Microphone" in Windows | The driver is not installed or not running. See [DRIVER_INSTALLATION.md](DRIVER_INSTALLATION.md). The dashboard says *Virtual microphone not installed*. |
| Crackles / dropouts | Diagnostics: if packet loss is above 1 % or jitter above 20 ms, move closer to the router, prefer 5 GHz, choose the **Stable** latency preset on the PC, and disable battery optimisation for DroidMic on the phone. |
| Too quiet | Raise *Input gain* on the phone (Audio tab). "Raw" mode is quieter by design. |
| Clipping (red meter) | Lower the gain. |
| Phone shows "Another app is using the microphone" | A call, voice assistant or recorder took the mic. Android delivers silence until it is released. |
| Stops when the screen turns off | Turn off battery optimisation for DroidMic (Audio tab → Battery). Some OEM skins (Xiaomi, Huawei, Samsung "deep sleep") need DroidMic exempted manually. |
| Latency grows over time | It shouldn't: the PC compensates for clock drift and caps the buffer. If it does, send the Diagnostics numbers in a bug report. |

## Logs

* PC: `%LocalAppData%\DroidMic\logs\droidmic.log` (Diagnostics → *Open log folder*).
* Phone: `adb logcat -s DroidMicCapture DroidMicService`.
* Headless receiver for network testing: `dotnet run --project windows/DroidMic.ReceiverCli -- --pair`.
