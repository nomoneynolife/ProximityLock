using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace ProximityLock;

/// <summary>
/// 连接模式监听器: 与已配对手机维持 GATT 长连接, 判定只看连接状态 —
/// 连着 = 手机在旁边 (state.ini 写 near=1, 锁屏界面自动解锁);
/// 断开 = 人走了 (短暂防抖后锁屏)。
/// 认证靠系统级配对: 只连接配对过的那台手机, 别人连不上这份信任。
/// </summary>
public sealed class GattWatcher : IProximityWatcher
{
    private readonly ulong _address;
    private readonly string? _deviceId;   // DeviceInformation.Id: Windows 知道地址类型, 优先用它
    private readonly string _deviceName;

    private BluetoothLEDevice? _device;
    private GattSession? _session;
    private readonly System.Timers.Timer _checkTimer;
    private readonly System.Timers.Timer _retryTimer;

    public int LostTimeoutSeconds { get; set; } = 2;   // 断开后的防抖时间 (防瞬时抖动误锁)

    /// <summary>GATT 连接是否在线</summary>
    public bool IsConnected =>
        _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;

    public string DeviceName => _deviceName;

    // 判定状态
    private bool _everConnected;
    private DateTimeOffset _disconnectedSince = DateTimeOffset.MaxValue;

    // 锁屏判定
    private bool _locked;
    private DateTimeOffset _lastLockAt = DateTimeOffset.MinValue;
    private bool _requireSignalRecovery;   // 手动解锁后解除武装, 重连才恢复

    public event Action<string, LogLevel>? Log;
    public event Action<bool>? LockStateChanged;

    public GattWatcher(ulong address, string? deviceId, string deviceName, int lostTimeout)
    {
        _address = address;
        _deviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
        _deviceName = string.IsNullOrWhiteSpace(deviceName) ? $"BLE:{address:X12}" : deviceName;
        LostTimeoutSeconds = Math.Max(1, lostTimeout);

        _checkTimer = new System.Timers.Timer(500);
        _checkTimer.Elapsed += (_, _) => Evaluate();

        _retryTimer = new System.Timers.Timer(10000);
        _retryTimer.Elapsed += (_, _) => { if (_device == null) _ = ConnectAsync(); };
    }

    public void Start()
    {
        _checkTimer.Start();
        _retryTimer.Start();
        Log?.Invoke($"连接模式: 正在连接 {_deviceName}… (连着=解锁, 断开={LostTimeoutSeconds}s后锁屏)", LogLevel.Info);
        _ = ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        if (_device != null) return;
        try
        {
            // 优先用设备 ID 连接: 配对时 Windows 缓存了地址类型。
            // 直接 FromBluetoothAddressAsync(默认 Public) 对用随机地址 (RPA) 广播的手机会失败。
            BluetoothLEDevice? device = null;
            if (_deviceId != null)
            {
                device = await BluetoothLEDevice.FromIdAsync(_deviceId);
            }
            if (device == null)
            {
                device = await BluetoothLEDevice.FromBluetoothAddressAsync(
                    _address, BluetoothAddressType.Random);
            }
            if (device == null)
            {
                device = await BluetoothLEDevice.FromBluetoothAddressAsync(_address);
            }
            if (device == null)
            {
                Log?.Invoke("暂未连上 (手机不在范围或没在广播?), 10 秒后自动重试。",
                    LogLevel.Info);
                return;
            }
            _device = device;
            _device.ConnectionStatusChanged += OnConnectionChanged;

            _session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId);
            if (_session != null)
            {
                _session.MaintainConnection = true;   // 关键: 系统保持长连接, 断了自动重连
            }

            _everConnected = true;
            Log?.Invoke($"[已连接] {_deviceName}。手机在旁边 → 锁屏界面会自动解锁。", LogLevel.Ok);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"连接失败: {ex.Message}", LogLevel.Error);
        }
    }

    private void OnConnectionChanged(BluetoothLEDevice sender, object args)
    {
        var connected = sender.ConnectionStatus == BluetoothConnectionStatus.Connected;
        if (connected)
        {
            _disconnectedSince = DateTimeOffset.MaxValue;
            if (_requireSignalRecovery)
            {
                _requireSignalRecovery = false;
                Log?.Invoke("[重新武装] 手机重新连上, 自动锁屏恢复判定。", LogLevel.Ok);
            }
            Log?.Invoke("[连接恢复] 手机已连上。", LogLevel.Ok);
        }
        else
        {
            _disconnectedSince = DateTimeOffset.Now;
            Log?.Invoke($"[连接断开] 手机离开范围, {LostTimeoutSeconds}s 后锁屏。", LogLevel.Warn);
        }
        LockStateChanged?.Invoke(!connected);
    }

    private void Evaluate()
    {
        var now = DateTimeOffset.Now;
        var connected = IsConnected;

        // 给 CP 的状态: 连接在线 = 手机在旁边 (锁屏界面自动解锁)
        ProximityState.Write(connected);

        if (!_everConnected) return;   // 从未连上过 (启动预热期) 不判丢失

        var lostSeconds = _disconnectedSince == DateTimeOffset.MaxValue
            ? 0 : (now - _disconnectedSince).TotalSeconds;
        var shouldLock = !connected && lostSeconds >= LostTimeoutSeconds && !_requireSignalRecovery;

        // 手动解锁后解除武装: 用户输了密码 = 人在电脑前, 重连之前不再锁
        if (_locked && !connected && lostSeconds >= LostTimeoutSeconds
            && (now - _lastLockAt).TotalSeconds >= 5 && !IsSessionLocked() && !_requireSignalRecovery)
        {
            _locked = false;
            _requireSignalRecovery = true;
            Log?.Invoke("[手动解锁] 检测到已回到桌面但手机未重连, 暂停自动锁屏 (重连后恢复)。",
                LogLevel.Warn);
        }

        if (shouldLock && !_locked)
        {
            Log?.Invoke($"[触发锁屏] 连接断开 {lostSeconds:F0}s (手机不在蓝牙范围内)", LogLevel.Error);
            _locked = true;
            _lastLockAt = now;
            LockStateChanged?.Invoke(true);
            LockWorkStationSafe();
        }
        else if (!shouldLock && _locked && connected)
        {
            _locked = false;
            Log?.Invoke("[状态复位] 手机已重连。", LogLevel.Ok);
        }
    }

    private static void LockWorkStationSafe()
    {
        try
        {
            if (!LockWorkStation()) Debug.WriteLine("LockWorkStation failed");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"LockWorkStation exception: {ex}");
        }
    }

    private static bool IsSessionLocked()
    {
        try
        {
            return Process.GetProcessesByName("LogonUI").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    public void Stop()
    {
        _checkTimer.Stop();
        _retryTimer.Stop();
        if (_device != null)
        {
            _device.ConnectionStatusChanged -= OnConnectionChanged;
            if (_session != null)
            {
                _session.MaintainConnection = false;
                _session.Dispose();
                _session = null;
            }
            _device.Dispose();
            _device = null;
        }
        _everConnected = false;
        _locked = false;
        _requireSignalRecovery = false;
        _disconnectedSince = DateTimeOffset.MaxValue;
        ProximityState.Write(false);
        LockStateChanged?.Invoke(false);
    }

    public void Dispose() => Stop();
}
