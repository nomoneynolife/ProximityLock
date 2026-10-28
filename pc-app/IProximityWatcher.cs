namespace ProximityLock;

public enum LogLevel { Info, Ok, Warn, Error }

/// <summary>
/// 接近监听器接口。连接模式 (GattWatcher): 与配对手机维持 GATT 长连接,
/// 连着 = 人在旁边 (state.ini 写 near=1, 锁屏界面自动解锁), 断开 = 锁屏。
/// </summary>
public interface IProximityWatcher : IDisposable
{
    event Action<string, LogLevel>? Log;
    event Action<bool>? LockStateChanged;

    /// <summary>断开后的防抖秒数</summary>
    int LostTimeoutSeconds { get; set; }

    /// <summary>GATT 连接是否在线</summary>
    bool IsConnected { get; }

    /// <summary>设备显示名 (日志/UI 用)</summary>
    string DeviceName { get; }

    void Start();
    void Stop();
}
