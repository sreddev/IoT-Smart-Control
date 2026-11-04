App structure (analysis)

- LedvanceControl.App — WPF, net10.0-windows, WinExe, AssemblyName IoTSmartControl; uses WPF + a WinForms tray icon; custom WindowChrome titlebar. References:
- LedvanceControl.Core — net10.0 class library (Tuya LAN/codec, Tuya OpenAPI, SmartThings, light/energy logic, config+DPAPI).
- Config lives in %APPDATA%, secrets via DPAPI. It's a full-trust desktop app (tray, local sockets, file access) → packaged as a Desktop Bridge MSIX, not a sandboxed UWP.

MSIX packaging added (packaging/)

- AppxManifest.xml — Store-compliant: identity (Name/Publisher/Version 1.1.0.0, x64), Windows.Desktop MinVersion 10.0.17763, Windows.FullTrustApplication entry point, VisualElements (tiles + splash), capabilities runFullTrust + internetClient + privateNetworkClientServer.
- Images/ — the complete visual-asset set generated from the Lumen icon: Square 44/71/150/310, Wide 310×150, StoreLogo 50, SplashScreen, and target-size app-list icons (16–256, plated + unplated), each at scale-100 and scale-200.
- build-msix.ps1 — publishes self-contained (no .NET needed on target), stages manifest+assets, packs with makeappx; -Sign/-Install create/trust a self-signed cert for local sideload.
- packaging/README.md — build/sideload steps, the Store requirements it satisfies, and the identity values to swap in before submission.

Verified

build-msix.ps1 produced a valid 72 MB x64 IoTSmartControl_1.1.0.0_x64.msix — makeappx pack and an unpack round-trip both succeeded. Committed and pushed (5bc5ae9); the .msix/cert outputs are git-ignored.

To actually ship to the Store (your steps)

1. Reserve the name in Partner Center, then from Product → Product identity copy the real Name, Publisher, and PublisherDisplayName into AppxManifest.xml (keep version revision 0).
2. pwsh packaging/build-msix.ps1 (unsigned) and upload the .msix — the Store signs it; don't sign Store uploads.
3. In Partner Center, justify runFullTrust (full-trust desktop app doing local-network device control) — routinely approved.

Two things worth knowing:
- Settings isolation: under MSIX, %APPDATA% is virtualized per-package, so the packaged app has its own config store — your current unpackaged connection won't carry into the packaged app (documented in packaging/README.md).
- For a local test install, run pwsh packaging/build-msix.ps1 -Install elevated (it trusts the test cert and installs). Want me to also add an arm64 build to the same flow, or wire an optional .wapproj for Visual Studio-based submission?