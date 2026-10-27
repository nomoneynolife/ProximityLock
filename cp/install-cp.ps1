# 安装 ProximityLock Credential Provider
# WARNING: 只在虚拟机里测试! 出问题时: 安全模式 -> 运行 uninstall-cp.ps1
# 必须以管理员身份运行
# 兼容 Windows PowerShell 5.1 和 PowerShell 7+

param(
    [string]$DllPath = ""
)

$ErrorActionPreference = "Stop"

# PowerShell 7.3+ 默认把 native 命令的非零退出码当成终止错误,
# 这里保持传统行为 (我们自己检查 ExitCode), 否则 reg.exe 之类失败会直接中断脚本。
if ($PSVersionTable.PSVersion.Major -ge 7) {
    $PSNativeCommandUseErrorActionPreference = $false
}

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

# ---------- 前置检查 ----------
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
$clsidKey = "HKLM:\SOFTWARE\Classes\CLSID\$guid"
$cpKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\$guid"

Write-Host "== ProximityLock Credential Provider 安装 ==" -ForegroundColor Cyan
Write-Host ("PowerShell 版本: " + $PSVersionTable.PSVersion)

# 纯 PowerShell 导出注册表键 (不用 reg.exe: 首次安装时键不存在会报错中断, 且依赖外部进程)
function Backup-RegKeys {
    param([string[]]$KeyPaths, [string]$OutFile)
    $entries = New-Object System.Collections.Generic.List[object]
    foreach ($kp in $KeyPaths) {
        $psPath = "Registry::$kp"
        if (-not (Test-Path $psPath)) { continue }
        $root = Get-Item $psPath
        $all = @($root) + @(Get-ChildItem $psPath -Recurse -ErrorAction SilentlyContinue)
        foreach ($k in $all) {
            $values = New-Object System.Collections.Generic.List[object]
            foreach ($n in $k.GetValueNames()) {
                $kind = $k.GetValueKind($n).ToString()
                $v = $k.GetValue($n)
                if ($kind -eq "Binary") { $v = [Convert]::ToBase64String([byte[]]$v) }
                $values.Add([pscustomobject]@{ Name = $n; Kind = $kind; Value = $v })
            }
            $entries.Add([pscustomobject]@{ Path = $k.Name; Values = $values })
        }
    }
    if ($entries.Count -eq 0) { return $false }
    # 手工拼数组, 避免单元素时 ConvertTo-Json 退化成对象
    $parts = $entries | ForEach-Object { $_ | ConvertTo-Json -Depth 6 -Compress }
    Set-Content -Path $OutFile -Value ("[" + ($parts -join ",") + "]") -Encoding UTF8
    return $true
}

# ---------- 1. 备份旧注册表 (首次安装时键不存在, 跳过) ----------
$backup = Join-Path $ScriptDir ("cp-registry-backup-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".json")
$backupKeys = @(
    "HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\$guid",
    "HKEY_LOCAL_MACHINE\SOFTWARE\Classes\CLSID\$guid"
)
$hadOld = (Test-Path $cpKey) -or (Test-Path $clsidKey)
if ($hadOld) {
    try {
        if (Backup-RegKeys -KeyPaths $backupKeys -OutFile $backup) {
            Write-Host ("[1/5] 旧注册表已备份: " + $backup)
        }
        else {
            Write-Host "[1/5] 旧键存在但没导内容 (空键), 继续安装" -ForegroundColor DarkGray
        }
    }
    catch {
        Write-Host ("[1/5] 备份失败, 继续安装: " + $_.Exception.Message) -ForegroundColor Yellow
    }
}
else {
    Write-Host "[1/5] 首次安装, 没有旧注册表项可备份 (跳过)" -ForegroundColor DarkGray
}

# ---------- 2. 准备数据目录 (DLL / 状态 / 凭据) ----------
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
try {
    Copy-Item $DllPath $targetDll -Force
}
catch {
    Write-Host "[ERROR] 复制 DLL 失败 (文件可能被 LogonUI 占用)" -ForegroundColor Red
    Write-Host ("        " + $_.Exception.Message)
    Write-Host "        先跑 .\uninstall-cp.ps1 并重启, 再重新安装" -ForegroundColor Yellow
    exit 1
}
Write-Host "[2/5] DLL 已复制到 $targetDll"

# ---------- 3. 数据目录 ACL: Users 可写状态, Administrators/SYSTEM 完全控制 ----------
try {
    $acl = Get-Acl $dataDir
    $acl.SetAccessRuleProtection($true, $false)  # 断开继承, 用显式授权
    foreach ($id in @("SYSTEM", "Administrators")) {
        $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
            $id, "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
        $acl.AddAccessRule($rule) | Out-Null
    }
    $usersRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        "Users", "Modify", "ContainerInherit,ObjectInherit", "None", "Allow")
    $acl.AddAccessRule($usersRule) | Out-Null
    Set-Acl -Path $dataDir -AclObject $acl
    Write-Host "[3/5] 目录权限已设置 (Users 可写状态文件)"
}
catch {
    Write-Host ("[WARN] 目录权限设置失败: " + $_.Exception.Message) -ForegroundColor Yellow
    Write-Host "       托盘程序可能写不了 state.ini, 需要手动给 Users 写权限" -ForegroundColor Yellow
}

# ---------- 4. 注册 COM 组件 ----------
New-Item -Path $clsidKey -Force | Out-Null
New-Item -Path "$clsidKey\InprocServer32" -Force | Out-Null
# 默认值用 Set-Item 设置 (比 Set-ItemProperty -Name "(Default)" 在各版本更一致)
Set-Item -Path "$clsidKey\InprocServer32" -Value $targetDll
Set-ItemProperty -Path "$clsidKey\InprocServer32" -Name "ThreadingModel" -Value "Both"
Set-Item -Path $clsidKey -Value "ProximityLock Credential Provider"
Write-Host "[4/5] COM 组件已注册 (CLSID $guid)"

# ---------- 5. 加入凭据提供程序列表 ----------
New-Item -Path $cpKey -Force | Out-Null
Set-Item -Path $cpKey -Value "ProximityLock"
Write-Host "[5/5] 已加入凭据提供程序列表"

# ---------- 自检 ----------
Write-Host ""
$dllOk = Test-Path $targetDll
$clsidOk = Test-Path "$clsidKey\InprocServer32"
$cpOk = Test-Path $cpKey
$dllValue = if ($clsidOk) { (Get-Item "$clsidKey\InprocServer32").GetValue("") } else { "" }

Write-Host "== 自检 ==" -ForegroundColor Cyan
Write-Host ("  DLL 落地        : " + $(if ($dllOk) { "OK  $targetDll" } else { "缺失" })) -ForegroundColor $(if ($dllOk) { "Green" } else { "Red" })
Write-Host ("  CLSID\\InprocServer32: " + $(if ($clsidOk) { "OK  -> $dllValue" } else { "缺失" })) -ForegroundColor $(if ($clsidOk) { "Green" } else { "Red" })
Write-Host ("  Credential Providers: " + $(if ($cpOk) { "OK" } else { "缺失" })) -ForegroundColor $(if ($cpOk) { "Green" } else { "Red" })

if (-not ($dllOk -and $clsidOk -and $cpOk)) {
    Write-Host "[ERROR] 安装不完整, 先跑 uninstall-cp.ps1 再重试" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "安装完成。下一步:" -ForegroundColor Green
Write-Host "  1) .\setup-credential.ps1 -User $env:USERNAME -Domain .   (录入登录密码)"
Write-Host "  2) 另开窗口 .\simulate-phone.ps1 -KeepNear                (模拟手机在附近)"
Write-Host "  3) Win+L 锁屏, 应在几秒内自动回到桌面"
Write-Host ""
Write-Host "卸载: .\uninstall-cp.ps1 (或双击 uninstall-cp.cmd, 安全模式也能跑)" -ForegroundColor Yellow
