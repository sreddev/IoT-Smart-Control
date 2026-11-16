# Packaging

Two Windows packages are built from the same self-contained publish (no .NET or Node.js needed on the target):

| Package | Built by | Notes |
|---|---|---|
| `out/IoTSmartControl-<ver>-Setup-x64.exe` | `installer/IoTSmartControl.iss` (Inno Setup 6) | Recommended download. Upgrade / clean install, uninstall keeps or deletes settings. |
| `out/IoTSmartControl_<ver>.0_x64.msix` (+ `IoTSmartControl-Test.cer`) | `build-msix.ps1 -Sign -Version <ver>.0` | Sideload package, signed with a test certificate, numbered like the app. |
| `out/IoTSmartControl_<store ver>_x64_store-upload.msix` | `build-msix.ps1` | Microsoft Store upload (unsigned; the Store signs it). |

**Two version numbers.** The app version (`<Version>` in `src/LedvanceControl.App/LedvanceControl.App.csproj`,
e.g. 2.5.0) names the GitHub release, the installer and the sideload MSIX. The Store numbers its packages on
its own: `AppxManifest.xml` holds the **Store version** (1.0.0.0 was the first Store release). Raise it for
every Store submission (1.0.1.0, 1.1.0.0, …); it must always be higher than the last one submitted.

## Building a release

```powershell
pwsh packaging/build-release.ps1      # tests → publish → bundle Node.js → Setup .exe → signed .msix → Store upload → SHA256SUMS
pwsh packaging/build-release.ps1 -SkipTests -NoMsix   # quick installer-only build
```

Needs the .NET 10 SDK, Node.js + npm (builds the Matter bridge and is bundled), the Windows SDK (`makeappx`,
`signtool`) and **Inno Setup 6** (`winget install JRSoftware.InnoSetup`). The version comes from
`src/LedvanceControl.App/LedvanceControl.App.csproj`; the Store upload takes the Store version from `AppxManifest.xml`.
Node.js is redistributed with its license: download the `LICENSE` for the bundled version from
`https://github.com/nodejs/node/blob/v<version>/LICENSE` into `packaging/obj/node/LICENSE` (or pass
`-NodeLicense`).

## The `.exe` installer

| Behaviour | Detail |
|---|---|
| Install scope | Current user by default (no admin prompt, `%LOCALAPPDATA%\Programs\IoT Smart Control`); "all users" is offered and asks for admin rights. |
| Upgrade | When a previous version or its settings exist, a page offers **Upgrade and keep my settings** (default) or **Clean install**, which deletes the old program files, `%APPDATA%\IoTSmartControl` (config, protected keys, Matter storage, safety copies), the pre-rename `%APPDATA%\LedvanceControl` and the autostart entry before installing. |
| Running app | Closed (with its Matter bridge) before files are replaced. |
| Uninstall | After the usual confirmation it asks **Keep my settings** (default) or **Delete my settings**. The autostart entry is always removed. |
| Unattended | `Setup.exe /VERYSILENT [/CURRENTUSER] [/DIR=…] [/CLEANINSTALL]` · `unins000.exe /VERYSILENT [/DELETEDATA]` |
| Signing | Not code-signed: SmartScreen shows *More info → Run anyway* until a code-signing certificate is used. |

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
2. `Add-AppxPackage packaging/out/IoTSmartControl_<version>_x64.msix`

## How this meets Microsoft Store requirements

- **Package identity** — `Name`, `Publisher`, 4-part `Version` with a **0 revision** (`1.0.0.0`, the Store version).
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
4. Build **unsigned** (`build-msix.ps1` with no `-Sign`) and upload `IoTSmartControl_<store ver>_x64_store-upload.msix` to Partner Center.
   Do **not** sign Store uploads — the Store signs them.
5. `runFullTrust` is a restricted capability: in Partner Center's **Properties**, declare why it's
   needed (full-trust desktop app that performs local-network device control). This is routinely approved.

## Note on settings isolation

Under MSIX, `%APPDATA%` is virtualized per-package, so the packaged app keeps its config in its own
isolated store rather than the unpackaged `%APPDATA%\IoTSmartControl`. A connection configured in the
unpackaged build does not carry into the packaged app (and vice-versa); set up the connection once in
whichever build you keep.
