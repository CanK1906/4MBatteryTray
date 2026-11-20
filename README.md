# 4MBatteryTray

A lightweight, open-source Windows system tray utility that monitors the live battery percentage of the **Microsoft Modern Mobile Mouse** over Bluetooth Low Energy (BLE) using the standard GATT Battery Service (`0x180F`) and Battery Level Characteristic (`0x2A19`).

🌐 **Live Landing Page & Interactive Preview:** [https://cank1906.github.io/4MBatteryTray/](https://cank1906.github.io/4MBatteryTray/)  
⬇️ **Direct Download (v1.0 Windows x64):** [4MBatteryTray.exe](https://github.com/CanK1906/4MBatteryTray/releases/download/v1.0/4MBatteryTray.exe)

---

## Features

- **Dynamic System Tray Icon:** Displays the current battery percentage directly in a color-coded Windows tray icon with detailed status on hover and click.
- **Smart Low-Battery Alerts:** Configurable warning thresholds (`10%`, `15%`, `20%`, `25%`) with `+5%` recovery hysteresis to prevent repetitive notifications.
- **Configurable Refresh Intervals:** Supports `1 min`, `5 min`, `15 min`, and `30 min` polling intervals saved locally in `%AppData%\4MBatteryTray\settings.json`.
- **Optional Windows Startup:** Easily toggle auto-start via `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
- **Single-File Portable Executable:** Distributed as a self-contained single `.exe` built for `net10.0-windows10.0.22621.0` (`win-x64`) with no installer required.

---

## Requirements & How to Run

- **OS:** Windows 10 / Windows 11 (`x64`) with Bluetooth Low Energy (BLE) support
- **Device:** Microsoft Modern Mobile Mouse paired via Windows Bluetooth settings

1. Download `4MBatteryTray.exe` from the [v1.0 Release](https://github.com/CanK1906/4MBatteryTray/releases/tag/v1.0).
2. Run `4MBatteryTray.exe` directly.
3. If Windows SmartScreen appears on first launch (since the binary is not signed with a commercial certificate), click **More info** → **Run anyway**.

---

## Release Integrity (SHA256)

Verify the `v1.0` executable in PowerShell using `Get-FileHash .\4MBatteryTray.exe -Algorithm SHA256`:

```text
A49177704FEB239203A3421EA1AF62388D5EB4825E6BBFEF4169B29C37789233
```

---

## Disclaimer & Trademark Notice

`4MBatteryTray` is an independent, community-developed open-source utility and is **not** affiliated with, endorsed, sponsored, or supported by Microsoft Corporation. "Microsoft" and "Modern Mobile Mouse" are trademarks of Microsoft Corporation.

Released under the [MIT License](LICENSE).
