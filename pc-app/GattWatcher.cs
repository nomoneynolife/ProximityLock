using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
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
    // 与安卓端 BeaconService 的最小 GATT 服务一致
    private static readonly Guid ServiceUuid =
        new("9A660001-C77B-4A9D-B5E3-1D6B7A9E2C11");
    private static readonly Guid CharUuid =
        new("9A660002-C77B-4A9D-B5E3-1D6B7A9E2C11");

    private readonly ulong _address;
    private readonly string? _deviceId;   // DeviceInformation.Id: Windows 知道地址类型, 优先用它
    private readonly string _deviceName;

    private BluetoothLEDevice? _device;
    private GattSession? _session;
    private GattCharacteristic? _beatChar;        // 心跳保活读的特征值

    private readonly System.Timers.Timer _checkTimer;       // 500ms: 判定 + 写 state.ini
    private readonly System.Timers.Timer _keepAliveTimer;   // 3s: 心跳 / 低频促连
    private int _keepAliveBusy;
    private int _heartbeatFailures;
    private DateTimeOffset _lastPokeAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastNotFoundLog = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSuspectLog = DateTimeOffset.MinValue;
    private int _discovering;   // 防止并发服务发现

    // 防抖 5s: 手机省电策略会让链路瞬断几秒再自动重连,
    // 5s 内连回来视为抖动不锁屏; 只有真离开 (持续断开) 才锁
    public int LostTimeoutSeconds { get; set; } = 5;

    // 第一次成功做过全量服务发现后, 之后全走缓存 —
    // Uncached 全量发现是重活, 会晃动刚建立的链路 (开软件必误锁一次的根源)
    private bool _discoveredOnce;

    /// <summary>GATT 连接是否在线</summary>
    public bool IsConnected =>
        _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;

    public string DeviceName => _deviceName;

    // 链路状态: 只在真实变化时打日志 (避免事件抖动刷屏)
    private bool _linkUp;
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

        // 3 秒一轮: 连着 = 读特征值心跳; 断开 = 低频唤醒系统重连 (不反复 dispose 重建)
        _keepAliveTimer = new System.Timers.Timer(3000);
        _keepAliveTimer.Elapsed += (_, _) =>
        {
            if (Interlocked.Exchange(ref _keepAliveBusy, 1) == 0)
            {
                _ = KeepAliveTick();
            }
        };
    }

    public void Start()
    {
        _checkTimer.Start();
        _keepAliveTimer.Start();
        Log?.Invoke($"连接模式: 正在连接 {_deviceName}… (连着=解锁, 断开={LostTimeoutSeconds}s后锁屏)", LogLevel.Info);
        _ = KeepAliveTick();
    }

    // ---------- 建立 / 发现 ----------

    private async Task EnsureDeviceAsync()
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
            device ??= await BluetoothLEDevice.FromBluetoothAddressAsync(
                _address, BluetoothAddressType.Random);
            device ??= await BluetoothLEDevice.FromBluetoothAddressAsync(_address);

            if (device == null)
            {
                // 手机当前不在范围 / 蓝牙关闭 —— 静默重试, 30 秒才提一次
                if ((DateTimeOffset.Now - _lastNotFoundLog).TotalSeconds >= 30)
                {
                    _lastNotFoundLog = DateTimeOffset.Now;
                    Log?.Invoke("暂未找到手机 (不在范围或蓝牙未开?), 后台持续重试中…", LogLevel.Info);
                }
                return;
            }

            _device = device;
            _device.ConnectionStatusChanged += OnConnectionChanged;

            _session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId);
            if (_session != null)
            {
                // 让系统层面维持长连接: 手机暂时离开也会持续尝试重连,
                // 不需要我们自己 dispose 重建 (那才是之前日志刷屏的根源)
                _session.MaintainConnection = true;
            }

            await DiscoverAsync();
        }
        catch (Exception ex)
        {
            ClearDevice();
            // 手机不在范围时这里会反复抛异常, 30 秒提示一次就够
            if ((DateTimeOffset.Now - _lastNotFoundLog).TotalSeconds >= 30)
            {
                _lastNotFoundLog = DateTimeOffset.Now;
                Log?.Invoke($"初始化连接失败: {ex.Message} (持续重试中)", LogLevel.Warn);
            }
        }
    }

    /// <summary>
    /// 服务发现。注意: Windows 真正建立链路是在第一次访问 GATT 时,
    /// 所以这个方法本身就是"发起连接"的动作。
    /// </summary>
    private async Task DiscoverAsync()
    {
        if (Interlocked.Exchange(ref _discovering, 1) == 1) return;
        var device = _device;
        if (device == null) { Interlocked.Exchange(ref _discovering, 0); return; }
        try
        {
            // 第一次: Uncached 强制向手机真实发现 (拿准 GATT 数据库);
            // 之后: Cached 只查本地缓存, 零 GATT 事务, 不会晃动链路
            var mode = _discoveredOnce ? BluetoothCacheMode.Cached : BluetoothCacheMode.Uncached;
            var services = await device.GetGattServicesAsync(mode);
            var svc = services.Services.FirstOrDefault(s => s.Uuid == ServiceUuid);

            if (svc == null)
            {
                // 缓存兜底也拿不到就静默等下轮 — 服务发现只是心跳保活的辅助,
                // 连接判定只看链路状态, 失败不值得打扰用户
                if (_discoveredOnce) return;
                var cached = await device.GetGattServicesAsync(BluetoothCacheMode.Cached);
                svc = cached.Services.FirstOrDefault(s => s.Uuid == ServiceUuid);
                if (svc == null) return;
            }

            var chars = await svc.GetCharacteristicsAsync(mode);
            if (chars.Characteristics.Count == 0)
            {
                chars = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Cached);
            }
            _beatChar = chars.Characteristics.FirstOrDefault(c => c.Uuid == CharUuid);
            if (_beatChar != null) _discoveredOnce = true;
        }
        catch
        {
            // 发现失败不影响连接判定, 下一轮保活会再来
            _beatChar = null;
        }
        finally
        {
            Interlocked.Exchange(ref _discovering, 0);
        }
    }

    // ---------- 保活 ----------

    private async Task KeepAliveTick()
    {
        try
        {
            if (_device == null)
            {
                await EnsureDeviceAsync();
                return;
            }

            if (IsConnected)
            {
                // 连上但还没发现服务 → 补一次
                if (_beatChar == null)
                {
                    await DiscoverAsync();
                    if (_beatChar == null) return;
                }

                // 心跳: 读一次特征值, 保持链路活跃 + 确认连接真的可用
                try
                {
                    await _beatChar.ReadValueAsync(BluetoothCacheMode.Cached);
                    _heartbeatFailures = 0;
                }
                catch
                {
                    _heartbeatFailures++;
                    if (_heartbeatFailures >= 3)
                    {
                        if ((DateTimeOffset.Now - _lastSuspectLog).TotalSeconds >= 30)
                        {
                            _lastSuspectLog = DateTimeOffset.Now;
                            Log?.Invoke("连接无响应, 正在重新建立链路…", LogLevel.Warn);
                        }
                        ResetGattHandles();
                        await DiscoverAsync();
                    }
                }

                // 轮询指令通道: 不依赖 CCCD 订阅 — 手机点按钮时屏幕必亮,
                // 亮屏下读事务是通的, 用户按下后最多 3 秒就会被这里取到
                return;
            }

            // 已断开: 低频 (3s) 踢一下系统重新连接。
            // 不 dispose 设备对象 —— MaintainConnection 会自己重试, 我们只是加速。
            // 3s 是为了手机端「解锁电脑」(重开蓝牙) 后 PC 能秒级重连回来自动解锁。
            if ((DateTimeOffset.Now - _lastPokeAt).TotalSeconds >= 3)
            {
                _lastPokeAt = DateTimeOffset.Now;
                ResetGattHandles();
                await DiscoverAsync();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _keepAliveBusy, 0);
        }
    }

    /// <summary>丢弃 GATT 特征值句柄 (下次重新发现), 但保留设备对象让系统自己重连</summary>
    private void ResetGattHandles()
    {
        _beatChar = null;
        _heartbeatFailures = 0;
    }

    private void ClearDevice()
    {
        ResetGattHandles();
        if (_device != null)
        {
            _device.ConnectionStatusChanged -= OnConnectionChanged;
            _device.Dispose();
            _device = null;
        }
        _session?.Dispose();
        _session = null;
        _linkUp = false;
    }

    private void OnConnectionChanged(BluetoothLEDevice sender, object args)
    {
        // 只更新计时起点, 日志统一由 Evaluate 按真实状态迁移打印
        if (sender.ConnectionStatus != BluetoothConnectionStatus.Connected)
        {
            if (_disconnectedSince == DateTimeOffset.MaxValue)
            {
                _disconnectedSince = DateTimeOffset.Now;
            }
            // 断开瞬间立刻踢一次重连 — 大多数是手机省电抖动, 早 1 秒连回来就少一次误锁
            _lastPokeAt = DateTimeOffset.MinValue;
            if (Interlocked.Exchange(ref _keepAliveBusy, 1) == 0)
            {
                _ = Task.Run(async () => await KeepAliveTick());
            }
        }
    }

    // ---------- 唤醒显示器 ----------

    /// <summary>
    /// 唤醒序列: 先靠 SetThreadExecutionState 阻止屏幕再熄灭;
    /// 凭据提交要几秒, 等回到桌面后 SendInput 才有效 — 分几个时间点
    /// 发一次"零位移"鼠标事件把显示器点亮 (光标不动, 不打扰正在用电脑的人)。
    /// </summary>
    private static async Task WakeSequenceAsync()
    {
        lock (WakeLock)
        {
            if (_wakeBusy) return;
            _wakeBusy = true;
        }
        try
        {
            SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
            int[] points = { 2500, 5000, 8000, 12000 };
            int prev = 0;
            foreach (var t in points)
            {
                await Task.Delay(t - prev);
                prev = t;
                NudgeMouse();   // 锁屏界面下无效 (安全桌面隔离), 回到桌面后即可点亮
            }
        }
        finally
        {
            SetThreadExecutionState(ES_CONTINUOUS);
            lock (WakeLock) { _wakeBusy = false; }
        }
    }

    /// <summary>零位移鼠标移动: 产生输入事件唤醒显示器, 但光标原地不动</summary>
    private static void NudgeMouse()
    {
        try
        {
            var input = new INPUT
            {
                type = INPUT_MOUSE,
                mi = new MOUSEINPUT { dx = 0, dy = 0, dwFlags = MOUSEEVENTF_MOVE },
            };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"NudgeMouse failed: {ex}");
        }
    }

    // ---------- 状态判定 ----------

    private void Evaluate()
    {
        var now = DateTimeOffset.Now;
        var connected = IsConnected;

        // 给 CP 的状态: 连接在线 = 手机在旁边 (锁屏界面自动解锁)
        ProximityState.Write(connected);

        // 真实状态迁移 → 只在这里打日志, 数量可控
        if (connected && !_linkUp)
        {
            _linkUp = true;
            _everConnected = true;
            _disconnectedSince = DateTimeOffset.MaxValue;
            if (_requireSignalRecovery)
            {
                _requireSignalRecovery = false;
                Log?.Invoke("[重新武装] 手机重新连上, 自动锁屏恢复判定。", LogLevel.Ok);
            }
            else
            {
                Log?.Invoke($"[已连接] {_deviceName}", LogLevel.Ok);
            }
            LockStateChanged?.Invoke(false);
            // 手机重连时若电脑在锁屏 (典型场景: 手机端「解锁电脑」重开蓝牙),
            // 立刻启动唤醒序列 — 凭据提供程序自动提交的同时把显示器点亮
            if (IsSessionLocked()) _ = Task.Run(WakeSequenceAsync);
            _ = DiscoverAsync();   // 连接刚建立时补一次服务发现 (订阅指令通道)
        }
        else if (!connected && _linkUp)
        {
            _linkUp = false;
            _disconnectedSince = now;
            Log?.Invoke($"[连接断开] 手机不在范围内, {LostTimeoutSeconds}s 后锁屏。", LogLevel.Warn);
            LockStateChanged?.Invoke(true);
        }

        if (!_everConnected) return;   // 从未连上过 (启动预热期) 不判丢失

        // 兜底: Windows 的断开事件有时延迟上报, 这里实时探测 —
        // 发现已断开但计时还没开始, 立刻补上起点 (不依赖事件时机)
        if (!connected && _disconnectedSince == DateTimeOffset.MaxValue)
        {
            _disconnectedSince = now;
        }

        var lostSeconds = _disconnectedSince == DateTimeOffset.MaxValue
            ? 0 : (now - _disconnectedSince).TotalSeconds;

        // 手动解锁后解除武装: 用户输了密码 = 人在电脑前, 重连之前不再锁
        // (放在 shouldLock 计算之前, 否则同一轮判定会先解除武装又立刻锁回去)
        if (_locked && !connected && lostSeconds >= LostTimeoutSeconds
            && (now - _lastLockAt).TotalSeconds >= 5 && !IsSessionLocked() && !_requireSignalRecovery)
        {
            _locked = false;
            _requireSignalRecovery = true;
            Log?.Invoke("[手动解锁] 检测到已回到桌面但手机未重连, 暂停自动锁屏 (重连后恢复)。",
                LogLevel.Warn);
        }

        var shouldLock = !connected && lostSeconds >= LostTimeoutSeconds && !_requireSignalRecovery;

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

    // ---------- 唤醒显示器 ----------

    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;
    private const uint ES_DISPLAY_REQUIRED = 0x00000002;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private static readonly object WakeLock = new();
    private static bool _wakeBusy;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    public void Stop()
    {
        _checkTimer.Stop();
        _keepAliveTimer.Stop();
        ClearDevice();
        _everConnected = false;
        _locked = false;
        _requireSignalRecovery = false;
        _disconnectedSince = DateTimeOffset.MaxValue;
        ProximityState.Write(false);
        LockStateChanged?.Invoke(false);
    }

    public void Dispose() => Stop();
}
