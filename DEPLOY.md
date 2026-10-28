# 真机部署指南（物理机 + 蓝牙 + 真手机）

> **推荐方式（v0.2 起）**：打开 ProximityLock.exe，勾选「手机靠近自动解锁」，
> 在弹窗里输入 Windows 登录密码、UAC 点「是」即可完成全部安装（CP DLL 内嵌在
> EXE 里，自动注册 COM + 加密凭据 + 设权限）。取消勾选即卸载。
> 下面是手动方式，用于排查或无 GUI 场景。

> 前提：方案C (Credential Provider) 已在虚拟机中用 v6 测试通过。
> 真机安装前请先看完「风险与回退」一节。

## 风险与回退

- CP 写崩 = 登录界面卡死。回退手段（按优先级）：
  1. 开机 F8 / 强制中断进**安全模式** → 运行 `cp\uninstall-cp.cmd`
  2. WinRE 命令行手动删注册表（见 cp\README.md「离线救援」一节）
- 部署前确认：机器上有**另一个可用管理员账号**或记得 WinRE 救援步骤。
- **只支持本地账号**。Microsoft 账号（邮箱登录）走不同的凭据序列化，
  本 CP 的 KerbInteractiveLogon 对它无效——MSA 机器请勿安装，或先建本地账号。

## 前置检查

| 检查项 | 怎么查 | 要求 |
|---|---|---|
| 账号类型 | 设置→账户 | 本地账号（非邮箱 MSA） |
| 蓝牙 | 任务栏托盘 | PC 与手机都已开启蓝牙 |
| 手机广播 | ProximityLock APP | 打开自动广播 + 已加电池白名单 |
| PC 端程序 | ProximityLock.exe (Release) | 能看到目标手机 RSSI |

## 部署步骤

把整个项目目录拷到真机（至少 `cp\` 和 `pc-app\bin\Release\net8.0-windows10.0.19041.0\`）：

```powershell
# 1. 管理员 PowerShell, 进入 cp 目录
cd <项目目录>\cp

# 2. 安装 CP (复制 DLL / 设 ACL / 写注册表 / 备份旧键)
.\install-cp.cmd        # 双击也行

# 3. 录入真机登录凭据 (Domain 用 . 表示本机, 或填计算机名)
.\setup-credential.cmd  # 按提示输入 用户名 / 域 / 密码

# 4. 确认没有调试开关残留
Test-Path C:\ProgramData\ProximityLock\noautologon.flag   # 必须是 False
# 若存在: Remove-Item C:\ProgramData\ProximityLock\noautologon.flag

# 5. 先跑 cptest 自测 (不碰系统)
.\cptest.cmd            # 数量=1 自动登录=TRUE 且 GetSerialization OK 才继续
```

```powershell
# 6. 启动 PC 端托盘程序 (真机蓝牙扫描 → state.ini)
<项目目录>\pc-app\bin\Release\net8.0-windows10.0.19041.0\ProximityLock.exe
# 面板显示目标手机 RSSI 后, 点「开始监听」, 再开「开机自启」
```

## 验收流程

1. 手机带在身上，离开电脑 → 3 秒后自动锁屏（方案A 行为）
2. **锁屏状态下**人回到电脑旁 → RSSI 回到阈值以上 → 几秒内自动回桌面
3. 锁屏后检查日志：`C:\ProgramData\ProximityLock\cp.log`
   - `GetCredentialCount: 数量=1 自动登录=TRUE`
   - `ReportResult: status=0x00000000`（成功）
4. 故意把手机蓝牙关掉再锁屏 → 应**停留**在锁屏界面（proximity 判定不通过，正确拒绝）
5. 冷启动开机登录 → **不应**被自动登录（LOGON 场景 + 无交互会话 LUID 判定）

## 常见问题

| 现象 | 原因 | 处理 |
|---|---|---|
| 锁屏不自动解锁 | state.ini 没写 | 检查 ProximityLock.exe 是否在监听、目录 Users 写权限 |
| 锁屏不自动解锁 | ts 过期 (>6s) | 托盘程序被休眠挂起；看 state.ini 的 ts 是否在跳 |
| 自动解锁但回到锁屏 | 密码错 | `ReportResult` 非 0，重跑 setup-credential；注意密码改过没 |
| 完全没反应 | noautologon.flag 残留 | 删除该文件 |
| 密码改了 | creds.bin 是旧的 | 重新跑 setup-credential.cmd |

## 安全提示

- `creds.bin` 是机器范围 DPAPI 加密：**本机管理员可以解密**。个人电脑可用，
  公司域机/共用机不建议启用。
- 想临时停用自动解锁：`New-Item C:\ProgramData\ProximityLock\noautologon.flag -Force`
  （磁贴仍在但不自动登录），恢复则删除该文件。
