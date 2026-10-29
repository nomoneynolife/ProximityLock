using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
// WinForms 和 WPF 同名类冲突, 显式指定用 WPF 的
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;

namespace ProximityLock;

public partial class MainWindow : Window
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "ProximityLock";

    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ProximityLock", "config.json");

    private IProximityWatcher? _watcher;
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _logClearTimer;

    /// <summary>连接模式下拉框里的一项 (已配对的 BLE 设备)</summary>
    public sealed record PairedDevice(string Display, ulong Address, string Name, string DeviceId);

    // 托盘
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _realExit;

    public MainWindow()
    {
        InitializeComponent();

        // 实时信号面板 1 秒刷新一次
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uiTimer.Tick += (_, _) => RefreshSignalPanel();

        // 日志区内边距: 控制层 Padding 管不到 RichTextBox 里的文档内容,
        // 必须直接设 FlowDocument 的 PagePadding。
        // 两个坑: ①不能用 FlowDocument(new Paragraph(...)) 构造 — 空段落占一整行;
        // ②RichTextBox 挂载文档时会重置 PagePadding — 必须在赋值之后再设
        LogBox.Document = new System.Windows.Documents.FlowDocument();
        LogBox.Document.PagePadding = new Thickness(0);

        // 日志自动清屏: 到点清空并提示一句
        _logClearTimer = new DispatcherTimer();
        _logClearTimer.Tick += (_, _) =>
        {
            LogBox.Document.Blocks.Clear();
            AppendLog("日志已自动清屏 (可在上方设置调整或关闭)。", LogLevel.Info);
        };

        InitTrayIcon();
        InitAutoStart();
        InitAutoUnlock();
        InitVersionFooter();
        InitLogClear();
        _ = LoadPairedDevicesAsync();

        Loaded += (_, _) =>
        {
            if (App.StartMinimized)
            {
                WindowState = WindowState.Minimized;
                Hide();
            }
            // 开机自启 (--listen): 自动开始监听; 手动打开 exe 不自动监听, 方便调试
            if (App.AutoListen)
            {
                AppendLog("开机自启模式, 自动开始监听。", LogLevel.Info);
                StartBtn_Click(this, new RoutedEventArgs());
            }
        };

        AppendLog("就绪。先在 Windows 蓝牙设置里配对手机, 再在上面选中并点「开始监听」。", LogLevel.Info);
        AppendLog("连接模式: 连接在线 = 锁屏界面自动解锁; 断开 = 锁屏。", LogLevel.Info);
        AppendLog("关闭或最小化窗口会缩到托盘, 托盘图标右键可退出。", LogLevel.Info);
        _uiReady = true;
    }

    private bool _uiReady;

    /// <summary>底部版本号跟随 csproj 的 Version</summary>
    private void InitVersionFooter()
    {
        var v = typeof(MainWindow).Assembly.GetName().Version;
        if (v != null) VersionRun.Text = $"ProximityLock v{v.Major}.{v.Minor}.{v.Build}";
    }

    private void FooterLink_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "https://github.com/nomoneynolife/ProximityLock") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog($"打开链接失败: {ex.Message}", LogLevel.Warn);
        }
    }

    // ---------- 托盘 ----------

    private void InitTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("显示主界面", null, (_, _) => ShowFromTray());
        menu.Items.Add("退出", null, (_, _) => RealExit());

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Proximity Lock - 手机离开自动锁屏",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void RealExit()
    {
        _realExit = true;
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        // 最小化 → 藏到托盘
        if (WindowState == WindowState.Minimized)
        {
            Hide();
            ShowTrayHint("已最小化到托盘, 双击托盘图标恢复");
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 点 X → 藏到托盘而不是退出
        if (!_realExit)
        {
            e.Cancel = true;
            Hide();
            ShowTrayHint("程序还在托盘运行着, 右键托盘图标可退出");
            return;
        }
        _watcher?.Dispose();
        _trayIcon?.Dispose();
        base.OnClosing(e);
    }

    private int _hintCount;

    private void ShowTrayHint(string text)
    {
        // 每次会话最多提示 2 次, 防止每次最小化都弹气泡
        if (_trayIcon == null || _hintCount >= 2) return;
        _hintCount++;
        _trayIcon.BalloonTipTitle = "Proximity Lock";
        _trayIcon.BalloonTipText = text;
        _trayIcon.ShowBalloonTip(2000);
    }

    // ---------- 开机自启动 ----------

    private static bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(AppName) != null;
    }

    private static void SetAutoStart(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enable)
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            // --minimized 静默藏托盘, --listen 启动后自动监听 (手动打开 exe 不带参, 不自动监听, 方便调试)
            key.SetValue(AppName, $"\"{exe}\" --minimized --listen");
        }
        else
        {
            key.DeleteValue(AppName, false);
        }
    }

    private void InitAutoStart()
    {
        var enabled = IsAutoStartEnabled();
        // 首次运行默认开启自启动
        if (!enabled && !File.Exists(ConfigPath))
        {
            SetAutoStart(true);
            enabled = true;
        }
        AutoStartCheck.IsChecked = enabled;
    }

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (AutoStartCheck == null) return; // XAML 初始化中触发
        try
        {
            SetAutoStart(AutoStartCheck.IsChecked == true);
        }
        catch (Exception ex)
        {
            AppendLog($"开机自启动设置失败: {ex.Message}", LogLevel.Warn);
        }
    }

    // ---------- 自动解锁 (Credential Provider 一键部署) ----------

    private bool _autoUnlockInitializing;

    private void InitAutoUnlock()
    {
        _autoUnlockInitializing = true;
        AutoUnlockCheck.IsChecked = CpInstaller.IsInstalled();
        _autoUnlockInitializing = false;
    }

    private async void AutoUnlock_Changed(object sender, RoutedEventArgs e)
    {
        if (_autoUnlockInitializing || AutoUnlockCheck == null) return; // XAML 初始化中触发

        bool want = AutoUnlockCheck.IsChecked == true;
        string? error = want ? await EnableAutoUnlockAsync() : await DisableAutoUnlockAsync();

        if (error != null)
        {
            // 失败/取消 → 复选框回弹到实际状态
            _autoUnlockInitializing = true;
            AutoUnlockCheck.IsChecked = CpInstaller.IsInstalled();
            _autoUnlockInitializing = false;
            AppendLog($"自动解锁: {error}", LogLevel.Warn);
        }
        else if (want)
        {
            AppendLog("自动解锁已开启: 锁屏后手机回到附近会自动进桌面 (仅本地账号)。" +
                      "换密码后需取消勾选重新设置。", LogLevel.Ok);
        }
        else
        {
            AppendLog("自动解锁已卸载。离开锁屏功能不受影响。", LogLevel.Ok);
        }
    }

    private async Task<string?> EnableAutoUnlockAsync()
    {
        var dlg = new PasswordDialog { Owner = this };
        if (dlg.ShowDialog() != true) return "已取消";

        var confirm = System.Windows.MessageBox.Show(
            "即将自动安装 Windows 凭据提供程序 (需要管理员授权):\n\n" +
            $"  账号: {dlg.Domain}\\{dlg.UserName}\n\n" +
            "• 密码将用 DPAPI 加密存到本机 (C:\\ProgramData\\ProximityLock)\n" +
            "• 仅支持本地账号, 微软邮箱账号登录的机器无效\n" +
            "• 出问题可在本窗口取消勾选即可卸载\n\n继续?",
            "开启自动解锁", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return "已取消";

        AppendLog("正在安装自动解锁组件 (请在 UAC 弹窗里点「是”)…", LogLevel.Info);
        try
        {
            return await CpInstaller.InstallAsync(dlg.UserName, dlg.Domain, dlg.Password);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private async Task<string?> DisableAutoUnlockAsync()
    {
        var confirm = System.Windows.MessageBox.Show(
            "卸载自动解锁组件? (手机离开自动锁屏功能不受影响)",
            "关闭自动解锁", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return "已取消";

        AppendLog("正在卸载自动解锁组件 (请在 UAC 弹窗里点「是”)…", LogLevel.Info);
        try
        {
            return await CpInstaller.UninstallAsync();
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // ---------- 日志清屏 ----------

    /// <summary>读 config 初始化自动清屏 UI 和定时器</summary>
    private void InitLogClear()
    {
        var cfg = LoadConfig();
        AutoClearLogCheck.IsChecked = cfg.autoClearLog;
        var idx = cfg.clearLogMinutes switch
        {
            30 => 0, 60 => 1, 120 => 2, 240 => 3, 480 => 4, _ => 1,
        };
        ClearIntervalBox.SelectedIndex = idx;
        ClearIntervalBox.IsEnabled = cfg.autoClearLog;

        _logClearTimer.Interval = TimeSpan.FromMinutes(Math.Max(5, cfg.clearLogMinutes));
        if (cfg.autoClearLog) _logClearTimer.Start();
    }

    private void SaveLogClearConfig()
    {
        var cfg = LoadConfig();
        cfg.autoClearLog = AutoClearLogCheck.IsChecked == true;
        cfg.clearLogMinutes = ClearIntervalBox.SelectedItem is ComboBoxItem it
            && int.TryParse(it.Tag?.ToString(), out var m) ? m : 60;
        SaveConfig(cfg);
    }

    private void AutoClearLog_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || AutoClearLogCheck == null) return;
        bool on = AutoClearLogCheck.IsChecked == true;
        ClearIntervalBox.IsEnabled = on;
        if (on) _logClearTimer.Start(); else _logClearTimer.Stop();
        SaveLogClearConfig();
    }

    private void ClearInterval_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || ClearIntervalBox == null
            || ClearIntervalBox.SelectedItem is not ComboBoxItem item
            || !int.TryParse(item.Tag?.ToString(), out var minutes)) return;

        _logClearTimer.Interval = TimeSpan.FromMinutes(Math.Max(5, minutes));
        SaveLogClearConfig();
    }

    private void ClearLogBtn_Click(object sender, RoutedEventArgs e)
    {
        LogBox.Document.Blocks.Clear();
    }

    // ---------- 连接模式: 已配对 BLE 设备 ----------

    /// <summary>config.json 全量配置 (设备选择 + 日志自动清屏)</summary>
    public sealed class AppConfig
    {
        public string? deviceAddress { get; set; }
        public string? deviceName { get; set; }
        public bool autoClearLog { get; set; } = true;
        public int clearLogMinutes { get; set; } = 60;
    }

    private static AppConfig LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new AppConfig();
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath)) ?? new AppConfig();
        }
        catch { return new AppConfig(); }
    }

    private static void SaveConfig(AppConfig cfg)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath,
            JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void SaveSelectedDeviceStatic(PairedDevice? device)
    {
        var cfg = LoadConfig();
        cfg.deviceAddress = device?.Address.ToString();
        cfg.deviceName = device?.Name;
        SaveConfig(cfg);
    }

    /// <summary>枚举系统里已配对的 BLE 设备 (手机在 Windows 蓝牙设置里配对一次即可出现在这里)</summary>
    private async Task LoadPairedDevicesAsync()
    {
        try
        {
            var selector = Windows.Devices.Bluetooth.BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
            var devices = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(selector);

            var items = new List<PairedDevice>();
            foreach (var di in devices)
            {
                var addr = ParseBleAddress(di.Id);
                if (addr == null) continue;
                var name = string.IsNullOrWhiteSpace(di.Name) ? $"BLE 设备 ({di.Id[^8..]})" : di.Name;
                items.Add(new PairedDevice(name, addr.Value, name, di.Id));
            }

            // 恢复上次的选择
            var saved = LoadConfig();
            DeviceBox.ItemsSource = items;
            if (!string.IsNullOrEmpty(saved.deviceAddress)
                && ulong.TryParse(saved.deviceAddress, out var savedAddr))
            {
                var hit = items.FirstOrDefault(d => d.Address == savedAddr);
                if (hit != null)
                {
                    DeviceBox.SelectedItem = hit;
                    AppendLog($"已选择连接模式设备: {hit.Display} (开始监听时生效)", LogLevel.Info);
                }
            }
        }
        catch (Exception ex)
        {
            AppendLog($"枚举蓝牙设备失败: {ex.Message}", LogLevel.Warn);
        }
    }

    /// <summary>从 DeviceInformation.Id (形如 BluetoothLE#BluetoothLEaa:bb:..-..) 解析蓝牙地址</summary>
    private static ulong? ParseBleAddress(string id)
    {
        const string prefix = "BluetoothLE#BluetoothLE";
        if (id.Length < prefix.Length + 17 || !id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var mac = id.Substring(prefix.Length, 17).Replace(":", "");
        return ulong.TryParse(mac, System.Globalization.NumberStyles.HexNumber, null, out var v)
            ? v : null;
    }

    private void RefreshDevicesBtn_Click(object sender, RoutedEventArgs e)
    {
        DeviceBox.ItemsSource = null;
        _ = LoadPairedDevicesAsync();
        AppendLog("正在刷新已配对设备列表…", LogLevel.Info);
    }

    private void DeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || DeviceBox == null) return;
        SaveSelectedDeviceStatic(DeviceBox.SelectedItem as PairedDevice);
    }

    // ---------- 监听控制 ----------

    private void StartBtn_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceBox.SelectedItem is not PairedDevice dev)
        {
            AppendLog("先在列表里选中你的手机 (没有就先去 Windows 蓝牙设置配对, 或点「刷新设备」)。",
                LogLevel.Warn);
            return;
        }

        _watcher?.Dispose();
        SaveSelectedDeviceStatic(dev);

        // 连接模式: GATT 长连接, 连着=解锁, 断开=锁屏 (2 秒防抖)
        _watcher = new GattWatcher(dev.Address, dev.DeviceId, dev.Name, lostTimeout: 5);
        _watcher.Log += (msg, level) => Dispatcher.Invoke(() => AppendLog(msg, level));

        _watcher.Start();
        _uiTimer.Start();

        StartBtn.IsEnabled = false;
        StopBtn.IsEnabled = true;
        DeviceBox.IsEnabled = false;
    }

    private void StopBtn_Click(object sender, RoutedEventArgs e)
    {
        _uiTimer.Stop();
        _watcher?.Stop();
        _watcher?.Dispose();
        _watcher = null;

        SignalList.Items.Clear();
        NoSignalText.Text = "已停止监听";
        NoSignalText.Visibility = Visibility.Visible;

        StartBtn.IsEnabled = true;
        StopBtn.IsEnabled = false;
        DeviceBox.IsEnabled = true;
    }

    private void TestLockBtn_Click(object sender, RoutedEventArgs e)
    {
        AppendLog("手动触发锁屏测试…", LogLevel.Info);
        Native.LockWorkStation();
    }

    private void RefreshSignalPanel()
    {
        if (_watcher == null) return;

        SignalList.Items.Clear();
        if (_watcher.IsConnected)
        {
            NoSignalText.Visibility = Visibility.Collapsed;
            SignalList.Items.Add(new TextBlock
            {
                Padding = new Thickness(0, 2, 0, 2),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Foreground = Brushes.Green,
                FontWeight = FontWeights.Bold,
                Text = $"[已连接] {_watcher.DeviceName}  蓝牙长连接在线 (断开 {_watcher.LostTimeoutSeconds}s 后锁屏)",
            });
        }
        else
        {
            NoSignalText.Text = $"{_watcher.DeviceName} 未连接 — 等待自动重连… (断开超过 {_watcher.LostTimeoutSeconds}s 会锁屏)";
            NoSignalText.Visibility = Visibility.Visible;
        }
    }

    private void AppendLog(string msg, LogLevel level)
    {
        var color = level switch
        {
            LogLevel.Ok    => Color.FromRgb(0x2E, 0x7D, 0x32), // 绿: 恢复/正常
            LogLevel.Warn  => Color.FromRgb(0xE6, 0x51, 0x00), // 橙: 警告/未知设备
            LogLevel.Error => Color.FromRgb(0xC6, 0x28, 0x28), // 红: 锁屏/丢失
            _              => Color.FromRgb(0x33, 0x33, 0x33), // 灰黑: 普通
        };

        var para = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
        para.Inlines.Add(new Run($"[{DateTime.Now:HH:mm:ss}] ")
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
        });
        para.Inlines.Add(new Run(msg) { Foreground = new SolidColorBrush(color) });

        LogBox.Document.Blocks.Add(para);
        LogBox.ScrollToEnd();
    }

    private static class Native
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool LockWorkStation();
    }
}
