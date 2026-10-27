# 自动解锁 (方案C) — Credential Provider

> ⚠️ **只在虚拟机里装！** CP 写崩会导致登录界面异常，恢复要进安全模式。
> 装之前先给 VM 打快照。

## 原理

```
手机(iBeacon广播)
      ↓ BLE
ProximityLock.exe (托盘程序, 用户会话)
      ↓ 每秒写 ${ProgramData}\ProximityLock\state.ini  (near=1/0, ts=unix秒)
ProximityCredentialProvider.dll (LogonUI 加载, SYSTEM)
      ↓ 读 state.ini: near=1 且 ts 在 6 秒内 → 有凭据
      ↓ GetCredentialCount(..., pbAutoLogon=TRUE)
Windows 自动提交 creds.bin 里的凭据 → 进桌面
```

关键点两个：
1. `pbAutoLogon=TRUE` 让 LogonUI 自动登录，不用点任何东西
2. 锁屏时手机已离开（CP 查到 near=0 不给凭据），手机回来后靠后台线程轮询 + `CredentialsChanged` 通知 LogonUI 重新拉取

## 文件

| 文件 | 作用 |
|---|---|
| `ProximityCredentialProvider.cpp` | CP 源码 (COM, ICredentialProvider + Credential2) |
| `build.bat` | 编译 (需 VS + C++ 工作负载) |
| `install-cp.ps1` | 管理员安装: 复制 DLL、设 ACL、写注册表、备份 reg |
| `uninstall-cp.ps1` | 卸载 (安全模式也能跑) |
| `setup-credential.ps1` | 录入账户密码, DPAPI 机器范围加密成 creds.bin |

## VM 测试步骤

```powershell
# 0. 打快照! 然后开管理员 PowerShell
cd <项目>\cp
.\build.bat                       # 编译 DLL
.\install-cp.ps1                  # 安装 (会备份注册表)

# 1. 录入凭据 (VM 里的登录账户)
.\setup-credential.ps1 -User 用户名 -Domain .    # 本地账户 Domain 用 . 或计算机名

# 2. 模拟"手机在附近": 手动写状态文件 (真实环境由托盘程序自动写)
$dir = "C:\ProgramData\ProximityLock"
"near=1`nts=$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())" | Set-Content "$dir\state.ini" -Encoding ascii

# 3. Win+L 锁屏, 观察是否自动回到桌面
```

真实环境不用手动写 state.ini：`ProximityLock.exe` 一边监听一边每秒刷新它。

## 出问题怎么救

1. 登录界面异常 → 重启进**安全模式** → 管理员 PowerShell → `.\uninstall-cp.ps1`
2. 还不行 → 用安装时生成的 `cp-registry-backup-*.reg` 恢复注册表
3. 最坏情况 → VM 回滚快照（所以必须在 VM 里玩）

## 已知限制 / 待验证

- `pbAutoLogon` 在不同 Windows 版本行为略有差异，需要实测（本次只验证编译，未在真机运行）
- 账户必须是**密码登录**：Windows Hello PIN 是硬件绑定的，第三方拿不到，必须录真实密码
- 微软账户: `-Domain` 填邮箱或用计算机名，视登录方式而定
- 域账户: `-Domain` 填域 NetBIOS 名
- 解锁用的 LUID 靠 `LsaEnumerateLogonSessions` 找当前控制台会话，多用户同时登录时可能取错，需要时再精化
- **安全等价性**: creds.bin 是 DPAPI 机器范围加密，本机管理员可解密 = 密码以可解形式存在本机。自己权衡要不要用

## 下一步

- [ ] VM 实测 `pbAutoLogon` 流程
- [ ] 托盘程序加"自动解锁"开关 + 密码录入 UI（省掉手工跑脚本）
- [ ] 失败兜底: 自动登录失败 3 次就彻底禁用，避免锁死
