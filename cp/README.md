# 自动解锁 (方案C) — Credential Provider

> ⚠️ **只在虚拟机里装！** CP 写崩会让登录界面卡死，恢复要么进 WinRE 删注册表，要么回滚快照。

## 原理

```
手机(iBeacon广播)
      ↓ BLE
ProximityLock.exe (托盘程序, 用户会话)
      ↓ 每秒写 C:\ProgramData\ProximityLock\state.ini  (near=1/0, ts=unix秒)
ProximityCredentialProvider.dll (LogonUI 加载, SYSTEM)
      ↓ 读 state.ini: near=1 且 ts 在 6 秒内
      ↓ GetCredentialCount 返回 1 个凭据 + pbAutoLogon=TRUE
Windows 自动提交 creds.bin 里的凭据 → 进桌面
```

**同时接 CPUS_LOGON（1）和 CPUS_UNLOCK_WORKSTATION（2）** 两个场景：Win10/11 锁屏（Win+L）后 LogonUI 实测用 **CPUS_LOGON** 加载 CP（老系统才是 UNLOCK 场景）。用"目标用户是否已有交互会话"区分真解锁与冷启动开机——LOGON 场景查不到交互会话 LUID = 冷启动开机，不介入，所以**开机登录不会被自动登录**。

## 文件

| 文件 | 作用 |
|---|---|
| `ProximityCredentialProvider.cpp` | CP 源码 |
| `cptest.cpp` / `cptest.exe` | **不注册、不进登录界面**，直接加载 DLL 跑一遍调用链的自测工具 |
| `build.bat` | 编译 DLL + cptest.exe |
| `install-cp.ps1` / `.cmd` | 安装: 复制 DLL、设 ACL、写注册表、备份旧键 |
| `uninstall-cp.ps1` / `.cmd` | 卸载（安全模式也能跑） |
| `setup-credential.ps1` / `.cmd` | 录入密码, DPAPI(机器范围) 加密成 creds.bin |
| `simulate-phone.ps1` / `.cmd` | VM 没蓝牙时模拟手机靠近/离开 |
| `install-cp.cmd` / `setup-credential.cmd` / `uninstall-cp.cmd` / `simulate-phone.cmd` | 双击即跑，自动绕过执行策略 |

## 正确的测试顺序（别跳步）

### 第 0 步：命令行自测（不碰系统）

```powershell
cd C:\Users\Administrator\Desktop\cp
.\build.bat                      # 编译 DLL + cptest
.\setup-credential.ps1 -User administrator -Domain .
.\simulate-phone.ps1 -KeepNear   # 窗口 A，保持运行
.\cptest.cmd                     # 窗口 B
```

`cptest` 输出正常的样子：

```
[1] LoadLibrary 成功
DllGetClassObject            : 0x00000000 OK
CreateInstance               : 0x00000000 OK
SetUsageScenario             : 0x00000000 OK
GetFieldDescriptorCount      : 3 个字段
    field 0: type=4   (EDIT_TEXT)
    field 1: type=5   (PASSWORD_TEXT)
    field 2: type=9   (SUBMIT_BUTTON)
GetCredentialCount           : OK   数量=1  自动登录=TRUE
SetSelected                  : FALSE          <- 必须是 FALSE
GetSerialization             : OK   响应=2 (已返回凭据)
    序列化包长度 : 114 字节
    认证包 ID    : 0            <- Negotiate 的合法 ID, 0 不是错误
    UserName     : len=26 在包内=是
    DomainName   : len=2  在包内=是
    Password     : len=22 在包内=是
```

**这一步不通就不要装。** 常见失败与对策：

| cptest 结果 | 原因 | 怎么办 |
|---|---|---|
| `GetCredentialCount 数量=0` | near 状态过期 / creds 解不开 / LUID 找不到 | 先确保 `simulate-phone -KeepNear` 在跑；看 cp.log 里的 `EnsureCache` 行 |
| `GetSerialization 响应=1` | 凭据包构造失败 | 看 cp.log `PackLogonBuffer failed` |
| LoadLibrary 失败 | 路径不对 / 32 位 DLL | 用绝对路径；确认是 x64 |

### 第 1 步：只显示磁贴，不自动登录（关键的分阶段验证）

```powershell
New-Item -ItemType File "C:\ProgramData\ProximityLock\noautologon.flag" -Force
.\install-cp.cmd
```

然后锁屏（保持 `simulate-phone -KeepNear` 在跑），**观察界面是否流畅**：

- **界面正常，出现预填了账户密码的磁贴** → CP 加载没问题，可以进第 2 步
- **界面卡死 / 空白 / 转圈** → CP 在 LogonUI 里本身有问题，立刻看 `cp.log`，然后卸载

### 第 2 步：开自动登录

```powershell
Remove-Item "C:\ProgramData\ProximityLock\noautologon.flag"
```

锁屏，应该在几秒内自动回桌面。

失败后会熔断（`ReportResult` 收到非 0 状态就禁止再次自动重试），所以不会像以前那样无限循环把界面拖死。

## 登录界面卡死怎么救

**症状**：锁屏后界面冻结，鼠标键盘无响应。

1. **VM 强制断电重启**（VMware：虚拟机 → 电源 → 关闭电源）
2. 重启后能进系统 → 管理员 PowerShell → 项目目录 → `.\uninstall-cp.cmd`
3. 重启后又卡在登录界面 → 进 WinRE 删注册表：
   - VMware 里强制断电 2~3 次，Windows 会自动进"自动修复"
   - 或挂 Windows ISO 启动 → 修复计算机 → 疑难解答 → 高级选项 → 命令提示符
   ```bat
   reg load HKLM\OFFLINE C:\Windows\System32\config\SOFTWARE
   reg delete "HKLM\OFFLINE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{8D2A5E11-3C7B-4F1E-9A64-5C0E2B7D9F31}" /f
   reg delete "HKLM\OFFLINE\Classes\CLSID\{8D2A5E11-3C7B-4F1E-9A64-5C0E2B7D9F31}" /f
   reg unload HKLM\OFFLINE
   shutdown -r -t 0
   ```
4. 最快：**恢复快照**（所以装之前一定先打快照）

## 日志：卡在哪一步一目了然

`C:\ProgramData\ProximityLock\cp.log`（超过 2MB 自动停止写入）

正常流程长这样：

```
=== DLL_PROCESS_ATTACH pid=1234 ===
--- CProvider created ---
SetUsageScenario: 1                     <- Win11 锁屏解锁走 LOGON 场景 (1), 正常
EnsureCache: user=Administrator domain=. luidOk=1 luid=0:660752
GetCredentialCount: near=1 -> 1 credential, autologon=1 (flag=0 blocked=0)
SetSelected -> FALSE                    <- 必须是 FALSE
GetSerialization: OK unlock=1 user=Administrator pkg=0 bytes=114
ReportResult: status=0x00000000 sub=0x00000000   <- 0 = 登录成功
```

| 日志现象 | 含义 |
|---|---|
| 没有 `DLL_PROCESS_ATTACH` | CP 没被加载：注册表没写对，或不是解锁场景 |
| `SetUsageScenario: 1` 后没有 `GetCredentialCount` | v2 及更早版本才有的问题：只认场景 2，Win11 锁屏走场景 1 被跳过。**换 v3 DLL** |
| `LOGON scenario but no existing session (cold boot) -> 0` | 冷启动开机保护生效，不介入，正常 |
| `luidOk=0` | 找不到交互会话 LUID → 不给凭据。核对 creds 里的用户名与登录名是否一致 |
| `near=0 -> 0 credentials` | state.ini 不新鲜（simulate 没在跑，或超过 6 秒） |
| `autologon=0 (blocked=1)` | 之前失败过，已熔断，本次不自动登录（重新登录一次 LogonUI 会复位） |
| `ReportResult: status=0xC000006A` | 密码错误（STATUS_WRONG_PASSWORD） |
| `ReportResult: status=0xC0000064` | 账户不存在（STATUS_NO_SUCH_USER） |
| 卡在 `CredentialsChanged` 之后没有下文 | 后台线程通知 LogonUI 刷新时挂住了 |

## VM 里没有蓝牙怎么触发

**不需要蓝牙。** 蓝牙只在"托盘程序 → state.ini"这一段用到，CP 只认 state.ini。

| 验证内容 | 在哪做 | 怎么做 |
|---|---|---|
| 真实 BLE / RSSI / 阈值 / 离开锁屏 | **宿主机** | 跑 `ProximityLock.exe` + 手机广播 |
| CP 自动登录 | **VM** | `simulate-phone.cmd -KeepNear` 写状态文件 |

```powershell
.\simulate-phone.cmd -Status     # 看当前 near/ts 和判定结果
.\simulate-phone.cmd -Near       # 模拟靠近 (写一次, 6 秒有效)
.\simulate-phone.cmd -KeepNear   # 持续模拟靠近 (推荐)
.\simulate-phone.cmd -Away       # 模拟离开
```

## v3 修了什么（针对"锁屏了但没解锁"）

| # | 问题 | 修法 |
|---|---|---|
| 1 | `GetCredentialCount` 只认 `CPUS_UNLOCK_WORKSTATION`；**Win10/11 锁屏后 LogonUI 实测用 `CPUS_LOGON`（场景 1）加载 CP**，所以锁屏后 CP 一直返回 0 个凭据，watcher 触发了 `CredentialsChanged` 也没用 | 两个场景都接 |
| 2 | 无法区分"LOGON 场景的解锁"和"冷启动开机登录" | LOGON 场景 + `luidOk=0`（目标用户还没有交互会话）= 冷启动，不介入；有会话 = 真解锁，正常给凭据 |
| 3 | 序列化类型按 UI 场景判定，Win11 的"LOGON 场景解锁"会拿不到 UNLOCK_LOGON | 改为按 LUID 判定：拿到会话 LUID 就用 `KERB_INTERACTIVE_UNLOCK_LOGON`，与场景无关 |

宿主机实测（cptest，免注册）：
- UNLOCK 场景：1 凭据 + autologon=1 + GetSerialization OK
- LOGON 场景 + 已有会话（= Win11 锁屏）：1 凭据 + autologon=1 ✓
- LOGON 场景 + 无会话（= 冷启动）：0 凭据，日志打印 `cold boot -> 0` ✓

## v2 修了什么（针对"登录界面卡死"）

| # | 问题 | 后果 | 修法 |
|---|---|---|---|
| 1 | `SetSelected` 也返回 `pbAutoLogon=TRUE` | 与 `GetCredentialCount` 的 TRUE 双重触发；凭据不被接受就"提交→失败→重新枚举→再提交"死循环，界面直接卡死 | 改为 FALSE，只让 `GetCredentialCount` 控制 |
| 2 | `ReportResult` 不处理失败 | 密码错 → 无限重试 | 失败即熔断 `_autoLogonBlocked` |
| 3 | 每次枚举都做 DPAPI 解密 + `LsaEnumerateLogonSessions` | LogonUI 频繁枚举 → UI 线程累积阻塞，表现为卡顿/卡死 | 缓存，只做一次 |
| 4 | 开机登录场景也介入 | 可能进不去系统 | 只在 `CPUS_UNLOCK_WORKSTATION` 提供凭据 |
| 5 | `GetFieldState` 给字段设 `CPFIS_FOCUSED` | 自动登录时抢焦点，异常 | 全部 `CPFIS_NONE` |
| 6 | `GetSubmitButtonValue` 指向密码框，但没有 SUBMIT_BUTTON 字段 | 描述符与实现不一致 | 补真正的 `CPFT_SUBMIT_BUTTON` |
| 7 | 无日志 | 完全黑盒 | 加 `cp.log` |
| 8 | LUID 只按"控制台会话+用户名"匹配 | VM 里会话编号对不上就永远找不到 | 三级回退：本进程会话 → 控制台会话 → 任意交互会话 |

## 兼容性

脚本同时支持 **Windows PowerShell 5.1** 和 **PowerShell 7+**：

- 注册表备份用纯 PowerShell 导出 JSON，不调 `reg.exe`
- PS 7.3+ 的 `$PSNativeCommandUseErrorActionPreference` 已显式关掉
- 凭据加密 P/Invoke `CryptProtectData`，不依赖 `System.Security.ProtectedData`

## 报错 "在此系统上禁止运行脚本"

```powershell
# 1) 最省事: 双击对应 .cmd
# 2) 当前窗口临时放行
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
# 3) 单次执行
powershell -NoProfile -ExecutionPolicy Bypass -File .\install-cp.ps1
```

## 已知限制

- 账户必须是**密码登录**：Windows Hello PIN 是硬件绑定的，第三方拿不到，必须录真实密码
- 微软账户: `-Domain` 填邮箱或计算机名；域账户填域 NetBIOS 名
- `creds.bin` 是 DPAPI 机器范围加密，本机管理员可解密 —— 等于密码以可解形式存在本机
- 自动登录失败一次即熔断，需要重新进一次登录界面才会重试
