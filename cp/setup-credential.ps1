# 录入自动解锁用的 Windows 账户凭据
# DPAPI 机器范围加密 (SYSTEM 可解, 需要管理员权限写 ProgramData)
# 用法: 管理员运行 .\setup-credential.ps1 -User 你的用户名 [-Domain 域或计算机名]

param(
    [Parameter(Mandatory = $true)][string]$User,
    [string]$Domain = ""
)

$ErrorActionPreference = "Stop"
$dataDir = "C:\ProgramData\ProximityLock"
$credsFile = Join-Path $dataDir "creds.bin"

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
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try {
    $plainPwd = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
} finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
}

if ([string]::IsNullOrEmpty($plainPwd)) {
    Write-Host "[ERROR] 密码为空" -ForegroundColor Red
    exit 1
}

New-Item -ItemType Directory -Force -Path $dataDir | Out-Null

# 明文格式: user \n domain \n password (与 CP 端解析一致)
$plain = $User + "`n" + $Domain + "`n" + $plainPwd
$plainPwd = $null
[GC]::Collect()

$bytes = [Text.Encoding]::UTF8.GetBytes($plain)
$blob = [Security.Cryptography.ProtectedData]::Protect(
    $bytes, $null, [Security.Cryptography.DataProtectionScope]::LocalMachine)

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

Write-Host ""
Write-Host ("[OK] 凭据已加密写入: " + $credsFile) -ForegroundColor Green
Write-Host "现在锁屏 (Win+L) 测试: 手机在附近应在几秒内自动回到桌面" -ForegroundColor Green
