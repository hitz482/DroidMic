# Third-party components

All components are redistributable under the licences listed. No GPL code is used.

| Component | Used by | Licence |
|-----------|---------|---------|
| .NET runtime and WPF (bundled in the self-contained exe) | Windows app | MIT |
| NAudio.Wasapi 2.2.1 | Endpoint detection, self-test, monitor | MIT |
| QRCoder 1.6.0 | Pairing QR code | MIT |
| CommunityToolkit.Mvvm 8.3.2 | Desktop view models | MIT |
| Microsoft.Extensions.Logging.Abstractions / Console | Logging | MIT |
| System.Security.Cryptography.ProtectedData | DPAPI | MIT |
| AndroidX (core, activity, lifecycle, Compose, Material 3) | Android app | Apache-2.0 |
| kotlinx.coroutines, kotlinx.serialization | Android core/app | Apache-2.0 |
| ZXing Android Embedded 4.3.0 and ZXing core | QR scanning | Apache-2.0 |
| Windows Driver Kit headers and libraries (portcls, ks, stdunk) | Driver build (not redistributed as source) | Microsoft WDK licence |

The driver is original code. Its architecture (PortCls adapter, WaveRT miniport,
timer-driven position, topology bridge pin) follows the patterns of Microsoft's
SysVAD and SimpleAudioSample samples (MS-PL), but no sample source was copied.
No third-party virtual audio driver is bundled.
