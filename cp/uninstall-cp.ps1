# 卸载 ProximityLock Credential Provider (安全模式下也能跑)
# 必须以管理员身份运行

$ErrorActionPreference = "Continue"
$guid = "{8D2A5E11-3C7B-4F1E-9A64-5C0E2B7D9F31}"
$dataDir = "C:\ProgramData\ProximityLock"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "[ERROR] 请用管理员身份运行" -ForegroundColor Red
    exit 1
}

Write-Host "== 卸载 Credential Provider ==" -ForegroundColor Cyan

# 1. 从凭据提供程序列表移除 (最关键, 先去这个)
Remove-Item -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\$guid" -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "[1/3] 已从凭据提供程序列表移除"

# 2. 注销 COM
Remove-Item -Path "HKLM:\SOFTWARE\Classes\CLSID\$guid" -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "[2/3] COM 注册已清理"

# 3. 删 DLL (凭据/状态文件保留, 手动删目录即可)
$dll = Join-Path $dataDir "ProximityCredentialProvider.dll"
if (Test-Path $dll) { Remove-Item $dll -Force -ErrorAction SilentlyContinue }
Write-Host "[3/3] DLL 已删除 (状态/凭据文件保留在 $dataDir)"

Write-Host ""
Write-Host "卸载完成。重启或注销后生效。" -ForegroundColor Green
