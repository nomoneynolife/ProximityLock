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
    private const string DefaultUuid = "E2C56DB5-DFFB-48D2-B060-D0F5A71096E0";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "ProximityLock";

    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ProximityLock", "config.json");

    private BeaconWatcher? _watcher;
    private readonly DispatcherTimer _uiTimer;

    // 托盘
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _realExit;

    public MainWindow()
    {
        InitializeComponent();

        // 启动时读取固化配置, 没有就用默认 UUID
        UuidBox.Text = LoadConfigUuid() ?? DefaultUuid;
        SeedBox.Text = LoadConfigSeed() ?? "";
        UpdateRollingUiState();

        RssiSlider.ValueChanged += (_, e) =>
            RssiValueText.Text = $"{(int)e.NewValue} dBm (阈值)";

        // 实时信号面板 1 秒刷新一次
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uiTimer.Tick += (_, _) => RefreshSignalPanel();

        InitTrayIcon();
        InitAutoStart();
        InitAutoUnlock();
        InitVersionFooter();

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

        AppendLog("就绪。先在安卓端开启广播, 再点“开始监听”。", LogLevel.Info);
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
        else if (enabled)
        {
            // 兼容旧版: 注册表里还是老参数的话升级成 --minimized --listen
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            var cur = key?.GetValue(AppName) as string ?? "";
            if (!cur.Contains("--listen", StringComparison.OrdinalIgnoreCase))
            {
                SetAutoStart(true);
            }
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

    // ---------- UUID / 滚动种子 配置固化 ----------

    private static string? LoadConfigUuid()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return null;
            var json = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            return json.RootElement.TryGetProperty("uuid", out var u) ? u.GetString() : null;
        }
        catch { return null; }
    }

    private static string? LoadConfigSeed()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return null;
            var json = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            return json.RootElement.TryGetProperty("seed", out var s) ? s.GetString() : null;
        }
        catch { return null; }
    }

    private static void SaveConfig(string uuid, string seed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath,
            JsonSerializer.Serialize(new { uuid, seed },
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private string EffectiveSeed
        => RollingUuid.IsValidSeed(SeedBox.Text) ? SeedBox.Text.Trim().ToLowerInvariant() : "";

    /// <summary>滚动模式开关变化: 固定 UUID 失效置灰</summary>
    private void UpdateRollingUiState()
    {
        var rolling = EffectiveSeed.Length > 0;
        UuidBox.IsEnabled = !rolling;
        UuidBox.Opacity = rolling ? 0.5 : 1.0;
    }

    /// <summary>种子框内容变化 → 校验并立即固化 (输入合法 hex 才生效, 不用单独按保存)</summary>
    private void SeedBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SeedBox == null || !_uiReady) return; // XAML 初始化中触发
        var seed = EffectiveSeed;
        SaveConfig(UuidBox.Text.Trim(), seed);
        UpdateRollingUiState();
        if (seed.Length > 0 && SeedBox.Text.Trim().Length == 32)
        {
            // 输满 32 位合法 hex 时提示一次并热切模式
            AppendLog("滚动 UUID 已启用: 两端种子一致才会互相识别, 手机在跑的话重启广播生效。", LogLevel.Ok);
        }
        RestartWatcherIfRunning();
    }

    /// <summary>剪贴板经常被其他程序短暂锁住 (CLIPBRD_E_CANT_OPEN), 重试几次</summary>
    private static bool TrySetClipboard(string text)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                System.Threading.Thread.Sleep(120);
            }
        }
        return false;
    }

    private void SeedGenBtn_Click(object sender, RoutedEventArgs e)
    {
        SeedBox.Text = RollingUuid.RandomSeed();   // TextChanged 会自动保存
        if (TrySetClipboard(SeedBox.Text))
            AppendLog("已生成滚动种子并复制到剪贴板 → 粘贴到手机端「滚动 UUID 种子」框, 两端一致即可。", LogLevel.Ok);
        else
            AppendLog("种子已生成, 但复制剪贴板失败 (被其他程序占用), 手动复制输入框内容。", LogLevel.Warn);
    }

    private void SeedCopyBtn_Click(object sender, RoutedEventArgs e)
    {
        if (TrySetClipboard(SeedBox.Text.Trim()))
            AppendLog("种子已复制到剪贴板。", LogLevel.Info);
        else
            AppendLog("复制失败: 剪贴板被其他程序占用, 稍后再试或手动复制。", LogLevel.Warn);
    }

    private void SeedClearBtn_Click(object sender, RoutedEventArgs e)
    {
        SeedBox.Text = "";   // TextChanged 会保存空种子并热切回固定模式
        AppendLog("已清除种子, 回到固定 UUID 模式。", LogLevel.Info);
    }

    private void RandBtn_Click(object sender, RoutedEventArgs e)
    {
        UuidBox.Text = Guid.NewGuid().ToString().ToUpper();
        SaveConfig(UuidBox.Text.Trim(), EffectiveSeed);
        AppendLog("已生成随机 UUID, 手机端填同一个后点“保存”固化。", LogLevel.Info);
    }

    private void CopyBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(UuidBox.Text.Trim());
            AppendLog("UUID 已复制到剪贴板。", LogLevel.Info);
        }
        catch (Exception ex)
        {
            AppendLog($"复制失败: {ex.Message}", LogLevel.Warn);
        }
    }

    private void DefaultBtn_Click(object sender, RoutedEventArgs e)
    {
        UuidBox.Text = DefaultUuid;
        SaveConfig(DefaultUuid, EffectiveSeed);
        RestartWatcherIfRunning();
        AppendLog("已恢复默认 UUID 并固化。", LogLevel.Ok);
    }

    private void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!Guid.TryParse(UuidBox.Text.Trim(), out _))
        {
            AppendLog("UUID 格式不对, 没保存。", LogLevel.Warn);
            return;
        }
        SaveConfig(UuidBox.Text.Trim(), EffectiveSeed);
        RestartWatcherIfRunning();
        AppendLog($"UUID 已固化: {UuidBox.Text.Trim()} (下次开机自动加载)", LogLevel.Ok);
    }

    /// <summary>UUID 变了, 正在监听的话停掉让用户重新开始</summary>
    private void RestartWatcherIfRunning()
    {
        if (_watcher == null) return;
        _uiTimer.Stop();
        _watcher.Stop();
        _watcher.Dispose();
        _watcher = null;
        SignalList.Items.Clear();
        NoSignalText.Text = "UUID 已变更, 重新点“开始监听”";
        NoSignalText.Visibility = Visibility.Visible;
        StartBtn.IsEnabled = true;
        StopBtn.IsEnabled = false;
        UuidBox.IsEnabled = true;
    }

    // ---------- 监听控制 ----------

    private void StartBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!Guid.TryParse(UuidBox.Text.Trim(), out var uuid))
        {
            AppendLog("UUID 格式不对, 检查一下。", LogLevel.Warn);
            return;
        }
        if (!int.TryParse(LostTimeoutBox.Text.Trim(), out var lostTimeout) || lostTimeout < 1)
        {
            AppendLog("信号丢失判定时间最低 1 秒。", LogLevel.Warn);
            return;
        }

        _watcher?.Dispose();
        _watcher = new BeaconWatcher(uuid, (int)RssiSlider.Value, lostTimeout)
        {
            SeedHex = EffectiveSeed.Length > 0 ? EffectiveSeed : null,
        };
        if (EffectiveSeed.Length > 0)
            AppendLog("滚动 UUID 模式: 正在按种子动态识别目标手机。", LogLevel.Info);
        _watcher.Log += (msg, level) => Dispatcher.Invoke(() => AppendLog(msg, level));

        _watcher.Start();
        _uiTimer.Start();

        StartBtn.IsEnabled = false;
        StopBtn.IsEnabled = true;
        UuidBox.IsEnabled = false;
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
        UuidBox.IsEnabled = true;
        RssiValueText.Text = $"{(int)RssiSlider.Value} dBm (阈值)";
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
        var list = _watcher.Snapshot();

        if (list.Count == 0)
        {
            NoSignalText.Text = "正在扫描, 未发现目标手机 (手机开广播了吗? UUID 一致吗?)";
            NoSignalText.Visibility = Visibility.Visible;
            return;
        }
        NoSignalText.Visibility = Visibility.Collapsed;

        foreach (var b in list)
        {
            // 绿 = 达到阈值 (在附近, 可自动解锁); 橙 = 收得到但低于阈值 (远处, 不算在附近)
            bool close = b.AvgRssi >= _watcher.RssiThreshold;
            var tb = new TextBlock
            {
                Padding = new Thickness(0, 2, 0, 2),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Foreground = close ? Brushes.Green : Brushes.Orange,
                FontWeight = FontWeights.Bold,
                Text = close
                    ? $"[目标手机·在附近] {b.Uuid}  {b.AvgRssi} dBm  · {b.AgeSeconds:F0}s 前"
                    : $"[目标手机·信号弱] {b.Uuid}  {b.AvgRssi} dBm (< 阈值 {_watcher.RssiThreshold}, 会触发锁屏)",
            };
            SignalList.Items.Add(tb);
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
