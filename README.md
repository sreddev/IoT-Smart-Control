# LEDVANCE Smart+ WiFi Control for Windows

An unofficial Windows desktop app (C# / .NET 10 / WPF) for LEDVANCE SMART+ WiFi lights and plugs.
It reimplements the features of the Android app *LEDVANCE SMART+ WiFi 1.3.2*.

## What the APK turned out to be

`LEDVANCE+SMART++WiFi_1.3.2_APKPure.apk` (package `com.ldvsmartwifi.smart`) is a white-label
**Tuya Smart** app (Tuya "OEM App" SDK, Flutter shell plus native Tuya modules):

| Area | Findings in the APK |
|---|---|
| Cloud | Tuya mobile API (`a1.tuyaeu.com`, `a1.tuyaus.com`, …), `tuya.m.device.*` actions, signed with an app key/secret that is obfuscated in the APK (`t_s.bmp`, native libs) |
| Tabs | `ty_device`, `ty_group`, `ty_scene`, `mall`, `ty_personal` (`assets/tab_config.json`) |
| Device panels | downloaded at runtime (React Native/Web panels) and driven by each device's DP (data point) schema |
| Modules | pairing (EZ/AP/BLE), groups, scenes and automations, timers, sharing, OTA, Alexa/Google, cameras, BLE mesh, geofencing, push notifications |
| Local control | Tuya LAN protocol (TCP 6668, UDP broadcasts 6666/6667/7000) |

This app doesn't reuse the vendor's embedded credentials. It uses the two supported routes into Tuya devices instead:

1. **Tuya LAN protocol** (3.1, 3.3, 3.4 and 3.5): direct, cloud-free control over your Wi-Fi.
   It covers AES-ECB/CRC32 framing, 3.4 session-key negotiation with HMAC-SHA256, 3.5 AES-GCM framing and UDP discovery.
2. **Official Tuya IoT Platform OpenAPI**: gets device lists, local keys, DP schemas, rooms, cloud scenes and automations, and is the fallback channel when a device isn't reachable on the LAN.

## Features

| Android app | This app |
|---|---|
| Device list grouped by room, on/off | **Devices** page, grouped by room, quick toggles, LAN/Cloud/Offline status |
| Light panel | Power, white mode (brightness, colour temperature), colour mode (hue/saturation/brightness, quick colours), the 8 built-in Tuya scenes (Night, Reading, …) |
| Generic device panels (plugs, switches, …) | Every DP rendered from the device schema: switches, sliders, drop-downs, text. Read-only values (power, voltage, …) are shown too |
| Groups tab | **Groups**: user-defined groups controlled together (power, brightness, colour, scenes) |
| Scenes tab | **Scenes**: local tap-to-run scenes captured from current device states, plus cloud scenes (run) and cloud automations (enable/disable) |
| Timers / schedules | **Schedules**: daily/weekday timers for a device, group or scene, with optional brightness. Closing the window keeps the app in the tray while schedules are enabled |
| Account / home | **Settings**: cloud project, cloud sync (devices, keys, schemas, rooms), LAN discovery, tinytuya `devices.json` import, manual devices, per-device IP/key/version/LAN-first |

Not implemented, because they need Tuya's private app credentials or hardware Windows doesn't have:
Wi-Fi pairing of *new* devices, BLE mesh, cameras, OTA, device sharing, Alexa/Google linking.
Pair new devices with the phone app once; this app then controls them.

## Getting started

1. Build and run:
   ```
   dotnet build LedvanceControl.slnx -c Release
   dotnet run --project src/LedvanceControl.App
   ```
   Or run `dotnet test` for the protocol tests. A simulated 3.3/3.4/3.5 device is in `tests/LedvanceControl.Tests/FakeTuyaDevice.cs`.
2. Get your devices' **local keys**, using either option:
   - **Cloud sync (recommended):** create a free *Cloud Development* project at <https://iot.tuya.com> in the data centre matching your account
     (Europe = `eu`). Enable the *IoT Core* and *Smart Home Scene Linkage* APIs. Link your app account under
     *Devices → Link App Account* by scanning the QR code with the app. Then paste the Access ID and Secret into **Settings** and press
     *Save & synchronise devices*.
     If your LEDVANCE account can't be linked (only Tuya-platform apps can be linked), add the bulbs to the free *Smart Life*
     app instead and link that account.
   - **tinytuya:** if you already have a `devices.json` from `python -m tinytuya wizard`, use *Import tinytuya devices.json…*.
3. Press **Discover devices** so the app learns each device's LAN IP and protocol version. From then on, control goes over the LAN.

Configuration is stored in `%APPDATA%\LedvanceControl\config.json`. The cloud secret and local keys are encrypted with Windows DPAPI.
Set `LEDVANCE_CONFIG` to use a different file.

## Project layout

```
src/LedvanceControl.Core     protocol + logic (no UI)
  Local/   TuyaCodec (framing, crypto), TuyaLocalClient (TCP session), TuyaDiscovery (UDP)
  Cloud/   TuyaCloudClient (OpenAPI signing, devices, schema, shadow, homes, scenes, automations)
  Lights/  HsvColor (Tuya colour encodings), LightProfile (DP mapping, scenes)
  Model/   config, DPAPI store, default DP schemas, tinytuya importer
  Services/DeviceManager, DeviceRuntime (LAN-first with cloud fallback), Scheduler
src/LedvanceControl.App      WPF UI (MVVM)
tests/LedvanceControl.Tests  xUnit tests incl. a fake Tuya device
```
