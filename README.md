# ProximityLock
   
[如果你在使用这个软件则默认触发诅咒，吾爱破解论坛的**风之暇想**是傻逼 ](https://www.52pojie.cn/?243467)
   
手机靠近解锁 Windows、离开自动锁屏 —— 蓝牙 (BLE iBeacon) 接近感知。

安卓手机变成一个 iBeacon 信标，PC 端托盘程序监听信号强度：

- **手机离开** → 信号变弱/丢失 → 自动锁屏 (Win+L 等效)
- **手机靠近** → 自动输入凭据回到桌面 (可选，Windows 凭据提供程序实现)
- **滚动 UUID** → 广播标识每 60 秒更换 (HMAC 派生)，空口抓包无法伪造

## 组成

```
android-app/   安卓端 (Kotlin)      前台服务持续广播 iBeacon, 支持滚动 UUID
pc-app/        PC 端 (.NET 8 WPF)   BLE 扫描 / 锁屏判定 / 自动解锁一键部署 / 托盘
cp/            凭据提供程序 (C++)   Windows 登录界面自动输入凭据的 COM 组件
DEPLOY.md      真机部署手册 (含故障排查)
cp/README.md   凭据提供程序排查手册 (含 WinRE 离线救援)
```

## 功能一览

| 功能 | 说明 |
|---|---|
| 离开锁屏 | RSSI 低于阈值持续 3 秒，或信号完全丢失超过可配置超时 (默认 8s) |
| 靠近自动解锁 | 锁屏界面凭据磁贴自动提交 (需勾选「手机靠近自动解锁」一键安装) |
| 信号面板 | 实时显示目标手机 RSSI，绿 = 在附近，橙 = 信号弱 (会锁屏) |
| 滚动 UUID | 两端共享 16 字节种子，UUID 每分钟按 HMAC-SHA256 派生更换 |
| 一键部署 | CP DLL 内嵌进 EXE，勾选复选框 + 输入密码 + UAC 即完成安装/卸载 |
| 托盘常驻 | 单实例、开机自启 (静默 + 自动监听)、最小化到托盘 |

## 快速开始

### 安卓端

1. 安装 `ProximityLock-v0.2-滚动UUID.apk` (或自行编译 `android-app/`)
2. 打开 App → 点「开始广播」→ 建议申请电池白名单 (防熄屏断广播)
3. (可选) PC 端生成种子后，粘贴到「滚动 UUID 种子」框 → 粘贴保存

### PC 端

1. 运行 `pc-app/bin/Release/.../ProximityLock.exe`
2. 点「开始监听」，面板出现绿色目标手机即连通
3. (可选) 开启自动解锁：
   - 勾选「手机靠近自动解锁」→ 输入 Windows 登录密码 → UAC 点「是」
   - 之后锁屏状态下手机靠近即自动进桌面；取消勾选即卸载
4. 建议开启「开机自启动」

### 自行编译

- PC 端：`dotnet build pc-app/ProximityLock.csproj -c Release` (.NET 8 SDK)
- 安卓端：`JAVA_HOME=<JDK21> gradle assembleDebug` (Gradle 8.7)
- 凭据提供程序：`cp/build.bat` (MSVC，产物 DLL 已内嵌进 PC 端 EXE)

## 注意事项 (务必阅读)

1. **仅支持本地账号**。微软邮箱账号 (MSA) 走不同的凭据序列化，自动解锁对它无效
2. **密码存本机**：自动解锁会把登录密码用 DPAPI (机器范围) 加密存到
   `C:\ProgramData\ProximityLock\creds.bin`，仅 SYSTEM/Administrators 可读。
   本机管理员级恶意软件可解密 —— 个人电脑使用，办公/共用机不建议开启自动解锁
3. **BLE 不是距离证明**：滚动 UUID 防住了"抓包伪造"，但理论上仍可被实时中继
   (需要专业设备蹲点)。家用场景足够，高安全需求场景请依赖密码 + Windows Hello
4. **有总开关**：取消勾选即卸载自动解锁组件；也可创建
   `C:\ProgramData\ProximityLock\noautologon.flag` 临时停用 (不卸载)
5. **锁屏卡死救援**：安全模式运行 `cp/uninstall-cp.cmd`，或按 `cp/README.md`
   的 WinRE 离线救援步骤删除注册表键
6. 换了 Windows 密码后：取消勾选再重新勾选，录入新密码
7. 熄屏后安卓系统可能暂停 BLE 广播：给 App 加电池白名单，丢失超时别设太短

## 故障排查

- PC 端日志在主窗口下方 (彩色)，CP 端日志在 `C:\ProgramData\ProximityLock\cp.log`
- 常见问题对照表见 `DEPLOY.md` 和 `cp/README.md`

## License

[MIT](LICENSE)
