# Virtual microphone driver: build, signing and installation

The **DroidMic Microphone** endpoint is provided by a kernel-mode driver,
`DroidMicVAD.sys` (`driver/VirtualAudioDevice`). Windows only loads kernel drivers
that carry an acceptable signature. That gives two separate workflows.

> **Status of this repository:** the driver source, INF and project are complete.
> They have **not** yet been compiled with the WDK or loaded on Windows. No signed
> driver is included. See PROGRESS.md.

## 1. Developer / test installation (your own PC only)

Use this for development. It requires **test-signing mode**, which weakens
Windows security. Use a dedicated test PC or VM, and switch test-signing off
afterwards. Never ask end users to do this.

Prerequisites: Visual Studio 2022 with *Desktop development with C++*, the
Windows 11 SDK, and the matching **Windows Driver Kit** (WDK) with its Visual
Studio extension.

```powershell
# 1. Build (signs with an auto-generated WDK test certificate)
./scripts/build-windows.ps1
./scripts/build-driver.ps1 -Configuration Release
./scripts/build-driver.ps1 -Configuration Release -VerifyOnly   # InfVerif

# 2. On the TEST machine, elevated:
bcdedit /set testsigning on          # BitLocker users: suspend BitLocker first
# Secure Boot must be off for test-signing to take effect on most PCs.
shutdown /r /t 0

# 3. Trust the test certificate (copied next to the package by build-driver.ps1)
certutil -addstore root    dist\windows\app\driver\DroidMicTestCert.cer
certutil -addstore TrustedPublisher dist\windows\app\driver\DroidMicTestCert.cer

# 4. Install (creates Root\DroidMicVAD and installs the package)
dist\windows\app\driver\DroidMic.DriverSetup.exe install dist\windows\app\driver\DroidMicVAD.inf
dist\windows\app\driver\DroidMic.DriverSetup.exe status
```

Then open DroidMic → **Microphone setup** → *Test the microphone endpoint*. The
self-test plays a tone through the driver and records it back from
"DroidMic Microphone" with WASAPI, exactly as any recording app would.

Remove it all again:

```powershell
DroidMic.DriverSetup.exe uninstall      # removes the device node and the driver-store package
bcdedit /set testsigning off
certutil -delstore root "WDKTestCert <your user>"
```

Recommended while developing: enable **Driver Verifier** for `DroidMicVAD.sys`
(`verifier /standard /driver DroidMicVAD.sys`). Then exercise start/stop, format
negotiation, sleep/resume, and app crash while streaming.

## 2. Consumer installation (release builds)

Ordinary Windows 10/11 PCs with Secure Boot load only drivers signed by
Microsoft. Self-signed and test-signed drivers do not load, and there is no
legitimate way around that.

Steps to publish a signed driver:

1. **Extended Validation (EV) code-signing certificate** from a CA that Microsoft
   accepts (hardware token or cloud HSM). It costs money and requires
   organisation verification.
2. Register the organisation in the **Windows Hardware Dev Center (Partner Center)**
   and associate the EV certificate.
3. Build the Release package and create a `.cab` containing `DroidMicVAD.inf`,
   `DroidMicVAD.sys` and `DroidMicVAD.cat`; sign the `.cab` with the EV cert.
4. Submit for **attestation signing** (Windows 10/11 client). Microsoft returns
   the package signed with its own certificate. Attestation-signed drivers
   install on Windows 10 1607+ and Windows 11 with Secure Boot on.
   (For Windows Update distribution or a "Certified" listing you need HLK
   testing instead. HLK testing is optional for direct distribution.)
5. Put the Microsoft-signed files in `dist/windows/app/driver`, write `release`
   into `signing.txt`, and run `scripts/package-release.ps1`. The installer then
   installs the driver without warnings.

Until those steps are done, release notes **must** state that the driver is
test-signed and the virtual microphone works only on test-mode PCs.

## What the installer does

* Copies `DroidMic.exe` and the driver package to `C:\Program Files\DroidMic`.
* If the "Install the DroidMic virtual microphone driver" task is selected, runs
  `DroidMic.DriverSetup.exe install`. This calls
  `SetupDiCreateDeviceInfo` + `DIF_REGISTERDEVICE` to create `Root\DroidMicVAD`, then
  `UpdateDriverForPlugAndPlayDevices`. It uses no undocumented APIs and changes
  no security settings.
* Adds inbound firewall rules named `DroidMic` for TCP 48110 and UDP 48111–48112 on
  **private** networks only (optional task).
* The uninstaller runs `DroidMic.DriverSetup.exe uninstall`, which removes the
  device node and deletes the package from the driver store
  (`pnputil /delete-driver oemNN.inf /uninstall`). It also deletes the firewall
  rules and logs. Nothing is left behind except settings in
  `%AppData%\DroidMic`, which you can delete by hand.

## Troubleshooting installation

| Symptom | Cause / fix |
|---------|-------------|
| `DriverSetup` error `0xE0000247`, or Device Manager **Code 52** | Signature not accepted. Use test mode (dev) or a Microsoft-signed package (release). |
| Device present but no "DroidMic Microphone" in Sound settings | Check Device Manager → *Sound, video and game controllers* → DroidMic Virtual Audio Device for a problem code. Run `DriverSetup status`. |
| Endpoint shows as "Microphone (DroidMic Virtual Audio Device)" | The MediaCategories name entry was not written; reinstall. Apps still work. |
| Recording apps hear silence | DroidMic isn't receiving from a phone, or the endpoint is muted. Check the dashboard meter and Sound settings. |
