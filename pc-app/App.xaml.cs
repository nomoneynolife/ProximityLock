using System.Windows;

namespace ProximityLock;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    public static bool StartMinimized { get; private set; }
    public static bool AutoListen { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 单实例: 已经跑着就直接退
        _singleInstanceMutex = new Mutex(true, @"Global\ProximityLock_SingleInstance", out var createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show("ProximityLock 已经在运行了 (看右下角托盘图标)。",
                "ProximityLock", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // --minimized / --silent: 静默启动 (藏托盘)
        // --listen: 开机自启专用, 启动后自动开始监听; 手动打开 exe 调试不自动监听
        StartMinimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)
                                      || a.Equals("--silent", StringComparison.OrdinalIgnoreCase));
        AutoListen = e.Args.Any(a => a.Equals("--listen", StringComparison.OrdinalIgnoreCase));
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
