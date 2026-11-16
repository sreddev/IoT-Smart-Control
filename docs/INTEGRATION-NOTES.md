# Integration notes: LEDVANCE SMART+, Matter, SmartThings and Tuya

What we verified (or ruled out) while adding hub-free Matter control and the House → Room → Device
model to IoT Smart Control. Each finding says how it was established, so it can be re-checked when
firmware or apps change. Dates are when the finding was made (November 2026).

## 1. LEDVANCE SMART+ app and cloud

| Finding | How we know |
|---|---|
| The current **LEDVANCE SMART+** app (the one that adds **Matter** bulbs) keeps the **house name, room names and device names in LEDVANCE's own cloud**. | Reading the bulb over Matter (§2.3) shows none of them stored on the device. |
| That cloud has **no public, documented API**. | No developer programme or API documentation exists; the app's traffic is private. |
| This project therefore **does not read SMART+ data**. Doing so would mean reverse-engineering the vendor app or reusing its embedded credentials, which the project deliberately avoids (see the README). | Design decision. |
| SMART+ commissions Matter bulbs into **its own Matter fabric** (one controller per phone account; the bulbs report it as an operational fabric). The app's "Wi-Fi control" option puts the bulb on Wi-Fi as a Matter-over-Wi-Fi node. | mDNS `_matter._tcp` adverts from the bulbs (§2.4). |
| The older **SMART+ WiFi** app (v1.3.x) is a white-label **Tuya** client; its bulbs speak the Tuya LAN protocol. Bulbs that are brand-locked to LEDVANCE can't be re-paired to generic Tuya/Smart Life. | Existing project findings (README "Brand-locking"). |

**Consequences for the app**
- Names, rooms and the house name are **set in this app** (or imported from SmartThings / Tuya, §3–4), and user edits are pinned so later syncs never overwrite them.
- The app **cannot verify** where a bulb lives in SMART+. Moving a Matter bulb to another house in this app therefore requires the user to **confirm they already moved it in SMART+** (§5).

## 2. Matter devices (LEDVANCE SMART MAT A100 RGBW, Matter over Wi-Fi)

### 2.1 Device facts (read over Matter)

| Attribute | Value |
|---|---|
| Vendor / product | `LEDVANCE` (vendor id **4489**), `SMART MAT A100 RGBW 827 FR E27` (product id **2756**), part number `A60` |
| Firmware | `1.1.0` (software version 1), hardware `1.0` |
| Data model | revision 17; Matter specification 1.3 (`specificationVersion` 0x01030000) |
| Endpoints | 0 = root node; 1 = **Extended Color Light** (`0x010D`) with Identify, Groups, On/Off, Level Control, Descriptor, Color Control |
| Colour | hue/saturation **and** XY, colour temperature **2703–6536 K** (`colorTempPhysicalMin/MaxMireds`) |
| Brightness | Level Control 1–254 (the app maps it to 1–100 %) |
| Session parameters | `SII=200 ms`, `SAI=300 ms`, `SAT=4000 ms`; `T=1` (TCP supported) in its commissionable advert |
| Fabrics / sessions | `caseSessionsPerFabric` 3; `subscriptionsPerFabric` 65535 |
| Labels | `nodeLabel` is **empty**; **no User Label or Fixed Label cluster** on any endpoint |
| Location | BasicInformation `location` = `XX` (unset) |

### 2.2 Control

- Works fully over the LAN with no hub and no internet: On/Off, `moveToLevelWithOnOff`, `moveToHueAndSaturation`, `moveToColor` (XY) and `moveToColorTemperature`, with attribute subscriptions pushing state changes made elsewhere (e.g. in SMART+) to the PC.
- Verified end to end: pairing, on/off, 20 % / 100 % brightness, 2703 K / 6536 K white, red / green / blue, off / on.
- Colour commands are ignored while the bulb is off (Matter `executeIfOff` defaults to false), so the app always sends On first.

### 2.3 What the bulb does *not* carry

The SMART+ name and room are **not** on the bulb: `BasicInformation.nodeLabel` is empty and there is no User Label / Fixed Label cluster. Any Matter controller (including this app) therefore starts with the product name, e.g. "SMART MAT A100 RGBW 827 FR E27".

### 2.4 Pairing a second controller (multi-admin)

- A bulb already in SMART+ stays there: this PC joins as an **additional Matter controller** with its own fabric (a bulb supports several fabrics).
- The user opens a pairing window in the bulb's current app (share / link with another Matter app) and gets an 11-digit manual code (or `MT:` QR payload). The bulb then advertises `_matterc._udp` with `CM=2` (enhanced commissioning), its discriminator `D=`, `VP=4489+2756` and `DN=` (device name).
- Manual codes carry a **Verhoeff check digit**. A mistyped code fails locally with "Invalid checksum" before anything is sent, so ask the user to re-read it.
- Pairing windows close after a few minutes; a failed attempt costs a fresh code.
- Device attestation passes; without a DCL certificate service the controller accepts it "for backward compatibility" (logged as a warning).

### 2.5 Windows networking pitfalls (each one blocked pairing until fixed)

1. **Windows Firewall:** Node.js (which runs the Matter controller) had **no inbound rule**, so device-initiated UDP (mDNS replies, subscription reports) was dropped. Fix: allow `node.exe` inbound UDP on *Private* networks:
   ```powershell
   New-NetFirewallRule -DisplayName "Node.js - IoT Smart Control Matter" -Program "C:\Program Files\nodejs\node.exe" -Direction Inbound -Action Allow -Protocol UDP -Profile Private
   ```
2. **No IPv6 on the Wi-Fi adapter:** matter.js requires IPv6 on the interface it is pinned to (`mdns.networkInterface`), and crashes with "No IPv6 addresses on interface" otherwise. Unpinned, it works over IPv4 mDNS, and the operational CASE session ended up on the bulb's IPv6 **link-local** address (`fe80::…%<ifindex>`).
3. **Two adapters on the same subnet** (Wi-Fi plus a Hyper-V external switch bound to a USB Ethernet adapter): Windows routes IPv4 to the bulb via the lower-metric adapter. Pairing worked once, after earlier attempts stalled at the final "Reconnect" (CASE) step; this is the prime suspect if a bulb drops offline.
4. **mDNS port 5353 is shared** with other software (Bonjour `mDNSResponder`, Quick Share, ChatGPT desktop). Node still receives multicast replies with `reuseAddr`, so this was *not* the cause, but it is worth knowing when debugging discovery.
5. Measured round-trips to a bulb: ~58 ms via the wired adapter, ~85 ms via Wi-Fi (IPv4), ~72 ms via IPv6 link-local. The bulb's own Wi-Fi power saving dominates, so forcing a specific adapter brings little.

### 2.6 Matter over Thread

Bulbs that use **Thread** instead of Wi-Fi can't be reached from a PC (no Thread radio): they need a Thread border router (HomePod mini, Nest Hub, recent Echo, SmartThings Station).

## 3. Samsung SmartThings

| Finding | Detail |
|---|---|
| **Matter devices need a SmartThings hub.** | SmartThings runs Matter devices only through a hub acting as the Matter controller (SmartThings Station / Aeotec hub, or a TV / fridge with a built-in hub). Without one, SMART+ Matter bulbs can't be controlled through SmartThings, which is why this app talks Matter directly. |
| **Location = house.** | Every device has a `locationId`. `GET /locations/{id}` returns the location `name` (scope `r:locations:*`), and `GET /locations/{id}/rooms` lists its rooms. The app maps each location to one house, and the device's room id to one of that house's rooms. |
| **A device's house follows SmartThings.** | Moving a device to another location must be done in the SmartThings app; the next sync moves it in this app (and clears a room that only existed in the old house). |
| **OAuth redirect** | SmartThings rejects `http://localhost` redirects for OAuth-In apps, so the app uses an HTTPS page that echoes the code (e.g. `https://httpbin.org/get`). |
| **Refresh tokens rotate.** | Each refresh returns a new refresh token, so two processes must not refresh with the same stored token at the same time. |
| **Metering** | Plugs bridged from other clouds often expose on/off only, without power/voltage/current. |

## 4. Tuya cloud

| Finding | Detail |
|---|---|
| **Home = house.** | `GET /v1.0/users/{uid}/homes` returns homes with names, and `GET /v1.0/homes/{id}/rooms` their rooms with device ids. Each device carries its `home_id`. The app maps each home to one house. |
| **A device's house follows its Tuya home.** | Move it in Smart Life / the Tuya app, then sync. |
| **Linking accounts** | Only Tuya-platform app accounts can be linked to an iot.tuya.com project; OEM apps such as LEDVANCE's can't. |
| **LAN discovery** | Tuya devices broadcast on UDP 6666 / 6667 / 7000 (protocol 3.1–3.5). |

## 5. How the app models houses (and why)

- **House → Room → Device.** Every device and group belongs to exactly one house, and every room to one house. Room names are unique within a house, so two houses can each have a "Kitchen".
- **At least one house always exists.** The last house can be renamed but not deleted, and a house that still has devices can't be deleted (its devices would be orphaned).
- **Cloud links.** A house can be linked to a SmartThings location and/or a Tuya home. On sync, the matching house is found by link, then by name, or (for a single unlinked house) adopted; otherwise a new house is created. The house takes the cloud name unless the user renamed it.
- **Moving devices between houses:**
  - SmartThings / Tuya devices: **locked**. Move them in that app, then sync.
  - Matter bulbs: allowed only after the user ticks *"I already moved this bulb to that house in the LEDVANCE SMART+ app"*, because SMART+ can't be checked (§1).
  - Manually added devices: free.
  - A moved device leaves its room (rooms belong to a house) and any group of its old house.
- **New devices** (Matter pairing, manual add, tinytuya import) join the house selected in the sidebar.
- **Scope:** the house picker at the top left scopes the Devices, Rooms and Groups pages. Scenes and Schedules stay global and show each device's house.
- **Managing houses** happens in that picker ("Manage houses…"), like the SMART+ home picker. Adding a house keeps the current one selected.
- **Migration:** configs from earlier versions (one house name + a flat room list) become a single house that keeps the name, rooms and every device.

## 6. Protecting secrets on disk (Windows and Linux)

What an attacker who can read the user's files would find, and how it is protected:

| Data | Where | Protection |
|---|---|---|
| SmartThings client secret + refresh token, Tuya secret, device local keys | `config.json` | Windows: DPAPI. Linux: AES-256-GCM with a master key from the desktop keyring or `systemd-creds --user`. Never plain text: without a secure store they are not saved. |
| Matter storage key (32 random bytes) | `config.json` | same as above |
| Matter fabric (root key pair, operational key + IPK, certificates, paired nodes) | `matter/` | every value AES-256-GCM-encrypted by the bridge, bound to its storage name; plain values from older versions are encrypted on the first start |
| The whole data folder | `%APPDATA%\IoTSmartControl` / `~/.config/IoTSmartControl` | owner-only: Windows ACL (user + SYSTEM, no inherited entries); Linux `0700` folders, `0600` files, bridge runs with umask 077 |

Findings that shaped this:
- **DPAPI and the desktop keyring stop other accounts and offline disk theft, not malware running as the user.** Such malware can ask DPAPI or an unlocked keyring to decrypt. Encryption still removes the plain-text files that any process (or a backup, sync folder or screen share) could pick up, and owner-only permissions keep other local accounts out: on the test PC a `CodexSandboxUsers` group had inherited read access to the data folder.
- **On Linux the previous code wrote every secret in plain text**, readable by all local users (umask 022), because DPAPI only exists on Windows. Fixed by the cross-platform secret store above.
- **A keyring that D-Bus starts on demand answers lookups but may need an unlock prompt to store.** Without a display (SSH, server, WSL without WSLg) that prompt never appears. The app therefore only uses a store once it has actually produced the master key (with a 5 s limit when there is no display) and otherwise falls back to `systemd-creds`.
- **`systemd-creds --user` needs systemd 256+** (Fedora 41+; verified on Fedora 45 with systemd 262). Ubuntu 24.04 (systemd 255) doesn't have it.
- **The Matter storage key travels over stdin**, not the command line or environment, which other processes can read (`/proc/<pid>/cmdline`, `/proc/<pid>/environ`).
- **Only folders the app owns are locked down.** A config placed in an existing folder (e.g. via `LEDVANCE_CONFIG`) only gets the file itself restricted.

Verified with automated tests on Windows 11 and on Fedora 45 (WSL), including the real `systemd-creds --user`, a
real gnome-keyring in a private D-Bus session, and the real Matter bridge (encrypted fabric, reopened with its key,
rejected with a wrong one, legacy plain files encrypted, files `0600`).

## 7. Latency: where the time goes

Measured in November 2026 on three SMART MAT bulbs (A100 E27 ×2, P40 E14, fw 1.1.0) and two SmartThings-linked LEDVANCE Wi-Fi devices, with every change read back from the device.

| Step | Time |
|---|---|
| Matter command, session open (even after 3 min idle) | 30–200 ms until the bulb acknowledges |
| First Matter connection after the bridge starts | 0.4–4 s per bulb (CASE handshake on the bulb), ~2.7 s bridge start-up |
| SmartThings command | 350–950 ms until the API accepts it, plus the SmartThings → LEDVANCE cloud hop (not measurable from the PC) |
| SmartThings status after a command | still shows the old value for 0.5–3 s |

Findings that shaped the app:

- **LEDVANCE Matter bulbs mishandle `transitionTime`.** With `transitionTime = 0` the level is reached in ~0.2–0.35 s. With **any** other value, even 1 (0.1 s), or with none, the bulb ramps linearly over **~4.5–5 s**. This looked like a 2–3 s delay before anything happened. The app now always sends 0.
- **The bulbs expose no `OnOffTransitionTime` / `OnTransitionTime` / `OffTransitionTime`** attributes, but support the OnOff *Lighting* feature: `OffWithEffect` (DelayedAllOff, *no fade*) switches off at once, and `CurrentLevel` is kept while off, so *move to the current level with on/off, in 0 s* switches on at once at the same brightness.
- **Brightness read back mid-ramp looks wrong.** While a slow ramp runs, `CurrentLevel` reports intermediate values (a 30 % white read back as ~70 % after 0.6 s). With zero transitions, reads are exact.
- **Cloud status lags.** A refresh shortly after a SmartThings command returns the old value. Applying it flipped switches and sliders back, so a change looked ignored. The app now holds a value it just sent against older reports (5 s for cloud devices), and 4 s after the last change re-reads the status and resends power / brightness once if they didn't take.

## 8. Re-checking these findings

- Bulb attributes and labels: the Matter bridge has an `inspect` command (`{"cmd":"inspect","nodeId":"1"}` on its stdin) that dumps BasicInformation, every label list per endpoint, the transition-time attributes and the OnOff *Lighting* feature.
- Matter adverts on the LAN: an mDNS query for `_matter._tcp.local` (paired) and `_matterc._udp.local` (pairing window open).
- Bridge debug log: run `node bridge.mjs --storage <dir> --verbose debug` to see every Matter message, including the commissioning steps.
