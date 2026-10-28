using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace ProximityLock;

/// <summary>
/// 连接模式监听器: 与手机维持 GATT 长连接, 用「连接是否在线」判定远近。
/// 广播模式的痛点 (熄屏暂停广播 → 信号丢失误判) 在连接模式下不存在:
/// 连接不随手机熄屏断开, 断开 = 人真的走了。
/// 对应安卓端 BeaconService 的 GATT 服务器 (SERVICE_UUID / CHAR_UUID)。
/// </summary>
public sealed class GattWatcher : IProximityWatcher
{
    // 与安卓端 BeaconService 一致
    private static readonly Guid ServiceUuid =
        new("9A660001-C77B-4A9D-B5E3-1D6B7A9E2C11");
    private static readonly Guid CharUuid =
        new("9A660002-C77B-4A9D-B5E3-1D6B7A9E2C11");

    private readonly ulong _address;
    private readonly string _deviceName;

    private BluetoothLEDevice? _device;
    private GattSession? _session;
    private BluetoothLEAdvertisementWatcher? _advWatcher; // 断连时手机会恢复广播, 用来显示 RSSI
    private readonly System.Timers.Timer _checkTimer;
    private readonly System.Timers.Timer _retryTimer;

    public Guid TargetUuid { get; set; }
    public int RssiThreshold { get; set; } = -65;
    public int LostTimeoutSeconds { get; set; } = 8;
    public string? SeedHex { get; set; }

    /// <summary>GATT 连接是否在线</summary>
    public bool IsConnected =>
        _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;

    /// <summary>最近一次从广播里看到的 RSSI (断连时才有广播; 连接中广播停止, 显示 -)</summary>
    public int LastRssi { get; private set; } = int.MinValue;

    public string DeviceName => _deviceName;

    // 判定状态
    private bool _everConnected;
    private bool _tokenOk;
    private DateTimeOffset _disconnectedSince = DateTimeOffset.MaxValue;
    private DateTimeOffset _lastRssiAt = DateTimeOffset.MinValue;

    // 锁屏判定 (与 BeaconWatcher 相同的防抖/解除武装逻辑)
    private const int GraceSeconds = 3;
    private bool _locked;
    private DateTimeOffset _lastLockAt = DateTimeOffset.MinValue;
    private bool _requireSignalRecovery;

    public event Action<string, LogLevel>? Log;
    public event Action<int>? RssiUpdated;
    public event Action<bool>? LockStateChanged;

    public GattWatcher(ulong address, string deviceName, int rssiThreshold, int lostTimeout)
    {
        _address = address;
        _deviceName = string.IsNullOrWhiteSpace(deviceName) ? $"BLE:{address:X12}" : deviceName;
        TargetUuid = Guid.Empty;
        RssiThreshold = rssiThreshold;
        LostTimeoutSeconds = lostTimeout;

        _checkTimer = new System.Timers.Timer(1000);
        _checkTimer.Elapsed += (_, _) => Evaluate();

        _retryTimer = new System.Timers.Timer(15000);
        _retryTimer.Elapsed += (_, _) => { if (_device == null) _ = ConnectAsync(); };
    }

    public void Start()
    {
        _checkTimer.Start();
        _retryTimer.Start();

        // 断连时手机恢复广播, 监听它拿 RSSI 给 UI 显示 (连接中手机不广播, RSSI 会停在旧值)
        _advWatcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
        };
        _advWatcher.Received += OnAdvReceived;
        try { _advWatcher.Start(); } catch { /* 蓝牙不可用时不影响连接本身 */ }

        Log?.Invoke($"连接模式: 正在连接 {_deviceName}…", LogLevel.Info);
        _ = ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        if (_device != null) return;
        try
        {
            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(_address);
            if (device == null)
            {
                // 手机当前不在广播 (熄屏/不在范围) —— 15 秒后重试, 不锁屏 (还没建立过连接)
                Log?.Invoke("暂未连上 (手机没在广播?), 15 秒后自动重试。首次连接建议先点亮手机屏幕。",
                    LogLevel.Info);
                return;
            }
            _device = device;
            _device.ConnectionStatusChanged += OnConnectionChanged;

            _session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId);
            if (_session != null)
            {
                _session.MaintainConnection = true;   // 关键: 系统保持长连接, 断了自动重连
                _session.SessionStatusChanged += (_, args) =>
                    Log?.Invoke($"GATT 会话状态: {args.Status}", LogLevel.Info);
            }

            _everConnected = true;
            Log?.Invoke($"已连接 {_deviceName} (连接模式, 熄屏不再导致误判)。", LogLevel.Ok);
            _ = VerifyTokenAsync();
        }
        catch (Exception ex)
        {
            Log?.Invoke($"连接失败: {ex.Message}", LogLevel.Error);
        }
    }

    /// <summary>读取手机 GATT 令牌验证「连上的真是我们的手机」(防邻居手机冒名)</summary>
    private async Task VerifyTokenAsync()
    {
        try
        {
            var services = await _device!.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            var svc = services.Services.FirstOrDefault(s => s.Uuid == ServiceUuid);
            if (svc == null)
            {
                Log?.Invoke("未找到 ProximityLock 服务 (手机端没在广播服务?), 跳过令牌验证。",
                    LogLevel.Warn);
                _tokenOk = true;   // 连得上就当是自己人, 验证失败不阻断 (兼容旧版手机端)
                return;
            }

            var chars = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            var ch = chars.Characteristics.FirstOrDefault(c => c.Uuid == CharUuid);
            if (ch == null)
            {
                _tokenOk = true;
                return;
            }

            var result = await ch.ReadValueAsync(BluetoothCacheMode.Uncached);
            if (result.Status != GattCommunicationStatus.Success)
            {
                Log?.Invoke($"令牌读取失败: {result.Status}, 跳过验证。", LogLevel.Warn);
                _tokenOk = true;
                return;
            }

            var reader = DataReader.FromBuffer(result.Value);
            var token = new byte[result.Value.Length];
            reader.ReadBytes(token);

            _tokenOk = TokenMatches(token);
            Log?.Invoke(_tokenOk
                ? "令牌验证通过: 确认是绑定的手机。"
                : "令牌不匹配! 连上的不是我们的手机, 不会自动解锁。", _tokenOk ? LogLevel.Ok : LogLevel.Error);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"令牌验证异常: {ex.Message}, 跳过验证。", LogLevel.Warn);
            _tokenOk = true;
        }
    }

    /// <summary>与安卓端 RollingUuid.tokenBytes 一致: 滚动模式 = HMAC(seed, 时间片)[0..16],
    /// 固定模式 = 固定 UUID 的大端 16 字节。时间片容忍 ±1。</summary>
    private bool TokenMatches(byte[] token)
    {
        if (token.Length != 16) return false;

        if (!string.IsNullOrWhiteSpace(SeedHex))
        {
            var seedBytes = Convert.FromHexString(SeedHex!);
            var now = RollingUuid.SliceAt();
            for (var s = now - 1; s <= now + 1; s++)
            {
                var msg = new byte[8];
                for (var i = 0; i < 8; i++) msg[i] = (byte)(s >> (8 * (7 - i)));
                var mac = HMACSHA256.HashData(seedBytes, msg);
                if (mac.AsSpan(0, 16).SequenceEqual(token)) return true;
            }
            return false;
        }

        var expect = GuidToBigEndian(TargetUuid);
        return expect.SequenceEqual(token);
    }

    /// <summary>Guid → 蓝牙大端 16 字节 (与 Guid 字符串同序)</summary>
    private static byte[] GuidToBigEndian(Guid g)
    {
        var hex = g.ToString("N");
        return Convert.FromHexString(hex);
    }

    private void OnAdvReceived(BluetoothLEAdvertisementWatcher sender,
        BluetoothLEAdvertisementReceivedEventArgs args)
    {
        // 只关心已选设备地址的广播 (手机断连时才会广播)
        if (args.BluetoothAddress != _address) return;
        LastRssi = args.RawSignalStrengthInDBm;
        _lastRssiAt = DateTimeOffset.Now;
        RssiUpdated?.Invoke(LastRssi);
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
            Log?.Invoke("手机已重新连接。", LogLevel.Ok);
            _ = VerifyTokenAsync();
        }
        else
        {
            _disconnectedSince = DateTimeOffset.Now;
            _tokenOk = false;
            Log?.Invoke("手机连接断开 (人走了?), 开始计时判定。", LogLevel.Warn);
        }
        LockStateChanged?.Invoke(!connected);
    }

    private void Evaluate()
    {
        var now = DateTimeOffset.Now;
        var connected = IsConnected;

        // 给 CP 的状态: 连接在线且令牌验证通过 = 手机在附近
        ProximityState.Write(connected && _tokenOk);

        if (!_everConnected) return;   // 从未连上过 (启动预热期) 不判丢失

        var lostSeconds = _disconnectedSince == DateTimeOffset.MaxValue
            ? 0 : (now - _disconnectedSince).TotalSeconds;
        var shouldLock = !connected && lostSeconds >= LostTimeoutSeconds && !_requireSignalRecovery;

        // 手动解锁后解除武装 (同广播模式): 用户输了密码 = 人在电脑前
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
            Log?.Invoke("[信号恢复] 手机已重连, 监听状态复位。", LogLevel.Ok);
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

    /// <summary>给 UI 的快照: 连接中显示在线, 断连显示 RSSI (如果有广播)</summary>
    public List<BeaconInfo> Snapshot()
    {
        var now = DateTimeOffset.Now;
        if (IsConnected)
        {
            var rssi = _lastRssiAt == DateTimeOffset.MinValue ? 0 : LastRssi;
            var age = _lastRssiAt == DateTimeOffset.MinValue ? 0 : (now - _lastRssiAt).TotalSeconds;
            return new List<BeaconInfo> { new(TargetUuid, rssi, age, true) };
        }
        if (_lastRssiAt == DateTimeOffset.MinValue || (now - _lastRssiAt).TotalSeconds > 15)
            return new List<BeaconInfo>();
        return new List<BeaconInfo>
        {
            new(TargetUuid, LastRssi, (now - _lastRssiAt).TotalSeconds, true)
        };
    }

    public void Stop()
    {
        _checkTimer.Stop();
        _retryTimer.Stop();
        if (_advWatcher != null)
        {
            _advWatcher.Received -= OnAdvReceived;
            try { _advWatcher.Stop(); } catch { }
            _advWatcher = null;
        }
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
        _tokenOk = false;
        _locked = false;
        _requireSignalRecovery = false;
        _disconnectedSince = DateTimeOffset.MaxValue;
        _lastRssiAt = DateTimeOffset.MinValue;
        LastRssi = int.MinValue;
        ProximityState.Write(false);
        LockStateChanged?.Invoke(false);
    }

    public void Dispose() => Stop();
}
