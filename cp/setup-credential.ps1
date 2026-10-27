# 录入自动解锁用的 Windows 账户凭据
# DPAPI 机器范围加密 (SYSTEM 可解, 需要管理员权限写 ProgramData)
#
# 用法:
#   .\setup-credential.ps1 -User administrator -Domain .
#   .\setup-credential.ps1 -SelfTest        # 自检 DPAPI 是否可用 (不写任何文件)
#
# 说明: 这里直接 P/Invoke crypt32!CryptProtectData, 不走 System.Security.ProtectedData。
# 原因: PowerShell 7 (.NET) 里 ProtectedData 是独立 NuGet 包, 默认没加载,
#       会报 "找不到类型 [Security.Cryptography.ProtectedData]"; PS 5.1 才有 System.Security.dll。
# P/Invoke 两个版本都能跑, 且与 CP 端 C++ 的 CryptUnprotectData 完全对应。

param(
    [string]$User = "",
    [string]$Domain = "",
    [switch]$SelfTest
)

$ErrorActionPreference = "Stop"
$dataDir = "C:\ProgramData\ProximityLock"
$credsFile = Join-Path $dataDir "creds.bin"
$CRYPTPROTECT_LOCAL_MACHINE = 0x4

# ---------- DPAPI ----------
if (-not ("ProximityLock.DpapiNative" -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace ProximityLock
{
    public static class DpapiNative
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct DATA_BLOB
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CryptProtectData(
            ref DATA_BLOB pDataIn, string szDataDescr, IntPtr pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CryptUnprotectData(
            ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

        [DllImport("kernel32.dll")]
        public static extern IntPtr LocalFree(IntPtr hMem);
    }
}
'@
}

function Invoke-Dpapi {
    param(
        [byte[]]$Data,
        [switch]$Unprotect
    )

    $in = New-Object ProximityLock.DpapiNative+DATA_BLOB
    # out 参数是值类型 struct, 必须先建好零值实例, 传 $null 会报"无法将空值转换为类型"
    $out = New-Object ProximityLock.DpapiNative+DATA_BLOB
    $in.cbData = $Data.Length
    $in.pbData = [Runtime.InteropServices.Marshal]::AllocHGlobal($Data.Length)

    try {
        [Runtime.InteropServices.Marshal]::Copy($Data, 0, $in.pbData, $Data.Length)

        if ($Unprotect) {
            $ok = [ProximityLock.DpapiNative]::CryptUnprotectData(
                [ref]$in, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]::Zero,
                $CRYPTPROTECT_LOCAL_MACHINE, [ref]$out)
        }
        else {
            $ok = [ProximityLock.DpapiNative]::CryptProtectData(
                [ref]$in, $null, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]::Zero,
                $CRYPTPROTECT_LOCAL_MACHINE, [ref]$out)
        }

        if (-not $ok) {
            $err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
            throw ("DPAPI 调用失败, Win32 错误码 " + $err + " (0x" + $err.ToString("X") + ")")
        }

        $result = New-Object byte[] $out.cbData
        [Runtime.InteropServices.Marshal]::Copy($out.pbData, $result, 0, $out.cbData)
        return $result
    }
    finally {
        if ($in.pbData -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::FreeHGlobal($in.pbData)
        }
        if ($null -ne $out -and $out.pbData -ne [IntPtr]::Zero) {
            [ProximityLock.DpapiNative]::LocalFree($out.pbData) | Out-Null
        }
    }
}

# ---------- 自检 ----------
if ($SelfTest) {
    Write-Host "== DPAPI 自检 ==" -ForegroundColor Cyan
    Write-Host ("PowerShell 版本: " + $PSVersionTable.PSVersion)
    $probe = [Text.Encoding]::UTF8.GetBytes("proximitylock-selftest-probe")
    try {
        $enc = Invoke-Dpapi -Data $probe
        $dec = Invoke-Dpapi -Data $enc -Unprotect
        $roundTrip = ([Text.Encoding]::UTF8.GetString($dec) -eq "proximitylock-selftest-probe")
        Write-Host ("加密: " + $probe.Length + " 字节 -> " + $enc.Length + " 字节")
        Write-Host ("解密回环: " + $(if ($roundTrip) { "OK" } else { "不一致" })) -ForegroundColor $(if ($roundTrip) { "Green" } else { "Red" })
        if ($roundTrip) { Write-Host "[OK] DPAPI 可用, 可以直接跑 -User 录入凭据" -ForegroundColor Green; exit 0 }
        exit 1
    }
    catch {
        Write-Host ("[ERROR] " + $_.Exception.Message) -ForegroundColor Red
        exit 1
    }
}

# ---------- 正常流程 ----------
if ([string]::IsNullOrWhiteSpace($User)) {
    Write-Host "[ERROR] 必须指定 -User (例如 -User administrator -Domain .)" -ForegroundColor Red
    exit 1
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "[ERROR] 请用管理员身份运行" -ForegroundColor Red
    exit 1
}

# 本地账户 domain 可用 "." 或留空; 微软账户一般用计算机名或邮箱
if ([string]::IsNullOrWhiteSpace($Domain)) {
    $Domain = $env:COMPUTERNAME
    Write-Host ("未指定 -Domain, 默认使用计算机名: " + $Domain)
}

Write-Host "== 录入自动解锁凭据 ==" -ForegroundColor Cyan
Write-Host ("账户: " + $Domain + "\" + $User)
Write-Host "注意: 密码会用 DPAPI(机器范围) 加密存到 $credsFile" -ForegroundColor Yellow
Write-Host "      本机管理员/系统账户可解密, 等同于把密码存在本机, 自行权衡" -ForegroundColor Yellow

$secure = Read-Host "输入 Windows 登录密码" -AsSecureString
if ($null -eq $secure -or $secure.Length -eq 0) {
    Write-Host "[ERROR] 密码为空。自动解锁必须有密码 (空密码账户无法用这种方式登录)" -ForegroundColor Red
    exit 1
}

$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try {
    $plainPwd = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
}

New-Item -ItemType Directory -Force -Path $dataDir | Out-Null

# 明文格式: user \n domain \n password (与 CP 端解析一致)
$plain = $User + "`n" + $Domain + "`n" + $plainPwd
$plainPwd = $null
[GC]::Collect()

$bytes = [Text.Encoding]::UTF8.GetBytes($plain)
$plain = $null

try {
    $blob = Invoke-Dpapi -Data $bytes
}
catch {
    Write-Host ("[ERROR] 加密失败: " + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
[Array]::Clear($bytes, 0, $bytes.Length)

# 落盘格式: 4 字节长度(小端) + 密文, 与 CP 端读取一致
$out = New-Object System.Collections.Generic.List[byte]
$out.AddRange([BitConverter]::GetBytes([int]$blob.Length))
$out.AddRange($blob)
[IO.File]::WriteAllBytes($credsFile, $out.ToArray())

# 收紧权限: 只留 SYSTEM / Administrators
$acl = Get-Acl $credsFile
$acl.SetAccessRuleProtection($true, $false)
foreach ($id in @("SYSTEM", "Administrators")) {
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        $id, "FullControl", "None", "None", "Allow")
    $acl.AddAccessRule($rule)
}
Set-Acl -Path $credsFile -AclObject $acl

# 回读自检: 确认密文能被解密且内容完整 (不解出明文到屏幕)
try {
    $readBack = [IO.File]::ReadAllBytes($credsFile)
    $len = [BitConverter]::ToInt32($readBack, 0)
    $payload = New-Object byte[] $len
    [Array]::Copy($readBack, 4, $payload, 0, $len)
    $verify = Invoke-Dpapi -Data $payload -Unprotect
    $verifyText = [Text.Encoding]::UTF8.GetString($verify)
    $lines = $verifyText -split "`n"
    $verifyOk = ($lines.Count -ge 3 -and $lines[0] -eq $User -and $lines[1] -eq $Domain -and $lines[2].Length -gt 0)
    [Array]::Clear($verify, 0, $verify.Length)
    $verifyText = $null
    if (-not $verifyOk) {
        Write-Host "[WARN] 回读校验异常, 密文结构可能不对, 建议重跑" -ForegroundColor Yellow
    }
}
catch {
    Write-Host ("[WARN] 回读校验失败: " + $_.Exception.Message) -ForegroundColor Yellow
}

Write-Host ""
Write-Host ("[OK] 凭据已加密写入: " + $credsFile) -ForegroundColor Green
Write-Host ("     " + $out.Count + " 字节 (4 头部 + " + $blob.Length + " 密文)")
Write-Host ""
Write-Host "下一步:" -ForegroundColor Cyan
Write-Host "  1) .\simulate-phone.ps1 -KeepNear   (另开一个管理员窗口, 持续模拟手机在附近)"
Write-Host "  2) Win+L 锁屏, 手机'在附近'时应在几秒内自动回到桌面"
