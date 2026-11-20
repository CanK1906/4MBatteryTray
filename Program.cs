using System;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
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
        var provider = new ModernMobileMouseGattProvider();
        var batteryService = new BatteryDetectionService(provider);
        Application.Run(new TrayBatteryApp(batteryService, settings));
    }
}

public sealed class AppSettings
{
    public int LowBatteryThreshold { get; set; } = 15;
    public int PollIntervalSeconds { get; set; } = 300;
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

public sealed record BatteryReading(int Percentage, string DeviceName);

public sealed record ScanOutcome(BatteryReading? Reading, string SummaryText, DateTime CheckedAt);

public sealed class ModernMobileMouseGattProvider
{
    private const string CanonicalDeviceName = "Microsoft Modern Mobile Mouse";

    // Bluetooth SIG Battery Service (0x180F) and Battery Level Characteristic (0x2A19)
    private static readonly Guid BatteryServiceUuid = new("0000180f-0000-1000-8000-00805f9b34fb");
    private static readonly Guid BatteryLevelCharUuid = new("00002a19-0000-1000-8000-00805f9b34fb");

    public async Task<ScanOutcome> ReadAsync(CancellationToken cancellationToken = default)
    {
        DateTime checkedAt = DateTime.Now;
        bool anyMatchedDeviceFound = false;

        try
        {
            string selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
            var deviceInfoList = await DeviceInformation.FindAllAsync(selector)
                .AsTask(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(4), cancellationToken);

            foreach (var deviceInfo in deviceInfoList)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(deviceInfo.Id) || !IsModernMobileMouse(deviceInfo.Name))
                {
                    continue;
                }

                anyMatchedDeviceFound = true;

                BatteryReading? reading = await TryReadSingleMouseAsync(deviceInfo.Id, deviceInfo.Name, cancellationToken);
                if (reading is not null && reading.Percentage is >= 0 and <= 100)
                {
                    string summary = $"{checkedAt:HH:mm:ss} — {reading.Percentage}% - {reading.DeviceName}";
                    return new ScanOutcome(reading, summary, checkedAt);
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

        string statusText = anyMatchedDeviceFound
            ? $"{checkedAt:HH:mm:ss} — {CanonicalDeviceName} (Disconnected or sleeping)"
            : $"{checkedAt:HH:mm:ss} — {CanonicalDeviceName} (Not found)";

        return new ScanOutcome(null, statusText, checkedAt);
    }

    private static async Task<BatteryReading?> TryReadSingleMouseAsync(
        string deviceId,
        string? deviceName,
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

            if (!IsModernMobileMouse(bleDevice.Name ?? deviceName))
            {
                return null;
            }

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
                                return new BatteryReading(level, CanonicalDeviceName);
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

    private static bool IsModernMobileMouse(string? name)
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
    private readonly ModernMobileMouseGattProvider _provider;

    public BatteryDetectionService(ModernMobileMouseGattProvider provider)
    {
        _provider = provider;
    }

    public Task<ScanOutcome> ScanBatteryAsync(CancellationToken cancellationToken = default)
    {
        return _provider.ReadAsync(cancellationToken);
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
    private readonly ToolStripMenuItem _startupMenuItem;
    private readonly ToolStripMenuItem _statusMenuItem;
    private readonly ToolStripMenuItem[] _thresholdMenuItems;
    private readonly ToolStripMenuItem[] _intervalMenuItems;
    private ScanOutcome? _lastOutcome;
    private Icon? _currentIcon;
    private bool _isRefreshing;
    private bool _lowBatteryNotifiedForCurrentDrop;

    public TrayBatteryApp(BatteryDetectionService batteryService, AppSettings settings)
    {
        _batteryService = batteryService;
        _settings = settings;

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
        settingsMenu.DropDownItems.Add(thresholdSubMenu);
        settingsMenu.DropDownItems.Add(intervalSubMenu);

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add(_statusMenuItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Refresh Now", null, async (_, _) => await RefreshBatteryAsync());
        contextMenu.Items.Add(settingsMenu);
        contextMenu.Items.Add(_startupMenuItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Exit", null, (_, _) => ExitApp());

        _notifyIcon = new NotifyIcon
        {
            Text = "Microsoft Modern Mobile Mouse: Checking...",
            ContextMenuStrip = contextMenu,
            Visible = true
        };

        _pollTimer = new System.Windows.Forms.Timer { Interval = _settings.PollIntervalSeconds * 1000 };
        _pollTimer.Tick += async (_, _) => await RefreshBatteryAsync();
        _pollTimer.Start();

        _ = RefreshBatteryAsync();
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
            UpdateTrayIcon(_lastOutcome);
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

        _pollTimer.Stop();
        _pollTimer.Interval = seconds * 1000;
        _pollTimer.Start();
    }

    private async Task RefreshBatteryAsync()
    {
        if (_isRefreshing)
        {
            return;
        }

        _isRefreshing = true;
        try
        {
            ScanOutcome outcome = await _batteryService.ScanBatteryAsync();
            _lastOutcome = outcome;
            _statusMenuItem.Text = outcome.SummaryText;
            UpdateTrayIcon(outcome);
            EvaluateLowBatteryNotification(outcome.Reading);
        }
        finally
        {
            _isRefreshing = false;
        }
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

        _notifyIcon.Text = outcome.Reading is not null
            ? $"Microsoft Modern Mobile Mouse: {outcome.Reading.Percentage}%"
            : outcome.SummaryText;

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
        _pollTimer.Stop();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        if (_currentIcon is not null)
        {
            DestroyIcon(_currentIcon.Handle);
            _currentIcon.Dispose();
        }

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