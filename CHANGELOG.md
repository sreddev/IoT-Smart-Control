# Changelog

## 2.5.0 — 2026-11-16

### New
- **Backup & restore** (Settings → App settings → Backup): export to a `.iscbackup` file, choosing houses &
  devices, automations, preferences and keys. Keys are only exported with a password (AES-256-GCM,
  PBKDF2-HMAC-SHA256 600 000 iterations). Import shows what the file holds first, then **Merge** or
  **Replace**; the app restarts to apply it. Your settings are saved automatically before every import, and
  **Undo last import** brings them back. Edited or damaged files are rejected.
- **Windows installer** (`IoTSmartControl-2.5.0-Setup-x64.exe`): per-user install without admin rights,
  in-place upgrades with an optional **clean install**, and an uninstaller that asks whether to keep or delete
  your settings. Unattended switches `/CLEANINSTALL` and `/DELETEDATA`.
- **Node.js is bundled** with both packages, so Matter works without installing Node.js.
- **Sortable tables in Settings**: click any column header (again to reverse); IP addresses sort numerically,
  masked keys sort by *has a key*.
- **Masked local keys**: click a key cell to reveal or hide it.

### Faster and more reliable control
- **Matter bulbs react in ~0.3 s instead of 2–5 s.** LEDVANCE Matter bulbs turn any non-zero transition time
  into a ~5 s ramp; the app now changes brightness and colour instantly and switches on and off without a
  fade.
- **Changes are sent immediately.** The 250–300 ms wait before sending slider and colour changes is gone;
  during a drag only the newest value is sent, and the final position always arrives.
- **No more "click twice" with SmartThings.** A refresh that still carries the old value no longer flips a
  switch or slider back, and power / brightness changes the device didn't apply are re-sent once.

### Accessibility
- Navigation items, window buttons and menu items have names for screen readers, and selecting a navigation
  item with assistive technology now opens its page.

### Packaging
- `packaging/build-release.ps1` builds and checks the whole release (tests, installer, signed MSIX, SHA-256
  checksums). The repository now includes the MIT `LICENSE`.

## 2.0.0
- Houses → rooms → devices with a house picker, Rooms page, auto-refresh with per-device spinners, Matter over
  Wi-Fi without a hub, editable device names and rooms, protected secrets on Windows and Linux.
