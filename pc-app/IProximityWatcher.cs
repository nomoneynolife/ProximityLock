namespace ProximityLock;

/// <summary>
/// 接近监听器的公共接口, 广播模式 (BeaconWatcher) 和连接模式 (GattWatcher) 可互换。
/// 两者都内部完成: 接近判定 → 写 state.ini (给 CP 自动解锁用) + 触发 LockWorkStation。
/// </summary>
public interface IProximityWatcher : IDisposable
{
    event Action<string, LogLevel>? Log;
    event Action<int>? RssiUpdated;

    Guid TargetUuid { get; set; }
    int RssiThreshold { get; set; }
    int LostTimeoutSeconds { get; set; }

    /// <summary>滚动 UUID 种子 (32位hex), 空 = 固定 UUID 模式</summary>
    string? SeedHex { get; set; }

    void Start();
    void Stop();

    /// <summary>给 UI 的实时信号快照 (只含目标手机)</summary>
    List<BeaconInfo> Snapshot();
}
