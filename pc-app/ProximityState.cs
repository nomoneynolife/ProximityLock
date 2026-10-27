using System.IO;

namespace ProximityLock;

/// <summary>
/// 把"手机是否在附近"写给 Credential Provider 读 (方案C 自动解锁用)。
/// 路径: C:\ProgramData\ProximityLock\state.ini, 格式: near=1/0 + ts=unix秒
/// </summary>
public static class ProximityState
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProximityLock");

    private static readonly string FilePath = Path.Combine(Dir, "state.ini");

    public static void Write(bool near)
    {
        try
        {
            if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
            var content = $"near={(near ? 1 : 0)}\nts={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}\n";
            File.WriteAllText(FilePath, content);
        }
        catch
        {
            // 没有写权限就算了, 不影响锁屏主功能
        }
    }
}
