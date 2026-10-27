# 模拟手机靠近/离开 (VM 里没有蓝牙时用这个测 Credential Provider)
#
# Credential Provider 本身不读蓝牙, 它只读状态文件:
#   C:\ProgramData\ProximityLock\state.ini   (near=1/0 + ts=unix秒, 6 秒内算新鲜)
# 所以 VM 里不需要蓝牙, 只要往这个文件写对内容就能触发自动解锁。
#
# 用法 (管理员 PowerShell, 在 cp 目录):
#   .\simulate-phone.ps1 -Near         # 模拟手机在附近 (只写一次, 6 秒后失效)
#   .\simulate-phone.ps1 -KeepNear     # 持续模拟"一直在附近", Ctrl+C 停止 (推荐)
#   .\simulate-phone.ps1 -Away         # 模拟手机离开
#   .\simulate-phone.ps1 -Status       # 看当前状态文件内容和是否有效

param(
    [switch]$Near,
    [switch]$Away,
    [switch]$KeepNear,
    [switch]$Status
)

$ErrorActionPreference = "Stop"
$dataDir = "C:\ProgramData\ProximityLock"
$stateFile = Join-Path $dataDir "state.ini"
$freshSeconds = 6

function Write-State([bool]$near) {
    try {
        if (-not (Test-Path $dataDir)) {
            New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
        }
        $ts = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
        $content = "near=" + [int]$near + "`nts=" + $ts + "`n"
        [IO.File]::WriteAllText($stateFile, $content, [Text.Encoding]::ASCII)
    }
    catch {
        Write-Host "[ERROR] 写状态文件失败: $stateFile" -ForegroundColor Red
        Write-Host "        请用管理员身份运行, 或先跑 install-cp.ps1 建好目录" -ForegroundColor Red
        exit 1
    }
}

function Show-State {
    if (-not (Test-Path $stateFile)) {
        Write-Host "状态文件不存在: $stateFile" -ForegroundColor Yellow
        Write-Host "CP 会判定为'手机不在附近', 不会自动解锁" -ForegroundColor DarkGray
        return
    }
    $text = (Get-Content $stateFile -Raw).Trim()
    $near = ""
    $ts = 0
    foreach ($line in ($text -split "`n")) {
        $line = $line.Trim()
        if ($line -like "near=*") { $near = $line.Substring(5) }
        if ($line -like "ts=*") { [long]::TryParse($line.Substring(3), [ref]$ts) | Out-Null }
    }
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $age = $now - $ts
    $fresh = ($age -le $freshSeconds) -and ($age -ge -60)
    $isNear = ($near -eq "1")

    Write-Host "状态文件: $stateFile" -ForegroundColor Cyan
    Write-Host $text
    Write-Host ""
    Write-Host ("near     = " + $near + $(if ($isNear) { "  (手机在附近)" } else { "  (手机离开)" }))
    Write-Host ("ts 距今  = " + $age + " 秒 (新鲜期 $freshSeconds 秒)")
    $color = if ($fresh -and $isNear) { "Green" } else { "Yellow" }
    $verdict = if ($fresh -and $isNear) { "有效 -> 锁屏后会自动登录" } else { "无效 -> 锁屏后不会自动登录" }
    Write-Host ("判定     = " + $verdict) -ForegroundColor $color
}

Write-Host "== ProximityLock 手机状态模拟 ==" -ForegroundColor Cyan

if ($Status -or (-not $Near -and -not $Away -and -not $KeepNear)) {
    Show-State
    if (-not $Status) {
        Write-Host ""
        Write-Host "用法:" -ForegroundColor DarkGray
        Write-Host "  .\simulate-phone.ps1 -Near       模拟手机靠近 (写一次)" -ForegroundColor DarkGray
        Write-Host "  .\simulate-phone.ps1 -KeepNear   持续模拟靠近, Ctrl+C 停止" -ForegroundColor DarkGray
        Write-Host "  .\simulate-phone.ps1 -Away       模拟手机离开" -ForegroundColor DarkGray
        Write-Host "  .\simulate-phone.ps1 -Status     查看当前状态" -ForegroundColor DarkGray
    }
    exit 0
}

if ($Away) {
    Write-State $false
    Write-Host "[OK] 已写入 near=0 (手机离开): 现在 Win+L 锁屏应该停在登录界面不自动进" -ForegroundColor Yellow
    exit 0
}

if ($Near) {
    Write-State $true
    Write-Host "[OK] 已写入 near=1 (手机在附近), 有效期 $freshSeconds 秒" -ForegroundColor Green
    Write-Host "     6 秒内 Win+L 锁屏即可看到自动登录" -ForegroundColor DarkGray
    exit 0
}

if ($KeepNear) {
    Write-Host "[OK] 正在持续模拟'手机在附近' (每 2 秒刷新时间戳), Ctrl+C 停止" -ForegroundColor Green
    Write-Host "     现在 Win+L 锁屏, 应该几秒内自动回到桌面" -ForegroundColor DarkGray
    try {
        while ($true) {
            Write-State $true
            Write-Host ("  ts 已刷新 " + (Get-Date -Format "HH:mm:ss")) -ForegroundColor DarkGray
            Start-Sleep -Seconds 2
        }
    }
    finally {
        # 停止刷新后时间戳 6 秒自然过期, 恢复"手机不在"的语义
        Write-Host ""
        Write-Host "已停止模拟 (6 秒后状态失效)" -ForegroundColor Yellow
    }
}
