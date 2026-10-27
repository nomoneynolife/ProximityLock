# Proximity Lock — 手机靠近解锁 / 离开锁屏 (方案A)

手机当 BLE iBeacon 不停广播, PC 端监听信号强度: 手机离开蓝牙范围 → Windows 自动锁屏。
**方案A 只做"离开锁屏", 靠近解锁需手动输密码** (自动解锁要上 Credential Provider, 后续版本)。

## 结构

```
pc-app/       C# WPF, .NET 8, 监听 iBeacon RSSI + LockWorkStation
android-app/  Kotlin, 前台服务持续广播 iBeacon
```

## 使用步骤

1. **安卓端**: 安装 `android-app/app/build/outputs/apk/debug/app-debug.apk`
   - 打开 App → 允许蓝牙广播 + 通知权限 → 点"开始广播"
   - 保持 App 广播运行 (前台服务常驻, 通知栏可见)
2. **PC 端**: 运行 `pc-app/bin/Release/net8.0-windows10.0.19041.0/ProximityLock.exe`
   - UUID 保持默认 (两端一致) → 点"开始监听"
   - 看到 RSSI 数值跳动说明链路通了
3. **测试**: 把手机拿远 (或关手机蓝牙), 约 5~8 秒后 PC 自动锁屏

## 参数说明 (PC 端)

| 参数 | 默认 | 说明 |
|---|---|---|
| Beacon UUID | E2C56DB5-... | 两端必须一致 |
| RSSI 阈值 | -65 dBm | 平滑后 RSSI 低于该值判定"离开", 约 2-3 米。越接近 -100 灵敏度越低 (要离更远才锁) |
| 信号丢失超时 | 8 秒 | 完全收不到广播超过该时间锁屏 (兜底)。低至 1 秒可调, 但手机熄屏省电会暂停广播, 设太紧容易误锁 |

> 说明: 走远锁屏主要靠 **RSSI 阈值**路径 (低于阈值持续 3 秒); "信号丢失"只是兜底, 因为安卓熄屏后系统省电可能暂停 BLE 广播。安卓端建议点一次"申请电池白名单"。

信号变差有 3 秒宽限期 (GraceSeconds, 代码内), 防止偶尔丢包误锁。

## UUID 自定义 (两端)

- **随机**: PC 端点"随机"生成; 安卓端点"随机"生成 (两端要么都随机再互抄, 要么一端生成复制给另一端)
- **复制**: 一键拷 UUID, 方便跨设备粘贴
- **保存固化**: PC 存到 `%APPDATA%\ProximityLock\config.json`, 安卓存到 SharedPreferences, 下次开机自动加载; 监听/广播运行中保存会自动用新 UUID 重启
- **恢复默认**: 一键回到 E2C56DB5-DFFB-48D2-B060-D0F5A71096E0 并固化

## 注意事项

- PC 需要有蓝牙 (台式机可插个 USB 蓝牙适配器, 4.0+ 即可)
- 部分国产 ROM (MIUI/EMUI 等) 锁屏后限制后台广播, 建议给 App 加电池白名单 / 锁定后台
- Windows 锁屏后蓝牙扫描可能暂停, 若休眠后不触发, 检查电源计划的蓝牙设置
- 解锁时手动输密码即可; 手机回来后 PC 端日志会提示"信号恢复"

## 构建命令

```bash
# PC 端
cd pc-app && dotnet build -c Release

# 安卓端 (JAVA_HOME 指向 Android Studio 自带 JBR)
cd android-app && gradle assembleDebug
```

## 后续升级路线 (方案B/C)

- **B**: 接入 Windows Hello 伴侣设备框架 (CDF), 手机靠近弹确认一键解锁
- **C**: 自写 Credential Provider, 完全无感解锁 (风险高, 动手前先做好系统还原点)
