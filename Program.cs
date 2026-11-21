using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace FourMBatteryTray;

internal static class Program
{
    internal const string AppName = "4MBatteryTray";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "Local\\4MBatteryTray_SingleInstance", out bool isNewInstance);
        if (!isNewInstance)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        StartupManager.EnsureRegistered(AppName);

        AppSettings settings = SettingsStore.Load();
        var provider = new BleBatteryGattProvider();
        var batteryService = new BatteryDetectionService(provider);
        Application.Run(new TrayBatteryApp(batteryService, settings));
    }
}

public sealed class AppSettings
{
    public int LowBatteryThreshold { get; set; } = 15;
    public int PollIntervalSeconds { get; set; } = 300;
    public string? SelectedDeviceId { get; set; }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private static string GetSettingsFilePath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, Program.AppName, "settings.json");
    }

    public static AppSettings Load()
    {
        try
        {
            string path = GetSettingsFilePath();
            if (!File.Exists(path))
            {
                var defaults = new AppSettings();
                Save(defaults);
                return defaults;
            }

            string json = File.ReadAllText(path);
            AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json);
            if (loaded is null)
            {
                return new AppSettings();
            }

            loaded.LowBatteryThreshold = loaded.LowBatteryThreshold is 10 or 15 or 20 or 25
                ? loaded.LowBatteryThreshold
                : 15;

            loaded.PollIntervalSeconds = loaded.PollIntervalSeconds is 60 or 300 or 900 or 1800
                ? loaded.PollIntervalSeconds
                : 300;

            if (string.IsNullOrWhiteSpace(loaded.SelectedDeviceId))
            {
                loaded.SelectedDeviceId = null;
            }

            return loaded;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            string path = GetSettingsFilePath();
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(settings, SerializerOptions);
            File.WriteAllText(path, json);
        }
        catch
        {
            // Ignore write errors so tray app continues running
        }
    }
}

public sealed record BleDeviceCandidate(string Id, string Name, bool IsPreferredDefault);

public sealed record BatteryReading(int Percentage, string DeviceId, string DeviceName);

public sealed record ScanOutcome(
    BatteryReading? Reading,
    string BaseSummaryText,
    DateTime CheckedAt,
    bool AnyMatchedDeviceFound,
    List<BleDeviceCandidate> DiscoveredDevices);

public sealed class BleBatteryGattProvider
{
    private const string PreferredDefaultName = "Microsoft Modern Mobile Mouse";

    // Bluetooth SIG Battery Service (0x180F) and Battery Level Characteristic (0x2A19)
    private static readonly Guid BatteryServiceUuid = new("0000180f-0000-1000-8000-00805f9b34fb");
    private static readonly Guid BatteryLevelCharUuid = new("00002a19-0000-1000-8000-00805f9b34fb");

    public async Task<IReadOnlyList<BleDeviceCandidate>> ListPairedBleDevicesAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<BleDeviceCandidate>();

        try
        {
            string selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
            var deviceInfoList = await DeviceInformation.FindAllAsync(selector)
                .AsTask(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(4), cancellationToken);

            foreach (var info in deviceInfoList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(info.Id))
                {
                    continue;
                }

                string cleanName = string.IsNullOrWhiteSpace(info.Name) ? "Unnamed BLE Device" : info.Name.Trim();
                bool isPreferred = IsPreferredDefault(cleanName);
                results.Add(new BleDeviceCandidate(info.Id, cleanName, isPreferred));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Return whatever was collected safely
        }

        return results
            .OrderByDescending(d => d.IsPreferredDefault)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<ScanOutcome> ReadAsync(string? selectedDeviceId, CancellationToken cancellationToken = default)
    {
        DateTime checkedAt = DateTime.Now;
        var discovered = new List<BleDeviceCandidate>();
        bool anyCandidateFound = false;

        try
        {
            string selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
            var deviceInfoList = await DeviceInformation.FindAllAsync(selector)
                .AsTask(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(4), cancellationToken);

            foreach (var deviceInfo in deviceInfoList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(deviceInfo.Id))
                {
                    continue;
                }

                string cleanName = string.IsNullOrWhiteSpace(deviceInfo.Name) ? "Unnamed BLE Device" : deviceInfo.Name.Trim();
                discovered.Add(new BleDeviceCandidate(deviceInfo.Id, cleanName, IsPreferredDefault(cleanName)));
            }

            List<BleDeviceCandidate> orderedTargets = BuildTargetOrder(discovered, selectedDeviceId);

            foreach (var target in orderedTargets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                anyCandidateFound = true;

                BatteryReading? reading = await TryReadSingleDeviceAsync(target.Id, target.Name, cancellationToken);
                if (reading is not null && reading.Percentage is >= 0 and <= 100)
                {
                    string summary = $"{checkedAt:HH:mm:ss} — {reading.Percentage}% - {reading.DeviceName}";
                    return new ScanOutcome(reading, summary, checkedAt, true, discovered);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Return safe status on timeouts or access errors
        }

        string targetLabel = GetTargetLabel(discovered, selectedDeviceId);
        string statusText = anyCandidateFound
            ? $"{checkedAt:HH:mm:ss} — {targetLabel} (Disconnected or sleeping)"
            : $"{checkedAt:HH:mm:ss} — {targetLabel} (Not found)";

        return new ScanOutcome(null, statusText, checkedAt, anyCandidateFound, discovered);
    }

    private static List<BleDeviceCandidate> BuildTargetOrder(
        List<BleDeviceCandidate> discovered,
        string? selectedDeviceId)
    {
        if (!string.IsNullOrWhiteSpace(selectedDeviceId))
        {
            var explicitMatch = discovered.Where(d => string.Equals(d.Id, selectedDeviceId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (explicitMatch.Count > 0)
            {
                return explicitMatch;
            }
        }

        // Auto mode: prioritize Modern Mobile Mouse first, then any other paired BLE device
        return discovered
            .OrderByDescending(d => d.IsPreferredDefault)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GetTargetLabel(List<BleDeviceCandidate> discovered, string? selectedDeviceId)
    {
        if (!string.IsNullOrWhiteSpace(selectedDeviceId))
        {
            var matched = discovered.FirstOrDefault(d => string.Equals(d.Id, selectedDeviceId, StringComparison.OrdinalIgnoreCase));
            if (matched is not null)
            {
                return matched.Name;
            }
        }

        return PreferredDefaultName;
    }

    private static async Task<BatteryReading?> TryReadSingleDeviceAsync(
        string deviceId,
        string deviceName,
        CancellationToken cancellationToken)
    {
        BluetoothLEDevice? bleDevice = null;

        try
        {
            using var perDeviceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            perDeviceCts.CancelAfter(TimeSpan.FromSeconds(3));

            bleDevice = await BluetoothLEDevice.FromIdAsync(deviceId)
                .AsTask(perDeviceCts.Token);

            if (bleDevice is null || bleDevice.ConnectionStatus != BluetoothConnectionStatus.Connected)
            {
                return null;
            }

            string resolvedName = string.IsNullOrWhiteSpace(bleDevice.Name) ? deviceName : bleDevice.Name.Trim();

            var servicesResult = await bleDevice.GetGattServicesForUuidAsync(
                    BatteryServiceUuid,
                    BluetoothCacheMode.Uncached)
                .AsTask(perDeviceCts.Token);

            if (servicesResult.Status != GattCommunicationStatus.Success ||
                servicesResult.Services.Count == 0)
            {
                return null;
            }

            foreach (var service in servicesResult.Services)
            {
                using (service)
                {
                    var charsResult = await service.GetCharacteristicsForUuidAsync(
                            BatteryLevelCharUuid,
                            BluetoothCacheMode.Uncached)
                        .AsTask(perDeviceCts.Token);

                    if (charsResult.Status != GattCommunicationStatus.Success ||
                        charsResult.Characteristics.Count == 0)
                    {
                        continue;
                    }

                    foreach (var characteristic in charsResult.Characteristics)
                    {
                        var readResult = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached)
                            .AsTask(perDeviceCts.Token);

                        if (readResult.Status == GattCommunicationStatus.Success &&
                            readResult.Value is not null &&
                            readResult.Value.Length >= 1)
                        {
                            var reader = DataReader.FromBuffer(readResult.Value);
                            byte level = reader.ReadByte();
                            if (level <= 100)
                            {
                                return new BatteryReading(level, deviceId, resolvedName);
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Skip sleeping or unresponsive connections
        }
        finally
        {
            bleDevice?.Dispose();
        }

        return null;
    }

    private static bool IsPreferredDefault(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return name.Trim().ToLowerInvariant().Contains("modern mobile mouse");
    }
}

public sealed class BatteryDetectionService
{
    private readonly BleBatteryGattProvider _provider;

    public BatteryDetectionService(BleBatteryGattProvider provider)
    {
        _provider = provider;
    }

    public Task<ScanOutcome> ScanBatteryAsync(string? selectedDeviceId, CancellationToken cancellationToken = default)
    {
        return _provider.ReadAsync(selectedDeviceId, cancellationToken);
    }

    public Task<IReadOnlyList<BleDeviceCandidate>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        return _provider.ListPairedBleDevicesAsync(cancellationToken);
    }
}

public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void EnsureRegistered(string appName)
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return;
        }

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return;
            }

            string desiredValue = $"\"{exePath}\"";
            string? currentValue = key.GetValue(appName) as string;

            if (!string.Equals(currentValue, desiredValue, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(appName, desiredValue);
            }
        }
        catch
        {
            // Do not crash if registry access is restricted
        }
    }

    public static bool IsRegistered(string appName)
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }

        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        string? currentValue = key?.GetValue(appName) as string;
        return string.Equals(currentValue, $"\"{exePath}\"", StringComparison.OrdinalIgnoreCase);
    }

    public static void Toggle(string appName)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null)
        {
            return;
        }

        if (IsRegistered(appName))
        {
            key.DeleteValue(appName, throwOnMissingValue: false);
        }
        else
        {
            EnsureRegistered(appName);
        }
    }
}

public sealed class TrayBatteryApp : ApplicationContext
{
    private const int InitialBackoffSeconds = 15;
    private const int MaxBackoffSeconds = 300;
    private const int WakeRecoveryDelayMilliseconds = 2000;

    private static readonly int[] ThresholdOptions = [10, 15, 20, 25];
    private static readonly (string Label, int Seconds)[] IntervalOptions =
    [
        ("1 min", 60),
        ("5 min", 300),
        ("15 min", 900),
        ("30 min", 1800)
    ];

    private readonly BatteryDetectionService _batteryService;
    private readonly AppSettings _settings;
    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly Control _uiMarshalControl;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly ToolStripMenuItem _startupMenuItem;
    private readonly ToolStripMenuItem _statusMenuItem;
    private readonly ToolStripMenuItem _deviceSubMenu;
    private readonly ToolStripMenuItem[] _thresholdMenuItems;
    private readonly ToolStripMenuItem[] _intervalMenuItems;

    private ScanOutcome? _lastOutcome;
    private Icon? _currentIcon;
    private bool _lowBatteryNotifiedForCurrentDrop;
    private bool _isExiting;
    private int _currentBackoffSeconds;
    private int? _activeRetryInSeconds;

    public TrayBatteryApp(BatteryDetectionService batteryService, AppSettings settings)
    {
        _batteryService = batteryService;
        _settings = settings;

        // Hidden window handle used to safely marshal SystemEvents callbacks onto UI thread
        _uiMarshalControl = new Control();
        _ = _uiMarshalControl.Handle;

        _statusMenuItem = new ToolStripMenuItem("Status: Initializing...")
        {
            Enabled = false
        };

        _startupMenuItem = new ToolStripMenuItem("Launch with Windows")
        {
            Checked = StartupManager.IsRegistered(Program.AppName)
        };
        _startupMenuItem.Click += (_, _) =>
        {
            StartupManager.Toggle(Program.AppName);
            _startupMenuItem.Checked = StartupManager.IsRegistered(Program.AppName);
        };

        _deviceSubMenu = new ToolStripMenuItem("BLE Device");
        _deviceSubMenu.DropDownOpening += async (_, _) => await PopulateDeviceMenuAsync();
        RebuildDeviceMenuItems([]);

        var thresholdSubMenu = new ToolStripMenuItem("Low Battery Threshold");
        _thresholdMenuItems = new ToolStripMenuItem[ThresholdOptions.Length];
        for (int i = 0; i < ThresholdOptions.Length; i++)
        {
            int threshold = ThresholdOptions[i];
            var item = new ToolStripMenuItem($"{threshold}%")
            {
                Checked = _settings.LowBatteryThreshold == threshold
            };
            item.Click += (_, _) => SetLowBatteryThreshold(threshold);
            _thresholdMenuItems[i] = item;
            thresholdSubMenu.DropDownItems.Add(item);
        }

        var intervalSubMenu = new ToolStripMenuItem("Refresh Interval");
        _intervalMenuItems = new ToolStripMenuItem[IntervalOptions.Length];
        for (int i = 0; i < IntervalOptions.Length; i++)
        {
            (string label, int seconds) = IntervalOptions[i];
            var item = new ToolStripMenuItem(label)
            {
                Checked = _settings.PollIntervalSeconds == seconds
            };
            item.Click += (_, _) => SetRefreshInterval(seconds);
            _intervalMenuItems[i] = item;
            intervalSubMenu.DropDownItems.Add(item);
        }

        var settingsMenu = new ToolStripMenuItem("Settings");
        settingsMenu.DropDownItems.Add(_deviceSubMenu);
        settingsMenu.DropDownItems.Add(thresholdSubMenu);
        settingsMenu.DropDownItems.Add(intervalSubMenu);

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add(_statusMenuItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Refresh Now", null, async (_, _) => await RefreshBatteryAsync(resetBackoff: true));
        contextMenu.Items.Add(settingsMenu);
        contextMenu.Items.Add(_startupMenuItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Exit", null, (_, _) => ExitApp());

        _notifyIcon = new NotifyIcon
        {
            Text = "BLE Battery: Checking...",
            ContextMenuStrip = contextMenu,
            Visible = true
        };

        _pollTimer = new System.Windows.Forms.Timer { Interval = _settings.PollIntervalSeconds * 1000 };
        _pollTimer.Tick += async (_, _) => await RefreshBatteryAsync(resetBackoff: false);

        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _pollTimer.Start();
        _ = RefreshBatteryAsync(resetBackoff: true);
    }

    private async Task PopulateDeviceMenuAsync()
    {
        if (_isExiting)
        {
            return;
        }

        try
        {
            IReadOnlyList<BleDeviceCandidate> devices = _lastOutcome?.DiscoveredDevices is { Count: > 0 }
                ? _lastOutcome.DiscoveredDevices
                : await _batteryService.ListDevicesAsync(_lifetimeCts.Token);

            if (_isExiting)
            {
                return;
            }

            RebuildDeviceMenuItems(devices);
        }
        catch (OperationCanceledException)
        {
            // Shutting down
        }
    }

    private void RebuildDeviceMenuItems(IReadOnlyList<BleDeviceCandidate> devices)
    {
        _deviceSubMenu.DropDownItems.Clear();

        var autoItem = new ToolStripMenuItem("Auto (Modern Mobile Mouse first)")
        {
            Checked = string.IsNullOrWhiteSpace(_settings.SelectedDeviceId)
        };
        autoItem.Click += async (_, _) => await SelectDeviceAsync(null);
        _deviceSubMenu.DropDownItems.Add(autoItem);

        if (devices.Count > 0)
        {
            _deviceSubMenu.DropDownItems.Add(new ToolStripSeparator());

            foreach (var device in devices)
            {
                bool isSelected = !string.IsNullOrWhiteSpace(_settings.SelectedDeviceId) &&
                                  string.Equals(_settings.SelectedDeviceId, device.Id, StringComparison.OrdinalIgnoreCase);

                var item = new ToolStripMenuItem(device.Name)
                {
                    Checked = isSelected
                };
                string capturedId = device.Id;
                item.Click += async (_, _) => await SelectDeviceAsync(capturedId);
                _deviceSubMenu.DropDownItems.Add(item);
            }
        }
    }

    private async Task SelectDeviceAsync(string? deviceId)
    {
        _settings.SelectedDeviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
        SettingsStore.Save(_settings);
        _lowBatteryNotifiedForCurrentDrop = false;

        if (_lastOutcome?.DiscoveredDevices is not null)
        {
            RebuildDeviceMenuItems(_lastOutcome.DiscoveredDevices);
        }

        await RefreshBatteryAsync(resetBackoff: true);
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (_isExiting || e.Mode != PowerModes.Resume)
        {
            return;
        }

        if (_uiMarshalControl.IsHandleCreated && !_uiMarshalControl.IsDisposed)
        {
            _uiMarshalControl.BeginInvoke(() => _ = HandleSystemResumeAsync());
        }
    }

    private async Task HandleSystemResumeAsync()
    {
        if (_isExiting)
        {
            return;
        }

        _currentBackoffSeconds = 0;
        _activeRetryInSeconds = null;
        _pollTimer.Stop();

        try
        {
            await Task.Delay(WakeRecoveryDelayMilliseconds, _lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_isExiting)
        {
            return;
        }

        await RefreshBatteryAsync(resetBackoff: true);
    }

    private void SetLowBatteryThreshold(int threshold)
    {
        _settings.LowBatteryThreshold = threshold;
        SettingsStore.Save(_settings);

        for (int i = 0; i < ThresholdOptions.Length; i++)
        {
            _thresholdMenuItems[i].Checked = ThresholdOptions[i] == threshold;
        }

        _lowBatteryNotifiedForCurrentDrop = false;

        if (_lastOutcome is not null)
        {
            UpdateTrayAppearance(_lastOutcome);
            EvaluateLowBatteryNotification(_lastOutcome.Reading);
        }
    }

    private void SetRefreshInterval(int seconds)
    {
        _settings.PollIntervalSeconds = seconds;
        SettingsStore.Save(_settings);

        for (int i = 0; i < IntervalOptions.Length; i++)
        {
            _intervalMenuItems[i].Checked = IntervalOptions[i].Seconds == seconds;
        }

        _currentBackoffSeconds = 0;
        _activeRetryInSeconds = null;
        RescheduleTimer(_settings.PollIntervalSeconds);

        if (_lastOutcome is not null)
        {
            UpdateTrayAppearance(_lastOutcome);
        }
    }

    private void RescheduleTimer(int intervalSeconds)
    {
        if (_isExiting)
        {
            return;
        }

        int clampedSeconds = Math.Max(5, intervalSeconds);
        _pollTimer.Stop();
        _pollTimer.Interval = clampedSeconds * 1000;
        _pollTimer.Start();
    }

    private async Task RefreshBatteryAsync(bool resetBackoff)
    {
        if (_isExiting)
        {
            return;
        }

        if (!await _refreshLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            if (_isExiting)
            {
                return;
            }

            if (resetBackoff)
            {
                _currentBackoffSeconds = 0;
                _activeRetryInSeconds = null;
            }

            _pollTimer.Stop();

            ScanOutcome outcome = await _batteryService.ScanBatteryAsync(_settings.SelectedDeviceId, _lifetimeCts.Token);
            if (_isExiting)
            {
                return;
            }

            _lastOutcome = outcome;

            if (outcome.Reading is not null)
            {
                _currentBackoffSeconds = 0;
                _activeRetryInSeconds = null;
                RescheduleTimer(_settings.PollIntervalSeconds);
            }
            else
            {
                _currentBackoffSeconds = _currentBackoffSeconds <= 0
                    ? InitialBackoffSeconds
                    : Math.Min(_currentBackoffSeconds * 2, MaxBackoffSeconds);

                int nextDelaySeconds = Math.Min(_currentBackoffSeconds, _settings.PollIntervalSeconds);
                _activeRetryInSeconds = nextDelaySeconds;
                RescheduleTimer(nextDelaySeconds);
            }

            UpdateTrayAppearance(outcome);
            EvaluateLowBatteryNotification(outcome.Reading);
        }
        catch (OperationCanceledException)
        {
            // Application is shutting down
        }
        finally
        {
            if (_refreshLock.CurrentCount == 0)
            {
                _refreshLock.Release();
            }
        }
    }

    private void UpdateTrayAppearance(ScanOutcome outcome)
    {
        string menuStatus = _activeRetryInSeconds.HasValue
            ? $"{outcome.BaseSummaryText} | Retry in {_activeRetryInSeconds.Value}s (backoff)"
            : outcome.BaseSummaryText;

        _statusMenuItem.Text = menuStatus;

        if (outcome.Reading is not null)
        {
            _notifyIcon.Text = TruncateNotifyText($"{outcome.Reading.DeviceName}: {outcome.Reading.Percentage}%");
        }
        else if (_activeRetryInSeconds.HasValue)
        {
            _notifyIcon.Text = TruncateNotifyText($"Retry in {_activeRetryInSeconds.Value}s — {outcome.BaseSummaryText}");
        }
        else
        {
            _notifyIcon.Text = TruncateNotifyText(outcome.BaseSummaryText);
        }

        UpdateTrayIcon(outcome);
    }

    private static string TruncateNotifyText(string text)
    {
        // Windows NotifyIcon.Text limit is 127 characters
        return text.Length <= 127 ? text : text[..124] + "...";
    }

    private void EvaluateLowBatteryNotification(BatteryReading? reading)
    {
        if (reading is null)
        {
            return;
        }

        int percent = reading.Percentage;
        if (percent <= _settings.LowBatteryThreshold)
        {
            if (!_lowBatteryNotifiedForCurrentDrop)
            {
                _notifyIcon.ShowBalloonTip(
                    6000,
                    $"{Program.AppName} — Low Battery",
                    $"{reading.DeviceName} battery is at {percent}%.",
                    ToolTipIcon.Warning);
                _lowBatteryNotifiedForCurrentDrop = true;
            }
        }
        else if (percent > _settings.LowBatteryThreshold + 5)
        {
            _lowBatteryNotifiedForCurrentDrop = false;
        }
    }

    private void UpdateTrayIcon(ScanOutcome outcome)
    {
        int? percent = outcome.Reading?.Percentage;
        string label = percent.HasValue ? $"{percent.Value}" : "--";

        int mediumThreshold = Math.Max(_settings.LowBatteryThreshold + 15, 35);
        Color bgColor = percent switch
        {
            null => Color.FromArgb(90, 90, 90),
            >= 0 when percent <= _settings.LowBatteryThreshold => Color.FromArgb(185, 38, 38),
            >= 0 when percent <= mediumThreshold => Color.FromArgb(205, 125, 15),
            _ => Color.FromArgb(28, 130, 75)
        };

        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;

        using var brush = new SolidBrush(bgColor);
        graphics.FillRoundedRectangle(brush, 1, 4, 30, 24, 6);

        float fontSize = label.Length >= 3 ? 11f : 13f;
        using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(Color.White);

        SizeF textSize = graphics.MeasureString(label, font);
        float x = (32 - textSize.Width) / 2f;
        float y = (32 - textSize.Height) / 2f;
        graphics.DrawString(label, font, textBrush, x, y);

        IntPtr hIcon = bitmap.GetHicon();
        Icon newIcon = Icon.FromHandle(hIcon);

        _notifyIcon.Icon = newIcon;
        if (_currentIcon is not null)
        {
            DestroyIcon(_currentIcon.Handle);
            _currentIcon.Dispose();
        }

        _currentIcon = newIcon;
    }

    private void ExitApp()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _pollTimer.Stop();
        _pollTimer.Dispose();
        _lifetimeCts.Cancel();

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        if (_currentIcon is not null)
        {
            DestroyIcon(_currentIcon.Handle);
            _currentIcon.Dispose();
        }

        _uiMarshalControl.Dispose();
        _lifetimeCts.Dispose();
        _refreshLock.Dispose();

        Application.Exit();
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics g, Brush brush, float x, float y, float width, float height, float radius)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        float diameter = radius * 2;
        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }
}