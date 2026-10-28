using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ProximityLock;

/// <summary>
/// 一键部署/卸载自动解锁组件 (Credential Provider)。
/// 普通权限进程不能直接装 (要写 HKLM), 所以由 EXE 自我提权 (runas) 再跑一遍,
/// 提权实例带 --install-cp / --uninstall-cp 参数, 在 App.OnStartup 里调 RunChild。
/// </summary>
public static class CpInstaller
{
    private const string Clsid = "{8D2A5E11-3C7B-4F1E-9A64-5C0E2B7D9F31}";
    private static readonly string DataDir = @"C:\ProgramData\ProximityLock";
    private static readonly string DllTarget = Path.Combine(DataDir, "ProximityCredentialProvider.dll");
    private static readonly string CredsFile = Path.Combine(DataDir, "creds.bin");
    private static readonly string FlagFile = Path.Combine(DataDir, "noautologon.flag");
    private static readonly string ResultFile = Path.Combine(DataDir, "install-result.txt");
    private static readonly string ClsidKeyPath = $@"SOFTWARE\Classes\CLSID\{Clsid}";
    private static readonly string CpKeyPath =
        $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{Clsid}";

    public static bool IsInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(CpKeyPath);
        return key != null;
    }

    // ==================== 提权子进程入口 ====================

    /// <summary>在提权实例里执行, 结果写 install-result.txt 供原进程读取</summary>
    public static int RunChild(string[] args)
    {
        bool install = args.Any(a => a.Equals("--install-cp", StringComparison.OrdinalIgnoreCase));
        bool uninstall = args.Any(a => a.Equals("--uninstall-cp", StringComparison.OrdinalIgnoreCase));
        if (!install && !uninstall) return 2;

        string credsTemp = args.SkipWhile(a => !a.Equals("--creds", StringComparison.OrdinalIgnoreCase))
                               .Skip(1).FirstOrDefault() ?? "";

        string result;
        try
        {
            if (install) result = DoInstall(credsTemp);
            else result = DoUninstall();
        }
        catch (Exception ex)
        {
            result = "FAIL " + ex.Message;
        }

        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(ResultFile, result);
        }
        catch { /* 写不进去就算了, 上层按失败处理 */ }
        return result.StartsWith("OK", StringComparison.Ordinal) ? 0 : 1;
    }

    private static string DoInstall(string credsTemp)
    {
        var steps = new List<string>();

        // 1. 数据目录 + ACL (Users 可写 state.ini, SYSTEM/Admin 全控)
        Directory.CreateDirectory(DataDir);
        var icacls = Run("icacls.exe",
            $"\"{DataDir}\" /inheritance:r /grant:r \"SYSTEM:(OI)(CI)F\" \"Administrators:(OI)(CI)F\" \"Users:(OI)(CI)M\"");
        if (!icacls.Contains("成功处理", StringComparison.Ordinal) &&
            !icacls.Contains("Successfully processed", StringComparison.OrdinalIgnoreCase) &&
            icacls.Trim().Length > 0)
        {
            // icacls 中文/英文系统输出不同, 只要退出码 0 就算过; 这里只记录
            steps.Add($"icacls: {icacls.Trim()}");
        }

        // 2. 从 EXE 资源里释放 CP DLL
        byte[] dll = ExtractEmbeddedDll();
        try
        {
            File.WriteAllBytes(DllTarget, dll);
        }
        catch (IOException)
        {
            // LogonUI 正占用旧 DLL (刚锁过屏): 旧文件还在原地且注册表指向它, 继续用
            steps.Add("DLL 被占用, 保留旧版本");
        }

        // 3. 注册 COM + 凭据提供程序
        using (var k = Registry.LocalMachine.CreateSubKey(ClsidKeyPath))
            k.SetValue(null, "ProximityLock Credential Provider");
        using (var k = Registry.LocalMachine.CreateSubKey(ClsidKeyPath + @"\InprocServer32"))
        {
            k.SetValue(null, DllTarget);
            k.SetValue("ThreadingModel", "Both");
        }
        using (var k = Registry.LocalMachine.CreateSubKey(CpKeyPath))
            k.SetValue(null, "ProximityLock");

        // 4. 写凭据 (DPAPI 机器范围): user\ndomain\npassword
        if (credsTemp.Length == 0 || !File.Exists(credsTemp))
            return "FAIL 没拿到凭据临时文件";
        string[] parts = File.ReadAllText(credsTemp).Split('\n');
        if (parts.Length < 3 || parts[0].Length == 0 || parts[2].Length == 0)
            return "FAIL 凭据内容不完整";
        try { File.Delete(credsTemp); } catch { }
        byte[] plain = Encoding.UTF8.GetBytes($"{parts[0]}\n{parts[1]}\n{parts[2]}");
        byte[] cipher = DpapiProtect(plain);
        using (var fs = new FileStream(CredsFile, FileMode.Create, FileAccess.Write))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write(cipher.Length);   // 4 字节小端长度
            bw.Write(cipher);
        }
        // creds.bin 收紧权限: 只留 SYSTEM / Administrators (目录级 Users Modify 会继承下来)
        Run("icacls.exe", $"\"{CredsFile}\" /inheritance:r /grant:r \"SYSTEM:F\" \"Administrators:F\"");

        // 5. 清掉调试开关
        try { if (File.Exists(FlagFile)) File.Delete(FlagFile); } catch { }

        return "OK " + (steps.Count > 0 ? string.Join("; ", steps) : "安装完成");
    }

    private static string DoUninstall()
    {
        // 只删注册表键 (CP 立即失效, 即使 DLL 被占用也删得掉), 凭据一并清掉
        try { Registry.LocalMachine.DeleteSubKeyTree(CpKeyPath, false); } catch { }
        try { Registry.LocalMachine.DeleteSubKeyTree(ClsidKeyPath, false); } catch { }
        try { if (File.Exists(CredsFile)) File.Delete(CredsFile); } catch { }
        return "OK 已卸载 (DLL 文件留着不影响, 想彻底清理可删 " + DataDir + ")";
    }

    // ==================== 普通权限侧: 触发 UAC 提权 ====================

    /// <summary>
    /// 自我提权重跑 EXE 执行安装。password 通过用户私有 TEMP 里的临时文件传递,
    /// 提权实例读完即删。返回 null = 成功, 否则错误信息 (含用户取消 UAC)。
    /// </summary>
    public static async Task<string?> InstallAsync(string user, string domain, string password)
    {
        string temp = Path.Combine(Path.GetTempPath(), "proximitylock-" + Guid.NewGuid().ToString("N") + ".tmp");
        await File.WriteAllTextAsync(temp, $"{user}\n{domain}\n{password}");
        try
        {
            return await ElevateAsync($"--install-cp --creds \"{temp}\"");
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }

    public static async Task<string?> UninstallAsync()
    {
        return await ElevateAsync("--uninstall-cp");
    }

    private static async Task<string?> ElevateAsync(string arguments)
    {
        try { File.Delete(ResultFile); } catch { }

        var psi = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? "ProximityLock.exe",
            Arguments = arguments,
            UseShellExecute = true,
            Verb = "runas",   // 触发 UAC; 用户取消会抛 Win32Exception
        };

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Win32Exception ex)
        {
            return $"已取消: {ex.Message}";
        }
        if (proc == null) return "提权进程启动失败";

        await Task.Run(() => { proc.WaitForExit(120_000); });

        if (!File.Exists(ResultFile)) return "提权进程没有返回结果 (安装未完成)";
        var result = await File.ReadAllTextAsync(ResultFile);
        return result.StartsWith("OK", StringComparison.Ordinal) ? null : result["FAIL ".Length..].Trim();
    }

    // ==================== 工具 ====================

    private static byte[] ExtractEmbeddedDll()
    {
        var asm = typeof(CpInstaller).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("ProximityCredentialProvider.dll", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("EXE 里没找到内嵌的 CP DLL (编译产物问题)");
        using var s = asm.GetManifestResourceStream(name)!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static string Run(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            return p?.StandardOutput.ReadToEnd() ?? "";
        }
        catch { return ""; }
    }

    // ---------- DPAPI (crypt32, 机器范围) ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDescr,
        IntPtr pEntropy, IntPtr pvReserved, IntPtr pPrompt, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private const int CRYPTPROTECT_LOCAL_MACHINE = 0x4;

    private static byte[] DpapiProtect(byte[] plain)
    {
        var pin = GCHandle.Alloc(plain, GCHandleType.Pinned);
        try
        {
            var inBlob = new DATA_BLOB { cbData = plain.Length, pbData = pin.AddrOfPinnedObject() };
            if (!CryptProtectData(ref inBlob, "ProximityLock", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_LOCAL_MACHINE, out var outBlob))
                throw new InvalidOperationException($"DPAPI 加密失败 (err={Marshal.GetLastWin32Error()})");
            try
            {
                var buf = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, buf, 0, buf.Length);
                return buf;
            }
            finally
            {
                LocalFree(outBlob.pbData);
            }
        }
        finally
        {
            pin.Free();
        }
    }
}
