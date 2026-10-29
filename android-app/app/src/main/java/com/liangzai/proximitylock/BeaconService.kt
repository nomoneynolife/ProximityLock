package com.liangzai.proximitylock

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothGatt
import android.bluetooth.BluetoothGattCharacteristic
import android.bluetooth.BluetoothGattDescriptor
import android.bluetooth.BluetoothGattServer
import android.bluetooth.BluetoothGattServerCallback
import android.bluetooth.BluetoothGattService
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothProfile
import android.bluetooth.BluetoothStatusCodes
import android.bluetooth.le.AdvertiseCallback
import android.bluetooth.le.AdvertiseData
import android.bluetooth.le.AdvertiseSettings
import android.content.Intent
import android.os.Build
import android.os.IBinder
import java.util.UUID

/**
 * 前台服务: 发出「可连接」的 BLE 广播, 并运行一个最小 GATT 服务器。
 * PC 端 (已与本机配对) 通过配对缓存连上来, 维持长连接 —
 * 连着 = 人在旁边, 断开 = 人走了。不需要 UUID / RSSI。
 *
 * 反向通道: PC 订阅 CMD 特征值后, 本机可用 notify 立刻下发
 * 「锁定电脑 / 解锁电脑」指令, 复用同一条蓝牙链路, 毫秒级。
 */
class BeaconService : Service() {

    companion object {
        @Volatile
        var isRunning = false
            private set

        /** PC 端是否已连上本机的 GATT 服务器 */
        @Volatile
        var pcConnected = false
            private set

        /** 远程锁定中: 停掉蓝牙广播, PC 重连不上, 保证锁屏不被自动解锁 */
        @Volatile
        var isRemoteLocked = false
            private set

        private const val CHANNEL_ID = "beacon_channel"
        private const val NOTIF_ID = 1

        const val ACTION_SEND_CMD = "com.liangzai.proximitylock.SEND_CMD"
        const val EXTRA_CMD = "cmd"
        const val CMD_LOCK = "LOCK"
        const val CMD_UNLOCK = "UNLOCK"

        // 结果码: 给界面弹提示用
        const val RESULT_OK = 0
        const val RESULT_NOT_RUNNING = 1
        const val RESULT_NO_PC = 2
        const val RESULT_FAILED = 3
        const val RESULT_NEED_ENABLE = 4   // 解锁: 蓝牙被系统限制无法自动开, 需要用户点一下授权框

        // 最小 GATT 服务: Windows 连接后服务发现不为空, 连接更稳
        val SERVICE_UUID: UUID = UUID.fromString("9A660001-C77B-4A9D-B5E3-1D6B7A9E2C11")
        val CHAR_UUID: UUID = UUID.fromString("9A660002-C77B-4A9D-B5E3-1D6B7A9E2C11")
        val CMD_UUID: UUID = UUID.fromString("9A660003-C77B-4A9D-B5E3-1D6B7A9E2C11")

        /** CCCD: notify 特征值必须显式挂上它, Windows 才能订阅通知 */
        val CCCD_UUID: UUID = UUID.fromString("00002902-0000-1000-8000-00805f9b34fb")

        @Volatile
        private var instance: BeaconService? = null

        /** 界面上的锁定/解锁按钮走这里, 同步返回结果码 */
        fun sendCommand(cmd: String): Int {
            val svc = instance ?: return RESULT_NOT_RUNNING
            return svc.executeCommand(cmd)
        }
    }

    private val advertiser by lazy {
        (getSystemService(BLUETOOTH_SERVICE) as BluetoothManager)
            .adapter?.bluetoothLeAdvertiser
    }

    private val btAdapter by lazy {
        (getSystemService(BLUETOOTH_SERVICE) as BluetoothManager).adapter
    }

    /** 蓝牙开关状态: 开机自恢复广播 (关蓝牙/开蓝牙都不会丢服务) */
    private val btStateReceiver = object : android.content.BroadcastReceiver() {
        override fun onReceive(context: android.content.Context?, intent: Intent?) {
            when (intent?.getIntExtra(BluetoothAdapter.EXTRA_STATE, -1)) {
                BluetoothAdapter.STATE_ON -> {
                    // 蓝牙重开 (远程解锁时): 重建 GATT 服务器 + 恢复广播, PC 自动连上 → 解锁
                    try { gattServer?.close() } catch (_: Exception) { }
                    gattServer = null
                    cmdChar = null
                    connectedDevice = null
                    pcConnected = false
                    if (isRunning && !isRemoteLocked) startAdvertising()
                }
                BluetoothAdapter.STATE_OFF, BluetoothAdapter.STATE_TURNING_OFF -> {
                    connectedDevice = null
                    pcConnected = false
                    updateNotification()
                }
            }
        }
    }

    private var gattServer: BluetoothGattServer? = null
    private var cmdChar: BluetoothGattCharacteristic? = null
    private var connectedDevice: BluetoothDevice? = null

    /** 最新一条待取指令 (带时间戳去重), PC 端轮询读取 */
    @Volatile
    private var pendingCmd: ByteArray = "READY".toByteArray()

    private val callback = object : AdvertiseCallback() {
        override fun onStartFailure(errorCode: Int) {
            isRunning = false
            stopSelf()
        }
    }

    override fun onCreate() {
        super.onCreate()
        instance = this
        // 注册蓝牙状态监听 (系统保护广播, 用 NOT_EXPORTED 收)
        androidx.core.content.ContextCompat.registerReceiver(
            this, btStateReceiver,
            android.content.IntentFilter(BluetoothAdapter.ACTION_STATE_CHANGED),
            androidx.core.content.ContextCompat.RECEIVER_NOT_EXPORTED)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        startAsForeground()

        if (intent?.action == ACTION_SEND_CMD) {
            // 只处理指令, 不改动运行状态
            val cmd = intent.getStringExtra(EXTRA_CMD)
            if (cmd != null) executeCommand(cmd)
            return START_STICKY
        }

        startAdvertising()
        isRunning = true
        PrefStore.setWantRunning(this, true)
        RestartReceiver.schedule(this, 20_000L)   // 看门狗: 每 20 秒确认一次存活
        return START_STICKY
    }

    /**
     * 用户从最近任务里划掉 App:
     * 正常 ROM 上前台服务会继续跑; 激进 ROM (vivo/小米等) 会把进程连同服务一起杀。
     * 这里既尝试原地续命, 也安排一个 1 秒后的看门狗 —— 进程真被杀, 闹钟仍会把服务拉回来。
     */
    override fun onTaskRemoved(rootIntent: Intent?) {
        if (PrefStore.isWantRunning(this)) {
            if (!isRunning) {
                startAdvertising()
                isRunning = true
            }
            // 关键: 立刻排一个 1 秒后的看门狗 —— ROM 真把进程杀了, 系统闹钟也会马上把服务拉回
            RestartReceiver.schedule(this, 1_000L)
            // 直接补一次 startService, 有的 ROM 允许同进程自启
            try {
                startService(Intent(this, BeaconService::class.java))
            } catch (_: Exception) { }
        }
        super.onTaskRemoved(rootIntent)
    }

    override fun onDestroy() {
        try { unregisterReceiver(btStateReceiver) } catch (_: Exception) { }
        advertiser?.stopAdvertising(callback)
        gattServer?.close()
        gattServer = null
        cmdChar = null
        connectedDevice = null
        pcConnected = false
        isRunning = false
        if (instance === this) instance = null
        if (!PrefStore.isWantRunning(this)) {
            // 用户主动停止/完全退出: 撤掉看门狗, 别把自己救回来
            RestartReceiver.cancel(this)
        } else {
            // 被动被系统销毁: 尽快排一次看门狗, 1.5 秒后就尝试恢复
            RestartReceiver.schedule(this, 1_500L)
        }
        // 停止时一并撤掉常驻通知 (完全退出时通知栏不留残影)
        if (Build.VERSION.SDK_INT >= 24) {
            stopForeground(STOP_FOREGROUND_REMOVE)
        } else {
            @Suppress("DEPRECATION")
            stopForeground(true)
        }
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    /** 远程锁定中不开广播: 广播一停 PC 就永远重连不上, 锁屏不会被自动解锁 */
    private fun startAdvertising() {
        if (isRemoteLocked) return
        val settings = AdvertiseSettings.Builder()
            .setAdvertiseMode(AdvertiseSettings.ADVERTISE_MODE_LOW_LATENCY)
            .setTxPowerLevel(AdvertiseSettings.ADVERTISE_TX_POWER_HIGH)
            .setConnectable(true)   // PC 端要能连上来 (GATT 长连接)
            .build()

        val data = AdvertiseData.Builder()
            .setIncludeDeviceName(true)   // 广播设备名, 方便在 PC 端识别
            .build()

        advertiser?.stopAdvertising(callback)
        advertiser?.startAdvertising(settings, data, callback)
        startGattServer()
    }

    /** 最小 GATT 服务器: 心跳特征值 (只读) + 指令特征值 (notify) */
    private fun startGattServer() {
        if (gattServer != null) return
        val server = try {
            (getSystemService(BLUETOOTH_SERVICE) as BluetoothManager)
                .openGattServer(this, gattCallback)
        } catch (_: SecurityException) {
            null
        } ?: return

        val service = BluetoothGattService(
            SERVICE_UUID, BluetoothGattService.SERVICE_TYPE_PRIMARY)

        val beat = BluetoothGattCharacteristic(
            CHAR_UUID,
            BluetoothGattCharacteristic.PROPERTY_READ,
            BluetoothGattCharacteristic.PERMISSION_READ)
        beat.value = "ProximityLock-v4".toByteArray()
        service.addCharacteristic(beat)

        // 指令通道: notify 走快速推送; 同时支持 READ 轮询 ——
        // vivo 熄屏省电会让首次 CCCD 订阅一直失败, PC 端读轮询不依赖订阅。
        // 用户点按钮时屏幕必亮, 亮屏下读写事务是通的, 轮询一定能取到指令。
        val cmd = BluetoothGattCharacteristic(
            CMD_UUID,
            BluetoothGattCharacteristic.PROPERTY_NOTIFY or
                BluetoothGattCharacteristic.PROPERTY_READ or
                BluetoothGattCharacteristic.PROPERTY_WRITE_NO_RESPONSE,
            BluetoothGattCharacteristic.PERMISSION_WRITE or
                BluetoothGattCharacteristic.PERMISSION_READ)
        // 关键: notify 特征值必须显式挂 CCCD 描述符, 否则 Windows 端订阅不上
        // (安卓不会自动添加, PC 端会一直显示「控制通道: 准备中」)
        cmd.addDescriptor(BluetoothGattDescriptor(
            CCCD_UUID,
            BluetoothGattDescriptor.PERMISSION_READ or
                BluetoothGattDescriptor.PERMISSION_WRITE))
        cmdChar = cmd
        service.addCharacteristic(cmd)

        server.addService(service)
        gattServer = server
    }

    private val gattCallback = object : BluetoothGattServerCallback() {

        /** 服务注册结果: 失败(个别 ROM 自动加 CCCD 导致冲突等)就原地重建, 保住控制通道 */
        override fun onServiceAdded(status: Int, service: BluetoothGattService?) {
            if (status == BluetoothGatt.GATT_SUCCESS) return
            android.util.Log.w("BeaconService", "addService failed: $status, rebuilding gatt server")
            try {
                gattServer?.close()
            } catch (_: Exception) { }
            gattServer = null
            cmdChar = null
            startGattServer()
        }

        override fun onConnectionStateChange(device: BluetoothDevice?, status: Int, newState: Int) {
            if (newState == BluetoothProfile.STATE_CONNECTED) {
                connectedDevice = device
                pcConnected = true
            } else {
                if (device == null || device.address == connectedDevice?.address) {
                    connectedDevice = null
                    pcConnected = false
                }
            }
            updateNotification()
        }

        override fun onCharacteristicReadRequest(
            device: BluetoothDevice?, requestId: Int,
            offset: Int, characteristic: BluetoothGattCharacteristic?
        ) {
            val value: ByteArray? = when (characteristic?.uuid) {
                CHAR_UUID -> "ProximityLock-v4".toByteArray()
                CMD_UUID -> pendingCmd   // PC 端轮询指令走这里
                else -> null
            }
            gattServer?.sendResponse(
                device, requestId,
                if (value != null) BluetoothGatt.GATT_SUCCESS else BluetoothGatt.GATT_FAILURE,
                0, value)
        }

        /** PC 写 CCCD 订阅通知: 必须回 GATT_SUCCESS, 否则 PC 端收不到指令 */
        override fun onDescriptorWriteRequest(
            device: BluetoothDevice?, requestId: Int,
            descriptor: BluetoothGattDescriptor?, preparedWrite: Boolean,
            responseNeeded: Boolean, offset: Int, value: ByteArray?
        ) {
            if (responseNeeded) {
                // 按规范回写收到的值 (部分栈会校验响应负载)
                gattServer?.sendResponse(
                    device, requestId, BluetoothGatt.GATT_SUCCESS, 0, value)
            }
        }
    }

    /**
     * 执行远程指令 (不依赖 GATT 通道 — 那条路被 vivo 省电策略堵死了):
     * 全部走「连接状态」这个已被验证 100% 可靠的东西。
     *
     * 锁定: 掐断 GATT 连接 + 停掉蓝牙广播 (PC 重连不上, 锁屏不被自动解锁)
     *       + 尝试关闭蓝牙 (Android 13+ 系统限制可能失败, 不影响锁定效果)。
     * 解锁: 清除锁定标记 + 尝试打开蓝牙 + 恢复广播
     *       → PC 自动重连 → near=1 → 凭据提供程序自动解锁。
     */
    private fun executeCommand(cmd: String): Int {
        if (cmd == CMD_LOCK) {
            val dev = connectedDevice
            // 1. 掐断连接 → PC 端 2s 防抖后自动锁屏
            try { gattServer?.cancelConnection(dev) } catch (_: Exception) { }
            connectedDevice = null
            pcConnected = false
            // 2. 停广播: PC 的重连尝试会一直失败, 保持锁定 (不依赖任何权限)
            isRemoteLocked = true
            try { advertiser?.stopAdvertising(callback) } catch (_: Exception) { }
            // 3. 尽力关蓝牙 (Android 13+ 第三方 App 被禁, 失败也无妨 — 广播已停)
            var btOff = false
            try { btOff = btAdapter.disable() } catch (_: Exception) { }
            updateNotification()
            android.util.Log.i("BeaconService", "remote lock: cancel+stopAdv, btOff=$btOff")
            return RESULT_OK
        }

        // UNLOCK
        isRemoteLocked = false
        val adapter = btAdapter
        if (adapter != null && !adapter.isEnabled) {
            var on = false
            try { on = adapter.enable() } catch (_: Exception) { }
            if (!on) {
                // Android 13+: 需要用户在系统弹窗点「允许」, 蓝牙开了后 receiver 会自动恢复广播
                return RESULT_NEED_ENABLE
            }
        }
        // 蓝牙本来就开着 (或刚打开): 立即恢复广播, PC 秒重连 → 自动解锁
        if (adapter?.isEnabled == true) startAdvertising()
        return RESULT_OK
    }

    /** 连接状态变了刷新常驻通知文案, 用户一眼能看到 PC 在不在线 */
    private fun updateNotification() {
        try {
            val nm = getSystemService(NOTIFICATION_SERVICE) as NotificationManager
            nm.notify(NOTIF_ID, buildNotification())
        } catch (_: Exception) { }
    }

    /**
     * Android 12+: 从后台拉起前台服务时, 系统默认把通知延后 10 秒显示,
     * 设置 FOREGROUND_SERVICE_IMMEDIATE 可以让状态栏立刻出现, 用户能马上看到恢复。
     */
    private fun withImmediateBehavior(n: Notification): Notification {
        if (Build.VERSION.SDK_INT < 31) return n
        val b = Notification.Builder.recoverBuilder(this, n)
        b.setForegroundServiceBehavior(Notification.FOREGROUND_SERVICE_IMMEDIATE)
        return b.build()
    }

    private fun buildNotification(): Notification {
        // 点通知栏 → 打开 App
        val openApp = PendingIntent.getActivity(
            this, 0,
            Intent(this, MainActivity::class.java).apply {
                flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP
            },
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

        val text = when {
            isRemoteLocked -> "已远程锁定电脑 · 点「解锁电脑」恢复"
            pcConnected    -> "已连上电脑 · 点此打开 App"
            else           -> "运行中 · 等待电脑连接 · 点此打开 App"
        }

        var notification: Notification = if (Build.VERSION.SDK_INT >= 33) {
            Notification.Builder(this, CHANNEL_ID)
                .setContentTitle("Proximity Lock")
                .setContentText(text)
                .setSmallIcon(android.R.drawable.stat_sys_data_bluetooth)
                .setOngoing(true)
                .setContentIntent(openApp)
                .build()
        } else {
            @Suppress("DEPRECATION")
            Notification.Builder(this)
                .setContentTitle("Proximity Lock")
                .setContentText(text)
                .setSmallIcon(android.R.drawable.stat_sys_data_bluetooth)
                .setOngoing(true)
                .setContentIntent(openApp)
                .build()
        }

        if (Build.VERSION.SDK_INT >= 31) {
            notification = withImmediateBehavior(notification)
        }
        return notification
    }

    private fun startAsForeground() {
        val nm = getSystemService(NOTIFICATION_SERVICE) as NotificationManager
        if (Build.VERSION.SDK_INT >= 26) {
            nm.createNotificationChannel(
                NotificationChannel(CHANNEL_ID, "自动解锁服务", NotificationManager.IMPORTANCE_LOW)
            )
        }

        val notification = buildNotification()

        if (Build.VERSION.SDK_INT >= 34) {
            startForeground(
                NOTIF_ID, notification,
                android.content.pm.ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE
            )
        } else {
            startForeground(NOTIF_ID, notification)
        }
    }
}
