# 4MBatteryTray

A lightweight, open-source Windows system tray utility that monitors the battery percentage of the **Microsoft Modern Mobile Mouse** over Bluetooth Low Energy (BLE) using the standard GATT Battery Service (`0x180F`) and Battery Level Characteristic (`0x2A19`).

## Features

- **System Tray Battery Indicator:** Displays the current battery percentage inside a dynamically colored tray icon and shows detailed status on hover or in the context menu.
- **Configurable Low Battery Alerts:** Sends a warning notification when the battery level drops to or below the configured threshold (default: `15%`), resetting only after recovering above `threshold + 5%` to prevent repeated notifications from minor fluctuations.
- **Tray Settings & Persistence:** Configure **Low Battery Threshold** (`10%`, `15%`, `20%`, `25%`) and **Refresh Interval** (`1 min`, `5 min`, `15 min`, `30 min`) directly from the **Settings** submenu, persisted locally in `%AppData%\4MBatteryTray\settings.json`.
- **Windows Startup Integration:** Toggle **Launch with Windows** to manage the current user's startup entry (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`), updating the executable path automatically when run from a new location.
- **Single-File Distribution:** Built with .NET (`net10.0-windows10.0.22621.0`, `win-x64`) as a self-contained single-file executable with single-instance mutex protection and a custom `.ico` icon.

## Usage & Build

### Download Release
1. Download `4MBatteryTray.exe` from the **Releases** section (`v1.0`).
2. Move `4MBatteryTray.exe` to a permanent folder of your choice and run it.
3. Right-click the tray icon to refresh manually, adjust **Settings**, toggle **Launch with Windows**, or exit.

### Build from Source
```powershell
dotnet build -c Release -r win-x64
dotnet publish -c Release -r win-x64
```

## Notes & Limitations

- **Sleeping or Disconnected Devices:** When the mouse is asleep or disconnected over Bluetooth LE, GATT reads may return no fresh battery percentage until the device wakes and reconnects.
- **Polling Intervals:** Shorter intervals (`1 min`) check more frequently, while longer intervals (`15 min` or `30 min`) reduce background polling at the expense of slower updates.
- **Unverified Binary Prompt:** Because prebuilt release binaries are distributed without a commercial code-signing certificate, Windows may display a SmartScreen prompt on first launch.

## Disclaimer & Trademark Notice

This project (`4MBatteryTray`) is an independent, community-developed open-source utility and is **not** an official product of Microsoft Corporation. It is not affiliated with, endorsed, sponsored, authorized, or supported by Microsoft Corporation in any way.

"Microsoft" and "Modern Mobile Mouse" are trademarks or registered trademarks of Microsoft Corporation in the United States and/or other countries. References to "Microsoft Modern Mobile Mouse" in this repository and software are made strictly for descriptive and hardware-compatibility identification purposes.

This software is provided **"AS IS"**, without warranty of any kind, express or implied, including but not limited to the warranties of merchantability, fitness for a particular purpose, and non-infringement. See the [LICENSE](LICENSE) file for full license terms.

## License

Distributed under the [MIT License](LICENSE).
