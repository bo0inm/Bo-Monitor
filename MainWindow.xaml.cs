using System.Text;
using System.Text.Json;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.ComponentModel;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using System.Windows.Interop;
using Microsoft.Win32;
using Microsoft.VisualBasic;
using LibreHardwareMonitor.Hardware;

namespace BoMonitor;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private static readonly string BaseDirectory = AppContext.BaseDirectory;
    private const string AutoLaunchRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AutoLaunchValueName = "BoMonitor";
    private bool _isExitRequested;
    private Computer? _computer;
    private DispatcherTimer? _monitorTimer;
    private DispatcherTimer? _passthroughHoverTimer;
    private bool _isHoverTransparent;
    private string? _styleConfigPath;
    private string? _monitorConfigPath;
    private MonitorConfig? _monitorConfig;
    private int _monitorUpdateIntervalSeconds = 2;
    private readonly ObservableCollection<MonitorDisplayItem> _monitorItems = new();
    private bool _isMousePassthroughEnabled;

    public MainWindow()
    {
        InitializeComponent();
        if (MonitorItemsControl is not null)
        {
            MonitorItemsControl.ItemsSource = _monitorItems;
        }
        StateChanged += MainWindow_StateChanged;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
        Loaded += MainWindow_Loaded;
        SourceInitialized += MainWindow_SourceInitialized;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var exStyle = GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64();
        exStyle |= WS_EX_TOOLWINDOW;
        exStyle &= ~WS_EX_APPWINDOW;
        SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr(exStyle));
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyStyleFromConfig();
        LoadMonitorConfig();
        BuildTrayContextMenu();
        StartMonitoring();

        UpdateTopMenuItem();
        UpdateMousePassthroughMenu();
    }

    private void ApplyStyleFromConfig()
    {
        var configPath = GetStyleConfigPath();
        if (!File.Exists(configPath))
        {
            return;
        }

        StyleConfig? config;
        try
        {
            var json = File.ReadAllText(configPath, Encoding.UTF8);
            config = JsonSerializer.Deserialize<StyleConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return;
        }

        if (config is null)
        {
            return;
        }

        ApplyWindowPosition(config);

        if (!string.IsNullOrWhiteSpace(config.FontFamily))
        {
            FontFamily = new FontFamily(config.FontFamily);
        }

        if (config.FontSize > 0)
        {
            FontSize = config.FontSize;
        }

        var foreground = TryParseColor(config.ForegroundColor);
        if (foreground.HasValue)
        {
            Foreground = new SolidColorBrush(foreground.Value);
        }

        var background = TryParseColor(config.BackgroundColor);
        if (background.HasValue)
        {
            var opacity = config.Opacity;
            if (double.IsNaN(opacity) || opacity < 0 || opacity > 1)
            {
                opacity = 1;
            }
            var color = background.Value;
            color.A = (byte)Math.Round(opacity * 255, MidpointRounding.AwayFromZero);
            RootBorder.Background = new SolidColorBrush(color);
        }
    }

    private void ApplyWindowPosition(StyleConfig config)
    {
        if (double.IsNaN(config.X) || double.IsNaN(config.Y))
        {
            return;
        }

        Left = config.X;
        Top = config.Y;
    }

    private string GetStyleConfigPath()
    {
        if (!string.IsNullOrWhiteSpace(_styleConfigPath))
        {
            return _styleConfigPath;
        }

        _styleConfigPath = Path.Combine(BaseDirectory, "config", "style.json");
        return _styleConfigPath;
    }

    private static Color? TryParseColor(string? colorText)
    {
        if (string.IsNullOrWhiteSpace(colorText))
        {
            return null;
        }

        try
        {
            var parsed = ColorConverter.ConvertFromString(colorText);
            if (parsed is Color color)
            {
                return color;
            }
        }
        catch
        {
        }

        return null;
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isExitRequested)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        SaveWindowPosition();
        TrayIcon.Dispose();
        _monitorTimer?.Stop();
        _monitorTimer = null;
        _passthroughHoverTimer?.Stop();
        _passthroughHoverTimer = null;
        _computer?.Close();
        _computer = null;
    }

    private void SaveWindowPosition()
    {
        var configPath = GetStyleConfigPath();
        StyleConfig config;

        try
        {
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath, Encoding.UTF8);
                config = JsonSerializer.Deserialize<StyleConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? new StyleConfig();
            }
            else
            {
                var directory = System.IO.Path.GetDirectoryName(configPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                config = new StyleConfig();
            }
        }
        catch
        {
            return;
        }

        config.X = Left;
        config.Y = Top;

        try
        {
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(configPath, json, Encoding.UTF8);
        }
        catch
        {
        }
    }

    private void TrayIcon_TrayMouseDoubleClick(object sender, RoutedEventArgs e)
    {
        ShowFromTray();
    }

    private void TrayOpen_Click(object sender, RoutedEventArgs e)
    {
        ShowFromTray();
    }

    private void BuildTrayContextMenu()
    {
        if (MonitoringMenuItem is null)
        {
            return;
        }

        BuildMonitoringMenuItems();
        BuildItemsMenu();
        UpdateMonitorIntervalMenu();
        UpdateAutoLaunchMenuItem();
        UpdateTopMenuItem();
        UpdateMousePassthroughMenu();
    }

    private void BuildMonitoringMenuItems()
    {
        if (MonitoringMenuItem is null)
        {
            return;
        }

        MonitoringMenuItem.Items.Clear();

        foreach (var key in GetMonitoringKeys())
        {
            var item = new MenuItem
            {
                Header = key,
                IsCheckable = true,
                IsChecked = GetMonitoringFlag(key),
                Tag = key
            };
            item.Click += MonitoringMenuItem_Click;
            MonitoringMenuItem.Items.Add(item);
        }
    }

    private void MonitoringMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not string key)
        {
            return;
        }

        SetMonitoringFlag(key, item.IsChecked);
        SaveMonitorConfig();
        RestartMonitoring();
        BuildItemsMenu();
    }

    private void TrayExit_Click(object sender, RoutedEventArgs e)
    {
        ExitApplication();
    }

    private void TrayHide_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void TrayTop_Click(object sender, RoutedEventArgs e)
    {
        Topmost = TopMenuItem?.IsChecked == true;
        UpdateTopMenuItem();
    }

    private void TrayAutoLaunch_Click(object sender, RoutedEventArgs e)
    {
        var enable = AutoLaunchMenuItem?.IsChecked == true;
        SetAutoLaunchEnabled(enable);
        UpdateAutoLaunchMenuItem();
    }

    private void TrayMousePassthrough_Click(object sender, RoutedEventArgs e)
    {
        _isMousePassthroughEnabled = MousePassthroughMenuItem?.IsChecked == true;
        _monitorConfig ??= new MonitorConfig();
        _monitorConfig.MousePassthroughEnabled = _isMousePassthroughEnabled;
        SaveMonitorConfig();
        ApplyMousePassthrough(_isMousePassthroughEnabled);
        UpdateMousePassthroughMenu();
    }

    private void TrayUpdateInterval_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item)
        {
            return;
        }

        if (item.Tag is int seconds)
        {
            ApplyMonitorUpdateInterval(seconds);
            return;
        }

        if (item.Tag is string text && int.TryParse(text, out var parsedSeconds))
        {
            ApplyMonitorUpdateInterval(parsedSeconds);
        }
    }

    private void WindowDrag_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isMousePassthroughEnabled)
        {
            return;
        }

        if (e.ClickCount > 1)
        {
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
            e.Handled = true;
        }
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    internal void ExitApplication()
    {
        _isExitRequested = true;
        Close();
    }

    private void StartMonitoring()
    {
        var monitoring = _monitorConfig?.Monitoring ?? new MonitoringConfig();
        _computer = new Computer
        {
            IsCpuEnabled = monitoring.CPU,
            IsGpuEnabled = monitoring.GPU,
            IsMemoryEnabled = monitoring.Memory,
            IsMotherboardEnabled = monitoring.Motherboard,
            IsStorageEnabled = monitoring.Storage,
            IsNetworkEnabled = monitoring.Network,
            IsControllerEnabled = monitoring.Controller
        };
        _computer.Open();

        _monitorTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(_monitorUpdateIntervalSeconds)
        };
        _monitorTimer.Tick += MonitorTimer_Tick;
        _monitorTimer.Start();

        BuildItemsMenu();
        UpdateHardware(_computer.Hardware.ToArray());
        UpdateMonitorItems();
    }

    private void ApplyMonitorUpdateInterval(int seconds)
    {
        if (seconds <= 0)
        {
            return;
        }

        _monitorUpdateIntervalSeconds = seconds;
        if (_monitorConfig is not null)
        {
            _monitorConfig.UpdateIntervalSeconds = seconds;
            SaveMonitorConfig();
        }

        if (_monitorTimer is not null)
        {
            _monitorTimer.Interval = TimeSpan.FromSeconds(seconds);
        }

        UpdateMonitorIntervalMenu();
    }

    private void UpdateMonitorIntervalMenu()
    {
        if (UpdateMenuItem is null)
        {
            return;
        }

        foreach (var menuItem in UpdateMenuItem.Items.OfType<MenuItem>())
        {
            menuItem.IsCheckable = true;
            if (menuItem.Tag is int seconds)
            {
                menuItem.IsChecked = seconds == _monitorUpdateIntervalSeconds;
            }
            else if (menuItem.Tag is string text && int.TryParse(text, out var parsedSeconds))
            {
                menuItem.IsChecked = parsedSeconds == _monitorUpdateIntervalSeconds;
            }
            else
            {
                menuItem.IsChecked = false;
            }
        }
    }

    private void RestartMonitoring()
    {
        _monitorTimer?.Stop();
        _monitorTimer = null;
        _computer?.Close();
        _computer = null;

        StartMonitoring();
    }

    private void BuildItemsMenu()
    {
        if (ItemsMenuItem is null)
        {
            return;
        }

        ItemsMenuItem.Items.Clear();

        ItemsMenuItem.Items.Add(new MenuItem
        {
            Header = "Hide All",
            Tag = new DisplayTogglePayload(GetAllSensorIds(), false)
        });
        ((MenuItem)ItemsMenuItem.Items[ItemsMenuItem.Items.Count - 1]).Click += VisibilityMenuItem_Click;

        ItemsMenuItem.Items.Add(new MenuItem
        {
            Header = "Show All",
            Tag = new DisplayTogglePayload(GetAllSensorIds(), true)
        });
        ((MenuItem)ItemsMenuItem.Items[ItemsMenuItem.Items.Count - 1]).Click += VisibilityMenuItem_Click;

        ItemsMenuItem.Items.Add(new Separator());

        if (_computer is null)
        {
            ItemsMenuItem.Items.Add(new MenuItem
            {
                Header = "(No data)",
                IsEnabled = false
            });
            return;
        }

        var added = false;
        foreach (var hardware in _computer.Hardware)
        {
            if (!IsHardwareTypeEnabled(hardware.HardwareType))
            {
                continue;
            }

            ItemsMenuItem.Items.Add(CreateHardwareMenuItem(hardware));
            added = true;
        }

        if (!added)
        {
            ItemsMenuItem.Items.Add(new MenuItem
            {
                Header = "(No monitored items)",
                IsEnabled = false
            });
        }
    }

    private List<string> GetAllSensorIds()
    {
        var ids = new List<string>();
        if (_computer is null)
        {
            return ids;
        }

        foreach (var hardware in _computer.Hardware)
        {
            AppendSensorIds(ids, hardware);
        }

        return ids;
    }

    private void UpdateTopMenuItem()
    {
        if (TopMenuItem is null)
        {
            return;
        }

        TopMenuItem.IsChecked = Topmost;
    }

    private void UpdateMousePassthroughMenu()
    {
        if (MousePassthroughMenuItem is null)
        {
            return;
        }

        MousePassthroughMenuItem.IsChecked = _isMousePassthroughEnabled;
    }

    private void UpdateAutoLaunchMenuItem()
    {
        if (AutoLaunchMenuItem is null)
        {
            return;
        }

        AutoLaunchMenuItem.IsChecked = IsAutoLaunchEnabled();
    }

    private static bool IsAutoLaunchEnabled()
    {
        var currentPath = GetAutoLaunchExecutablePath();
        if (string.IsNullOrWhiteSpace(currentPath))
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoLaunchRegistryPath, false);
            var value = key?.GetValue(AutoLaunchValueName) as string;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var trimmed = value.Trim();
            if (trimmed.StartsWith("\"", StringComparison.Ordinal))
            {
                var endQuote = trimmed.IndexOf('"', 1);
                if (endQuote > 1)
                {
                    var path = trimmed.Substring(1, endQuote - 1);
                    return PathEquals(path, currentPath);
                }
            }

            var firstToken = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(firstToken))
            {
                return false;
            }

            return PathEquals(firstToken, currentPath);
        }
        catch
        {
            return false;
        }
    }

    private static void SetAutoLaunchEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoLaunchRegistryPath, true)
                ?? Registry.CurrentUser.CreateSubKey(AutoLaunchRegistryPath);
            if (key is null)
            {
                return;
            }

            if (enabled)
            {
                var path = GetAutoLaunchExecutablePath();
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                key.SetValue(AutoLaunchValueName, $"\"{path}\"");
            }
            else
            {
                key.DeleteValue(AutoLaunchValueName, false);
            }
        }
        catch
        {
        }
    }

    private static string? GetAutoLaunchExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Process.GetCurrentProcess().MainModule?.FileName;
        }

        return path;
    }

    private static bool PathEquals(string left, string right)
    {
        try
        {
            var leftPath = Path.GetFullPath(left);
            var rightPath = Path.GetFullPath(right);
            return string.Equals(leftPath, rightPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void ApplyMousePassthrough(bool enabled)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var exStyle = GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64();
        if (enabled)
        {
            exStyle |= WS_EX_TRANSPARENT;
            exStyle |= WS_EX_LAYERED;
        }
        else
        {
            exStyle &= ~WS_EX_TRANSPARENT;
        }

        SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr(exStyle));
        RootBorder.IsHitTestVisible = !enabled;
        UpdatePassthroughHoverTracking(enabled);
    }

    private void UpdatePassthroughHoverTracking(bool enabled)
    {
        if (!enabled)
        {
            StopPassthroughHoverTimer();
            SetHoverTransparency(false);
            return;
        }

        if (_passthroughHoverTimer is null)
        {
            _passthroughHoverTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _passthroughHoverTimer.Tick += PassthroughHoverTimer_Tick;
        }

        _passthroughHoverTimer.Start();
        UpdateHoverTransparencyFromCursor();
    }

    private void StopPassthroughHoverTimer()
    {
        if (_passthroughHoverTimer is null)
        {
            return;
        }

        _passthroughHoverTimer.Stop();
    }

    private void PassthroughHoverTimer_Tick(object? sender, EventArgs e)
    {
        UpdateHoverTransparencyFromCursor();
    }

    private void UpdateHoverTransparencyFromCursor()
    {
        if (!_isMousePassthroughEnabled || !IsVisible)
        {
            SetHoverTransparency(false);
            return;
        }

        if (!TryIsCursorOverWindow(out var isOver))
        {
            SetHoverTransparency(false);
            return;
        }

        SetHoverTransparency(isOver);
    }

    private void SetHoverTransparency(bool isOver)
    {
        if (RootBorder is null || _isHoverTransparent == isOver)
        {
            return;
        }

        _isHoverTransparent = isOver;
        var targetOpacity = isOver ? 0.0 : 1.0;
        var animation = new DoubleAnimation
        {
            To = targetOpacity,
            Duration = TimeSpan.FromMilliseconds(100),
            FillBehavior = FillBehavior.HoldEnd
        };

        RootBorder.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    private bool TryIsCursorOverWindow(out bool isOver)
    {
        isOver = false;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        if (!GetCursorPos(out var point))
        {
            return false;
        }

        if (!GetWindowRect(handle, out var rect))
        {
            return false;
        }

        isOver = point.X >= rect.Left && point.X <= rect.Right
            && point.Y >= rect.Top && point.Y <= rect.Bottom;
        return true;
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x20L;
    private const long WS_EX_LAYERED = 0x80000L;
    private const long WS_EX_TOOLWINDOW = 0x80L;
    private const long WS_EX_APPWINDOW = 0x40000L;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private MenuItem CreateHardwareMenuItem(IHardware hardware)
    {
        var item = new MenuItem
        {
            Header = $"{hardware.HardwareType}: {hardware.Name}"
        };

        var hardwareSensorIds = GetSensorIdsForHardware(hardware);
        AddVisibilityMenuItems(item, hardwareSensorIds);

        var sensorGroups = hardware.Sensors
            .Where(sensor => sensor.Value.HasValue)
            .GroupBy(sensor => sensor.SensorType)
            .OrderBy(group => group.Key.ToString());

        foreach (var group in sensorGroups)
        {
            var groupItem = new MenuItem
            {
                Header = group.Key.ToString()
            };

            var groupSensorIds = group.Select(sensor => sensor.Identifier.ToString()).ToList();
            AddVisibilityMenuItems(groupItem, groupSensorIds);

            foreach (var sensor in group.OrderBy(s => s.Name))
            {
                groupItem.Items.Add(CreateSensorMenuItem(sensor));
            }

            item.Items.Add(groupItem);
        }

        foreach (var subHardware in hardware.SubHardware)
        {
            item.Items.Add(CreateHardwareMenuItem(subHardware));
        }

        if (item.Items.Count == 0)
        {
            item.Items.Add(new MenuItem
            {
                Header = "(No sensors)",
                IsEnabled = false
            });
        }

        return item;
    }

    private MenuItem CreateSensorMenuItem(ISensor sensor)
    {
        var key = sensor.Identifier.ToString();
        var item = new MenuItem
        {
            Header = sensor.Name,
            IsCheckable = true,
            IsChecked = IsDisplayEnabled(key),
            Tag = key
        };
        item.Click += SensorMenuItem_Click;
        return item;
    }

    private void SensorMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not string key)
        {
            return;
        }

        SetDisplayEnabled(key, item.IsChecked);
        SaveMonitorConfig();
        UpdateMonitorItems();
    }

    private void MonitorItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2)
        {
            return;
        }

        if (sender is not FrameworkElement element || element.DataContext is not MonitorDisplayItem item)
        {
            return;
        }

        var currentTitle = item.Name;
        var input = Interaction.InputBox("Rename item", "Rename", currentTitle);
        if (string.IsNullOrWhiteSpace(input) || input == currentTitle)
        {
            return;
        }

        SetRenameTitle(item.SourceKey, input.Trim());
        SaveMonitorConfig();
        UpdateMonitorItems();
        BuildItemsMenu();
        e.Handled = true;
    }

    private void VisibilityMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not DisplayTogglePayload payload)
        {
            return;
        }

        SetDisplayForSensors(payload.SensorIds, payload.Enabled);
        SaveMonitorConfig();
        UpdateMonitorItems();
        BuildItemsMenu();
    }

    private bool IsHardwareTypeEnabled(HardwareType hardwareType)
    {
        var monitoring = _monitorConfig?.Monitoring ?? new MonitoringConfig();
        return hardwareType switch
        {
            HardwareType.Cpu => monitoring.CPU,
            HardwareType.GpuAmd => monitoring.GPU,
            HardwareType.GpuIntel => monitoring.GPU,
            HardwareType.GpuNvidia => monitoring.GPU,
            HardwareType.Memory => monitoring.Memory,
            HardwareType.Motherboard => monitoring.Motherboard,
            HardwareType.Storage => monitoring.Storage,
            HardwareType.Network => monitoring.Network,
            _ => false
        };
    }

    private void MonitorTimer_Tick(object? sender, EventArgs e)
    {
        if (_computer is null)
        {
            return;
        }

        UpdateHardware(_computer.Hardware.ToArray());
        UpdateMonitorItems();
#if DEBUG
        Debug.WriteLine($"Monitor updated at {DateTime.Now}");
#endif
    }

    private static void UpdateHardware(IHardware[] hardwareItems)
    {
        foreach (var hardware in hardwareItems)
        {
            hardware.Update();
            if (hardware.SubHardware.Length > 0)
            {
                UpdateHardware(hardware.SubHardware);
            }
        }
    }

    private void UpdateMonitorItems()
    {
        if (_computer is null)
        {
            _monitorItems.Clear();
            return;
        }

        var items = new List<MonitorDisplayItem>();
        foreach (var hardware in _computer.Hardware)
        {
            AppendDisplayItems(items, hardware);
        }

        _monitorItems.Clear();
        foreach (var item in items)
        {
            _monitorItems.Add(item);
        }
    }

    private void AppendDisplayItems(List<MonitorDisplayItem> items, IHardware hardware)
    {
        if (!IsHardwareTypeEnabled(hardware.HardwareType))
        {
            return;
        }

        foreach (var sensor in hardware.Sensors)
        {
            if (!sensor.Value.HasValue)
            {
                continue;
            }

            var key = sensor.Identifier.ToString();
            if (!IsDisplayEnabled(key))
            {
                continue;
            }

            var unit = GetSensorUnit(sensor.SensorType);
            var defaultName = $"{hardware.Name} {sensor.Name}";
            items.Add(new MonitorDisplayItem
            {
                SourceKey = key,
                Name = GetDisplayTitle(key, defaultName),
                Value = $"{sensor.Value:0.##}{unit}"
            });
        }

        foreach (var subHardware in hardware.SubHardware)
        {
            AppendDisplayItems(items, subHardware);
        }
    }

    private static string GetSensorUnit(SensorType sensorType)
    {
        return sensorType switch
        {
            SensorType.Temperature => " °C",
            SensorType.Load => " %",
            SensorType.Level => " %",
            SensorType.Power => " W",
            SensorType.Voltage => " V",
            SensorType.Clock => " MHz",
            SensorType.Fan => " RPM",
            SensorType.Data => " GB",
            SensorType.SmallData => " MB",
            SensorType.Throughput => " B/s",
            _ => string.Empty
        };
    }

    private sealed class StyleConfig
    {
        public string? FontFamily { get; set; }
        public double FontSize { get; set; }
        public string? ForegroundColor { get; set; }
        public string? BackgroundColor { get; set; }
        public double Opacity { get; set; }
        public double X { get; set; } = double.NaN;
        public double Y { get; set; } = double.NaN;
    }

    private void LoadMonitorConfig()
    {
        var configPath = GetMonitorConfigPath();
        if (!File.Exists(configPath))
        {
            _monitorConfig = new MonitorConfig();
            return;
        }

        try
        {
            var json = File.ReadAllText(configPath, Encoding.UTF8);
            _monitorConfig = JsonSerializer.Deserialize<MonitorConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new MonitorConfig();
        }
        catch
        {
            _monitorConfig = new MonitorConfig();
        }

        if (_monitorConfig.UpdateIntervalSeconds > 0)
        {
            _monitorUpdateIntervalSeconds = _monitorConfig.UpdateIntervalSeconds;
        }

        _isMousePassthroughEnabled = _monitorConfig.MousePassthroughEnabled;
        ApplyMousePassthrough(_isMousePassthroughEnabled);
    }

    private void SaveMonitorConfig()
    {
        var configPath = GetMonitorConfigPath();
        var config = _monitorConfig ?? new MonitorConfig();

        try
        {
            var directory = System.IO.Path.GetDirectoryName(configPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(configPath, json, Encoding.UTF8);
        }
        catch
        {
        }
    }

    private string GetMonitorConfigPath()
    {
        if (!string.IsNullOrWhiteSpace(_monitorConfigPath))
        {
            return _monitorConfigPath;
        }

        _monitorConfigPath = Path.Combine(BaseDirectory, "config", "monitor.json");
        return _monitorConfigPath;
    }

    private static IEnumerable<string> GetMonitoringKeys()
    {
        return new[]
        {
            "CPU",
            "GPU",
            "Memory",
            "Motherboard",
            "Controller",
            "Network",
            "Storage"
        };
    }

    private bool GetMonitoringFlag(string key)
    {
        var monitoring = _monitorConfig?.Monitoring ?? new MonitoringConfig();
        return key switch
        {
            "CPU" => monitoring.CPU,
            "GPU" => monitoring.GPU,
            "Memory" => monitoring.Memory,
            "Motherboard" => monitoring.Motherboard,
            "Controller" => monitoring.Controller,
            "Network" => monitoring.Network,
            "Storage" => monitoring.Storage,
            _ => false
        };
    }

    private void SetMonitoringFlag(string key, bool value)
    {
        _monitorConfig ??= new MonitorConfig();
        var monitoring = _monitorConfig.Monitoring;

        switch (key)
        {
            case "CPU":
                monitoring.CPU = value;
                break;
            case "GPU":
                monitoring.GPU = value;
                break;
            case "Memory":
                monitoring.Memory = value;
                break;
            case "Motherboard":
                monitoring.Motherboard = value;
                break;
            case "Controller":
                monitoring.Controller = value;
                break;
            case "Network":
                monitoring.Network = value;
                break;
            case "Storage":
                monitoring.Storage = value;
                break;
        }
    }

    private bool IsDisplayEnabled(string key)
    {
        var display = _monitorConfig?.Display;
        if (display is null)
        {
            return true;
        }

        return !display.TryGetValue(key, out var enabled) || enabled;
    }

    private void SetDisplayEnabled(string key, bool enabled)
    {
        _monitorConfig ??= new MonitorConfig();
        _monitorConfig.Display ??= new Dictionary<string, bool>();
        _monitorConfig.Display[key] = enabled;
    }

    private string GetDisplayTitle(string sourceKey, string fallback)
    {
        var rename = _monitorConfig?.Rename;
        if (rename is null)
        {
            return fallback;
        }

        var match = rename.FirstOrDefault(item => string.Equals(item.Source, sourceKey, StringComparison.OrdinalIgnoreCase));
        if (match is null || string.IsNullOrWhiteSpace(match.Title))
        {
            return fallback;
        }

        return match.Title;
    }

    private void SetRenameTitle(string sourceKey, string title)
    {
        _monitorConfig ??= new MonitorConfig();
        _monitorConfig.Rename ??= new List<RenameItem>();

        var existing = _monitorConfig.Rename.FirstOrDefault(item => string.Equals(item.Source, sourceKey, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            _monitorConfig.Rename.Add(new RenameItem
            {
                Source = sourceKey,
                Title = title
            });
        }
        else
        {
            existing.Title = title;
        }
    }

    private void SetDisplayForSensors(IEnumerable<string> sensorIds, bool enabled)
    {
        _monitorConfig ??= new MonitorConfig();
        _monitorConfig.Display ??= new Dictionary<string, bool>();

        foreach (var id in sensorIds)
        {
            _monitorConfig.Display[id] = enabled;
        }
    }

    private static List<string> GetSensorIdsForHardware(IHardware hardware)
    {
        var ids = new List<string>();
        AppendSensorIds(ids, hardware);
        return ids;
    }

    private static void AppendSensorIds(List<string> ids, IHardware hardware)
    {
        foreach (var sensor in hardware.Sensors)
        {
            ids.Add(sensor.Identifier.ToString());
        }

        foreach (var subHardware in hardware.SubHardware)
        {
            AppendSensorIds(ids, subHardware);
        }
    }

    private void AddVisibilityMenuItems(MenuItem parent, IReadOnlyCollection<string> sensorIds)
    {
        if (sensorIds.Count == 0)
        {
            return;
        }

        parent.Items.Add(new MenuItem
        {
            Header = "Hide All",
            Tag = new DisplayTogglePayload(sensorIds, false)
        });
        ((MenuItem)parent.Items[parent.Items.Count - 1]).Click += VisibilityMenuItem_Click;

        parent.Items.Add(new MenuItem
        {
            Header = "Show All",
            Tag = new DisplayTogglePayload(sensorIds, true)
        });
        ((MenuItem)parent.Items[parent.Items.Count - 1]).Click += VisibilityMenuItem_Click;

        parent.Items.Add(new Separator());
    }

    private sealed class MonitorConfig
    {
        public MonitoringConfig Monitoring { get; set; } = new MonitoringConfig();
        public Dictionary<string, bool>? Display { get; set; }
        public List<RenameItem>? Rename { get; set; }
        public int UpdateIntervalSeconds { get; set; } = 2;
        public bool MousePassthroughEnabled { get; set; }
    }

    private sealed class MonitoringConfig
    {
        public bool CPU { get; set; } = true;
        public bool GPU { get; set; } = true;
        public bool Memory { get; set; } = true;
        public bool Motherboard { get; set; }
        public bool Controller { get; set; }
        public bool Network { get; set; }
        public bool Storage { get; set; }
    }

    private sealed class MonitorDisplayItem
    {
        public string SourceKey { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    private sealed class RenameItem
    {
        public string Source { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
    }

    private sealed class DisplayTogglePayload
    {
        public DisplayTogglePayload(IEnumerable<string> sensorIds, bool enabled)
        {
            SensorIds = sensorIds.ToList();
            Enabled = enabled;
        }

        public List<string> SensorIds { get; }
        public bool Enabled { get; }
    }
}