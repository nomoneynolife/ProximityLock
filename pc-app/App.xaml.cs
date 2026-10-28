using System.Windows;

namespace ProximityLock;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    public static bool StartMinimized { get; private set; }
    public static bool AutoListen { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 提权子进程模式: --install-cp / --uninstall-cp
        // 装完/卸完写结果文件就退出, 不进主界面, 不受单实例 Mutex 影响
        var args = e.Args;
        if (args.Any(a => a.Equals("--install-cp", StringComparison.OrdinalIgnoreCase))
            || args.Any(a => a.Equals("--uninstall-cp", StringComparison.OrdinalIgnoreCase)))
        {
            int code = CpInstaller.RunChild(args);
            Shutdown(code);
            return;
        }

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
        StartMinimized = args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)
                                      || a.Equals("--silent", StringComparison.OrdinalIgnoreCase));
        AutoListen = args.Any(a => a.Equals("--listen", StringComparison.OrdinalIgnoreCase));

        // App.xaml 不再设 StartupUri, 这里手动创建 (给子进程模式留出提前退出的机会)
        new MainWindow().Show();
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
