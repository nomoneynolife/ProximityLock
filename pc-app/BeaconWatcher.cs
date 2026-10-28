using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth.Advertisement;

namespace ProximityLock;

public enum LogLevel { Info, Ok, Warn, Error }

/// <summary>实时信号面板里的一条 Beacon 记录</summary>
public sealed record BeaconInfo(Guid Uuid, int AvgRssi, double AgeSeconds, bool IsTarget);

/// <summary>
/// 监听安卓端发出的 iBeacon 广播，根据 RSSI 判定手机是否离开，触发锁屏。
/// </summary>
public sealed class BeaconWatcher : IProximityWatcher
{
    // iBeacon 帧格式: 厂商数据 (Apple 0x004C): [0x02, 0x15, UUID 16字节, Major 2字节, Minor 2字节, TxPower 1字节]
    private const ushort AppleCompanyId = 0x004C;

    private BluetoothLEAdvertisementWatcher? _watcher;
    private readonly System.Timers.Timer _checkTimer;

    // RSSI 平滑 (指数移动平均, 仅目标设备)
    private double _rssiAvg = double.MinValue;
    private DateTimeOffset _lastSeen = DateTimeOffset.MinValue;
    private bool _everSeen;

    // 配置
    public Guid TargetUuid { get; set; }
    public int RssiThreshold { get; set; } = -65;   // 约 2-3 米
    public int LostTimeoutSeconds { get; set; } = 8; // 丢包兜底: 熄屏省电会暂停广播, 不能太紧

    /// <summary>滚动 UUID 种子 (32位hex)。非空 = 滚动模式, TargetUuid 不再生效。</summary>
    public string? SeedHex { get; set; }

    /// <summary>该 UUID 是否目标手机: 固定模式比对 TargetUuid; 滚动模式比对种子推导的时间片集合 (上一片+当前+下一片, 容忍两端时钟差)</summary>
    private bool IsTarget(Guid uuid)
    {
        if (string.IsNullOrWhiteSpace(SeedHex)) return uuid == TargetUuid;
        var now = RollingUuid.SliceAt();
        for (long s = now - 1; s <= now + 1; s++)
        {
            if (uuid == RollingUuid.Derive(SeedHex!, s)) return true;
        }
        return false;
    }

    // 锁屏防抖: 信号变差后需持续低于阈值 GraceSeconds 才锁, 避免误判
    private const int GraceSeconds = 3;
    private DateTimeOffset _belowSince = DateTimeOffset.MaxValue;
    private bool _locked;
    private DateTimeOffset _lastLockAt = DateTimeOffset.MinValue;
    // 手动解锁后解除武装: 用户输了密码 = 人在电脑前, 重新看到手机信号前不再自动锁屏
    private bool _requireSignalRecovery;

    // 实时信号: 所有见过的 iBeacon (目标+未知) -> (平滑RSSI, 最后收到时间)
    private readonly Dictionary<Guid, (double Avg, DateTimeOffset Last)> _beacons = new();
    private readonly object _sync = new();

    public event Action<string, LogLevel>? Log;
    public event Action<int>? RssiUpdated;   // 平滑后的 RSSI (仅目标)
    public event Action<bool>? LockStateChanged;

    public BeaconWatcher(Guid targetUuid, int rssiThreshold, int lostTimeoutSeconds)
    {
        TargetUuid = targetUuid;
        RssiThreshold = rssiThreshold;
        LostTimeoutSeconds = lostTimeoutSeconds;

        _checkTimer = new System.Timers.Timer(1000);
        _checkTimer.Elapsed += (_, _) => Evaluate();
    }

    public void Start()
    {
        StopInternal();

        _watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
        };

        _watcher.Received += OnReceived;
        _watcher.Start();

        _checkTimer.Start();
        Log?.Invoke($"开始监听, UUID={TargetUuid}, 阈值={RssiThreshold} dBm, 超时={LostTimeoutSeconds}s", LogLevel.Info);
        Log?.Invoke("扫描中… 下方“实时信号”会列出所有收到的 iBeacon, 目标手机显示为绿色", LogLevel.Info);
    }

    public void Stop()
    {
        StopInternal();
        Log?.Invoke("监听已停止", LogLevel.Info);
    }

    private void StopInternal()
    {
        _checkTimer.Stop();
        if (_watcher != null)
        {
            _watcher.Received -= OnReceived;
            try { _watcher.Stop(); } catch { /* 已停止 */ }
            _watcher = null;
        }
        _rssiAvg = double.MinValue;
        _lastSeen = DateTimeOffset.MinValue;
        _belowSince = DateTimeOffset.MaxValue;
        _locked = false;
        _requireSignalRecovery = false;
        _everSeen = false;
        lock (_sync) { _beacons.Clear(); }
        ProximityState.Write(false);   // 停止监听时告诉 CP: 不在附近, 别自动解锁
        LockStateChanged?.Invoke(false);
    }

    /// <summary>给 UI 的实时信号快照 (只返回目标手机)</summary>
    public List<BeaconInfo> Snapshot()
    {
        var now = DateTimeOffset.Now;
        lock (_sync)
        {
            if (!string.IsNullOrWhiteSpace(SeedHex))
            {
                // 滚动模式: UUID 每分钟在变, 不按字典键过滤, 直接用聚合的 EMA/最后收到时间
                if (!_everSeen) return new List<BeaconInfo>();
                return new List<BeaconInfo>
                {
                    new(TargetUuid, (int)Math.Round(_rssiAvg), (now - _lastSeen).TotalSeconds, true)
                };
            }
            return _beacons
                .Where(kv => kv.Key == TargetUuid)
                .Select(kv => new BeaconInfo(
                    kv.Key, (int)Math.Round(kv.Value.Avg),
                    (now - kv.Value.Last).TotalSeconds, true))
                .ToList();
        }
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        foreach (var section in args.Advertisement.ManufacturerData)
        {
            if (section.CompanyId != AppleCompanyId) continue;
            var data = new byte[section.Data.Length];
            using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(section.Data))
            {
                reader.ReadBytes(data);
            }
            if (data.Length < 23 || data[0] != 0x02 || data[1] != 0x15) continue;

            // 蓝牙 UUID 是大端字节序, 不能用 new Guid(byte[]) (它会反转前 3 组)
            var uuid = GuidFromBigEndian(data.AsSpan(2, 16));
            var rssi = args.RawSignalStrengthInDBm;
            var now = DateTimeOffset.Now;

            bool isTarget;
            double avg;
            lock (_sync)
            {
                avg = _beacons.TryGetValue(uuid, out var v) ? v.Avg : double.MinValue;
                avg = avg == double.MinValue ? rssi : avg * 0.7 + rssi * 0.3;
                _beacons[uuid] = (avg, now);
                isTarget = IsTarget(uuid);
            }

            if (isTarget)
            {
                _everSeen = true;
                _lastSeen = now;
                _rssiAvg = avg;
                if (_requireSignalRecovery)
                {
                    _requireSignalRecovery = false;
                    Log?.Invoke("[重新武装] 重新看到手机信号, 自动锁屏恢复判定", LogLevel.Ok);
                }
                RssiUpdated?.Invoke((int)Math.Round(_rssiAvg));
            }
            return;
        }
    }

    private void Evaluate()
    {
        var now = DateTimeOffset.Now;

        // 每轮都把"是否在附近"写出去, 给 Credential Provider 读 (方案C 自动解锁)。
        // 注意: near 必须 = 信号新鲜 且 RSSI 达到阈值。
        // 只看"3 秒内有信号"的话, 手机在远处 (比如 -80) 广播照样收得到,
        // CP 会认为人在旁边, 锁屏瞬间就被自动解锁 → 看起来像"永远锁不上"。
        var fresh = (now - _lastSeen).TotalSeconds <= 3;
        var nearby = fresh && _rssiAvg != double.MinValue && _rssiAvg >= RssiThreshold;
        ProximityState.Write(nearby);

        // 预热期: 从没收到过目标 Beacon 时不能判定"丢失", 否则一启动就锁屏
        if (!_everSeen)
        {
            return;
        }

        // 情况 1: 完全收不到广播 (手机蓝牙关了/离太远/系统休眠)
        var lostSeconds = (now - _lastSeen).TotalSeconds;
        var lost = lostSeconds > LostTimeoutSeconds;

        // 情况 2: 信号持续低于阈值
        if (!lost && _rssiAvg != double.MinValue && _rssiAvg < RssiThreshold)
        {
            if (_belowSince == DateTimeOffset.MaxValue) _belowSince = now;
        }
        else
        {
            _belowSince = DateTimeOffset.MaxValue;
        }
        var belowSeconds = _belowSince == DateTimeOffset.MaxValue
            ? 0 : (now - _belowSince).TotalSeconds;

        var shouldLock = (lost || belowSeconds >= GraceSeconds) && !_requireSignalRecovery;

        // 防卡死 (只武装一次): 锁屏后用户手动解锁回了桌面, 但信号一直没回来 ——
        // 此时用户人在电脑前 (密码都输了), 不能反复锁屏。
        // 解除武装, 直到 OnReceived 重新看到目标信号才恢复。
        if (_locked && (lost || belowSeconds >= GraceSeconds)
            && (now - _lastLockAt).TotalSeconds >= 5 && !IsSessionLocked() && !_requireSignalRecovery)
        {
            _locked = false;
            _requireSignalRecovery = true;
            Log?.Invoke("[手动解锁] 检测到已回到桌面但手机信号未恢复, " +
                        "暂停自动锁屏 (重新看到手机信号后恢复)", LogLevel.Warn);
        }

        if (shouldLock && !_locked)
        {
            var reason = lost
                ? $"信号丢失 {lostSeconds:F0}s (提示: 手机熄屏后安卓会暂停 BLE 广播, 点亮手机屏幕即可恢复)"
                : $"RSSI={_rssiAvg:F0} 低于阈值 {RssiThreshold} 持续 {belowSeconds:F0}s";
            Log?.Invoke($"[触发锁屏] 原因: {reason}", LogLevel.Error);
            _locked = true;
            _lastLockAt = now;
            LockStateChanged?.Invoke(true);
            LockWorkStationSafe();
        }
        else if (!shouldLock && _locked)
        {
            // 手机回来了 (解锁仍需手动输入密码, 这里只恢复监听状态)
            _locked = false;
            Log?.Invoke($"[信号恢复] RSSI={_rssiAvg:F0} dBm, 手机在附近 (方案A不含自动解锁)", LogLevel.Ok);
        }
    }

    private static void LockWorkStationSafe()
    {
        try
        {
            if (!LockWorkStation())
                Debug.WriteLine("LockWorkStation failed");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"LockWorkStation exception: {ex}");
        }
    }

    /// <summary>LogonUI 进程只在锁屏界面运行, 用它判断当前是否处于锁屏状态</summary>
    private static bool IsSessionLocked()
    {
        try
        {
            return Process.GetProcessesByName("LogonUI").Length > 0;
        }
        catch
        {
            return false;   // 查不到就当没锁, 保守处理 (5 秒防抖兜底)
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    /// <summary>把蓝牙大端 UUID 字节流转成 Guid。注意不能用 new Guid(byte[]), 它会把前 3 组按小端反转。</summary>
    private static Guid GuidFromBigEndian(ReadOnlySpan<byte> b)
    {
        Span<char> hex = stackalloc char[32];
        for (var i = 0; i < 16; i++)
        {
            b[i].TryFormat(hex.Slice(i * 2, 2), out _, "x2");
        }
        // Guid 字符串与蓝牙字节流同序: 8-4-4-4-12
        return new Guid($"{hex[0..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}");
    }

    public void Dispose() => StopInternal();
}
