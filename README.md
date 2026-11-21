# 4MBatteryTray

A lightweight, open-source Windows system tray utility that monitors live Bluetooth Low Energy (BLE) battery percentages using the standard GATT Battery Service (`0x180F`) and Battery Level Characteristic (`0x2A19`), defaulting to the **Microsoft Modern Mobile Mouse** while supporting other paired BLE battery devices.

🌐 **Live Landing Page & Interactive Preview:** [https://cank1906.github.io/4MBatteryTray/](https://cank1906.github.io/4MBatteryTray/)  
⬇️ **Direct Download (v2.0 Windows x64):** [4MBatteryTray.exe](https://github.com/CanK1906/4MBatteryTray/releases/download/v2.0/4MBatteryTray.exe)

---

## Features

- **Dynamic System Tray Icon:** Displays the current battery percentage directly in a color-coded Windows tray icon with detailed status and active retry timing on hover and click.
- **Wake-up Auto Refresh:** Automatically refreshes the battery status shortly after Windows resumes from sleep (`PowerModes.Resume`) and resets backoff state.
- **Exponential Backoff & Retry Visibility:** Gradually increases retry intervals (`15s` → `30s` → `60s` → `120s` → `300s`, capped by your configured refresh interval) when the device is sleeping or disconnected, showing `Retry in Xs (backoff)` directly in the tray menu and tooltip.
- **Multi-Device BLE Selection:** Browse paired BLE devices under **Settings → BLE Device**, keeping **Auto (Modern Mobile Mouse first)** as default or saving a specific device ID in `%AppData%\4MBatteryTray\settings.json`.
- **Smart Low-Battery Alerts:** Configurable warning thresholds (`10%`, `15%`, `20%`, `25%`) with `+5%` recovery hysteresis to prevent repetitive notifications.
- **Configurable Refresh Intervals:** Supports `1 min`, `5 min`, `15 min`, and `30 min` polling intervals saved locally in `%AppData%\4MBatteryTray\settings.json`.
- **Optional Windows Startup:** Easily toggle auto-start via `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
- **Single-File Portable Executable:** Distributed as a self-contained single `.exe` built for `net10.0-windows10.0.22621.0` (`win-x64`) with no installer required.

---

## Requirements & How to Run

- **OS:** Windows 10 / Windows 11 (`x64`) with Bluetooth Low Energy (BLE) support
- **Device:** Microsoft Modern Mobile Mouse (or another paired BLE device supporting GATT Battery Service `0x180F`) paired via Windows Bluetooth settings

1. Download `4MBatteryTray.exe` from the [v2.0 Release](https://github.com/CanK1906/4MBatteryTray/releases/tag/v2.0).
2. Run `4MBatteryTray.exe` directly.
3. If Windows SmartScreen appears on first launch (since the binary is not signed with a commercial certificate), click **More info** → **Run anyway**.

---

## Release Integrity (SHA256)

Verify the `v2.0` executable in PowerShell using `Get-FileHash .\4MBatteryTray.exe -Algorithm SHA256`:

    AAA460D75FCF410A811B33FAEC61E35F789E2F8D66AB676483E5BEE3110FAFE9

---

## Disclaimer & Trademark Notice

`4MBatteryTray` is an independent, community-developed open-source utility and is **not** affiliated with, endorsed, sponsored, or supported by Microsoft Corporation. "Microsoft" and "Modern Mobile Mouse" are trademarks of Microsoft Corporation.

Released under the [MIT License](LICENSE).