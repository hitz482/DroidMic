# Building the Windows side

## Prerequisites

| Component | Needed for | Notes |
|-----------|-----------|-------|
| .NET 8 SDK (Microsoft build) | App, tools, tests | On Linux, the distro's source-built SDK lacks the WPF targets. Use Microsoft's SDK (`dotnet-install` or packages.microsoft.com) to compile `DroidMic.Desktop` there. |
| Visual Studio 2022 + *Desktop development with C++* | Driver | |
| Windows SDK + **WDK** (same version) | Driver | https://learn.microsoft.com/windows-hardware/drivers/download-the-wdk |
| Inno Setup 6 | Installer | `choco install innosetup` |

## Commands

```powershell
# Tests (protocol, audio engine, server integration, C#/C driver contract)
dotnet test windows/tests/DroidMic.Tests

# Desktop app -> dist/windows/app/DroidMic.exe (self-contained, single file, x64)
./scripts/build-windows.ps1 -Configuration Release

# Driver -> dist/windows/app/driver (TEST-SIGNED)
./scripts/build-driver.ps1 -Configuration Release

# Installer + zip + checksums -> dist/
./scripts/package-release.ps1 -SkipBuild
```

Cross-compiling the app from Linux/macOS works (`EnableWindowsTargeting` is set):

```bash
dotnet publish windows/DroidMic.Desktop -c Release -o dist/windows/app
```

## Projects

* `DroidMic.Protocol`, `DroidMic.AudioEngine`, `DroidMic.Networking`: `net8.0`,
  cross-platform, fully unit-tested.
* `DroidMic.Platform`: Windows-only integrations (driver IOCTLs, Core Audio, DPAPI, firewall).
* `DroidMic.Desktop`: WPF UI. Single instance, tray icon, settings in `%AppData%\DroidMic`.
* `DroidMic.DriverSetup`: elevated installer helper (`install <inf>` / `uninstall` / `status`).
* `DroidMic.ReceiverCli`: headless receiver (`--pair`, `--pin`, `--wav out.wav`) for tests and diagnostics.

## Running without the driver

DroidMic.exe works without the driver: it receives, meters and shows statistics,
and the dashboard says *Virtual microphone not installed*. The optional *monitor*
setting plays the phone audio on the PC speakers so you can check it by ear.
Other apps cannot use the phone until the driver is installed.
