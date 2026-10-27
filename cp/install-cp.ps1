# 安装 ProximityLock Credential Provider
# WARNING: 只在虚拟机里测试! 出问题时: 安全模式 -> 运行 uninstall-cp.ps1
# 必须以管理员身份运行

param(
    [string]$DllPath = ""
)

$ErrorActionPreference = "Stop"

# 定位脚本所在目录。
# 注意: 直接运行 .\install-cp.ps1 时 $PSScriptRoot 有值;
# 如果把脚本内容复制粘贴到控制台执行, $PSScriptRoot 是空的 (会报 Join-Path 绑定失败),
# 这里回退到当前工作目录, 保证两种方式都能跑。
$ScriptDir = $PSScriptRoot
if (-not $ScriptDir -and $MyInvocation.MyCommand.Path) {
    $ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
if (-not $ScriptDir) {
    $ScriptDir = (Get-Location).Path
}

if (-not $DllPath) {
    $DllPath = Join-Path $ScriptDir "ProximityCredentialProvider.dll"
}

# 管理员检查
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "[ERROR] 请用管理员身份运行 PowerShell" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $DllPath)) {
    Write-Host "[ERROR] 找不到 DLL: $DllPath" -ForegroundColor Red
    Write-Host "        先运行 build.bat 编译; 或在 cp 目录下用 .\install-cp.ps1 运行" -ForegroundColor Red
    Write-Host "        (不要复制粘贴脚本内容到控制台, 那样取不到脚本目录)" -ForegroundColor DarkGray
    exit 1
}

$guid = "{8D2A5E11-3C7B-4F1E-9A64-5C0E2B7D9F31}"
$dataDir = "C:\ProgramData\ProximityLock"
$targetDll = Join-Path $dataDir "ProximityCredentialProvider.dll"

Write-Host "== ProximityLock Credential Provider 安装 ==" -ForegroundColor Cyan

# 1. 备份相关注册表
$backup = Join-Path $ScriptDir ("cp-registry-backup-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".reg")
reg export "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\$guid" $backup /y 2>$null | Out-Null
Write-Host ("[1/5] 注册表已备份: " + $backup)

# 2. 准备数据目录 (放 DLL / 状态 / 凭据)
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
Copy-Item $DllPath $targetDll -Force
Write-Host "[2/5] DLL 已复制到 $targetDll"

# 3. 数据目录 ACL: Users 可写 (托盘程序要写状态), Administrators/SYSTEM 完全控制
$acl = Get-Acl $dataDir
$acl.SetAccessRuleProtection($true, $false)  # 断开继承, 显式授权
foreach ($id in @("SYSTEM", "Administrators")) {
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        $id, "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
    $acl.AddAccessRule($rule)
}
$usersRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    "Users", "Modify", "ContainerInherit,ObjectInherit", "None", "Allow")
$acl.AddAccessRule($usersRule)
Set-Acl -Path $dataDir -AclObject $acl
Write-Host "[3/5] 目录权限已设置 (Users 可写状态文件)"

# 4. 注册 COM 组件
New-Item -Path "HKLM:\SOFTWARE\Classes\CLSID\$guid" -Force | Out-Null
New-Item -Path "HKLM:\SOFTWARE\Classes\CLSID\$guid\InprocServer32" -Force | Out-Null
Set-ItemProperty -Path "HKLM:\SOFTWARE\Classes\CLSID\$guid\InprocServer32" -Name "(Default)" -Value $targetDll
Set-ItemProperty -Path "HKLM:\SOFTWARE\Classes\CLSID\$guid\InprocServer32" -Name "ThreadingModel" -Value "Both"
Set-ItemProperty -Path "HKLM:\SOFTWARE\Classes\CLSID\$guid" -Name "(Default)" -Value "ProximityLock Credential Provider"
Write-Host "[4/5] COM 组件已注册"

# 5. 注册到凭据提供程序列表
$cpKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\$guid"
New-Item -Path $cpKey -Force | Out-Null
Set-ItemProperty -Path $cpKey -Name "(Default)" -Value "ProximityLock"
Write-Host "[5/5] 已加入凭据提供程序列表"

Write-Host ""
Write-Host "安装完成。下一步:" -ForegroundColor Green
Write-Host "  1) 管理员运行: .\setup-credential.ps1   (录入登录密码, DPAPI 加密)"
Write-Host "  2) 测试状态文件: 'near=1`nts=<当前unix时间>' 写入 C:\ProgramData\ProximityLock\state.ini"
Write-Host "  3) Win+L 锁屏, 应自动登录回桌面"
Write-Host ""
Write-Host "卸载: 管理员运行 .\uninstall-cp.ps1" -ForegroundColor Yellow
