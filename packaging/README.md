# MSIX packaging

Packages **IoT Smart Control** as an `.msix` for the Microsoft Store (or sideloading).
The app is a full-trust WPF desktop app, so it's packaged as a **Desktop Bridge** app:
`Windows.FullTrustApplication` entry point + the `runFullTrust` restricted capability.

## Contents

| File | Purpose |
|---|---|
| `AppxManifest.xml` | Package manifest (identity, capabilities, visual elements). |
| `Images/` | Full visual-asset set (tiles, Store logo, splash, target-size app icons) at scale-100 / scale-200. |
| `build-msix.ps1` | Publishes the app self-contained and packs the `.msix` with the Windows SDK. |

## Build

```powershell
# Unsigned package to upload to Partner Center (the Store signs it):
pwsh packaging/build-msix.ps1

# Signed package for local sideload testing (self-signed cert):
pwsh packaging/build-msix.ps1 -Sign

# Build, trust the test cert and install it in one go (run elevated):
pwsh packaging/build-msix.ps1 -Install

# Apple-silicon / ARM devices:
pwsh packaging/build-msix.ps1 -Runtime win-arm64 -Sign
```

Output lands in `packaging/out/IoTSmartControl_<version>_<arch>.msix` (git-ignored).
The app is published **self-contained**, so the target PC needs no .NET runtime.

### Sideload install (what `-Install` automates)

1. Trust the exported `packaging/out/IoTSmartControl-Test.cer` into
   **Local Machine → Trusted People** (self-signed certs aren't trusted by default).
2. `Add-AppxPackage packaging/out/IoTSmartControl_1.1.0.0_x64.msix`

## How this meets Microsoft Store requirements

- **Package identity** — `Name`, `Publisher`, 4-part `Version` with a **0 revision** (`1.1.0.0`).
- **Architecture** — `x64` (and `arm64` via `-Runtime`). Submit each as a package in one submission.
- **Target platform** — `Windows.Desktop`, `MinVersion 10.0.17763.0` (1809).
- **Visual assets** — Square 44/71/150/310, Wide 310×150, Store logo (50), splash, and
  target-size app-list icons (16/24/32/48/256, plated + unplated), each at scale-100 and scale-200.
- **Capabilities** — `runFullTrust` (desktop bridge), `internetClient` (Tuya/SmartThings cloud),
  `privateNetworkClientServer` (Tuya LAN).
- **Self-contained** — no runtime dependency on the user's machine.

## Before submitting to the Store

1. Reserve the app name in **Partner Center** and open **Product → Product identity**.
2. In `AppxManifest.xml` replace, with the exact values shown there:
   - `Identity/@Name` (e.g. `1234Sreddev.IoTSmartControl`)
   - `Identity/@Publisher` (e.g. `CN=ABCDEF12-3456-7890-ABCD-1234567890AB`)
   - `Properties/PublisherDisplayName`
3. Keep the version revision at `0` (`w.x.y.0`).
4. Build **unsigned** (`build-msix.ps1` with no `-Sign`) and upload the `.msix` to Partner Center.
   Do **not** sign Store uploads — the Store signs them.
5. `runFullTrust` is a restricted capability: in Partner Center's **Properties**, declare why it's
   needed (full-trust desktop app that performs local-network device control). This is routinely approved.

## Note on settings isolation

Under MSIX, `%APPDATA%` is virtualized per-package, so the packaged app keeps its config in its own
isolated store rather than the unpackaged `%APPDATA%\IoTSmartControl`. A connection configured in the
unpackaged build does not carry into the packaged app (and vice-versa); set up the connection once in
whichever build you keep.
