package com.liangzai.proximitylock

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.bluetooth.BluetoothManager
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
 */
class BeaconService : Service() {

    companion object {
        @Volatile
        var isRunning = false
            private set

        private const val CHANNEL_ID = "beacon_channel"
        private const val NOTIF_ID = 1

        // 最小 GATT 服务: Windows 连接后服务发现不为空, 连接更稳
        val SERVICE_UUID: UUID = UUID.fromString("9A660001-C77B-4A9D-B5E3-1D6B7A9E2C11")
        val CHAR_UUID: UUID = UUID.fromString("9A660002-C77B-4A9D-B5E3-1D6B7A9E2C11")
    }

    private val advertiser by lazy {
        (getSystemService(BLUETOOTH_SERVICE) as BluetoothManager)
            .adapter?.bluetoothLeAdvertiser
    }

    private var gattServer: android.bluetooth.BluetoothGattServer? = null

    private val callback = object : AdvertiseCallback() {
        override fun onStartFailure(errorCode: Int) {
            isRunning = false
            stopSelf()
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        startAsForeground()
        startAdvertising()
        isRunning = true
        return START_STICKY
    }

    /**
     * 用户从最近任务里划掉 App: 服务继续跑 (前台服务 + START_STICKY)。
     * 部分 ROM 会顺手掐掉, 这里再主动兜一次 —— 只要没被用户点"完全退出", 就恢复广播。
     */
    override fun onTaskRemoved(rootIntent: Intent?) {
        if (!isRunning) {
            startAdvertising()
            isRunning = true
        }
        super.onTaskRemoved(rootIntent)
    }

    override fun onDestroy() {
        advertiser?.stopAdvertising(callback)
        gattServer?.close()
        gattServer = null
        isRunning = false
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

    private fun startAdvertising() {
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

    /** 最小 GATT 服务器: 一个服务 + 一个只读特征值 (返回版本标识) */
    private fun startGattServer() {
        if (gattServer != null) return
        val server = try {
            (getSystemService(BLUETOOTH_SERVICE) as BluetoothManager)
                .openGattServer(this, gattCallback)
        } catch (_: SecurityException) {
            null
        } ?: return

        val service = android.bluetooth.BluetoothGattService(
            SERVICE_UUID, android.bluetooth.BluetoothGattService.SERVICE_TYPE_PRIMARY)
        val char = android.bluetooth.BluetoothGattCharacteristic(
            CHAR_UUID,
            android.bluetooth.BluetoothGattCharacteristic.PROPERTY_READ,
            android.bluetooth.BluetoothGattCharacteristic.PERMISSION_READ)
        char.value = "ProximityLock-v3".toByteArray()
        service.addCharacteristic(char)
        server.addService(service)
        gattServer = server
    }

    private val gattCallback = object : android.bluetooth.BluetoothGattServerCallback() {
        override fun onCharacteristicReadRequest(
            device: android.bluetooth.BluetoothDevice?, requestId: Int,
            offset: Int, characteristic: android.bluetooth.BluetoothGattCharacteristic?
        ) {
            val value = if (characteristic?.uuid == CHAR_UUID) {
                "ProximityLock-v3".toByteArray()
            } else null
            gattServer?.sendResponse(
                device, requestId,
                if (value != null) android.bluetooth.BluetoothGatt.GATT_SUCCESS
                else android.bluetooth.BluetoothGatt.GATT_FAILURE,
                0, value)
        }
    }

    private fun startAsForeground() {
        val nm = getSystemService(NOTIFICATION_SERVICE) as NotificationManager
        if (Build.VERSION.SDK_INT >= 26) {
            nm.createNotificationChannel(
                NotificationChannel(CHANNEL_ID, "接近解锁服务", NotificationManager.IMPORTANCE_LOW)
            )
        }

        // 点通知栏 → 打开 App
        val openApp = PendingIntent.getActivity(
            this, 0,
            Intent(this, MainActivity::class.java).apply {
                flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP
            },
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

        val notification: Notification = if (Build.VERSION.SDK_INT >= 33) {
            Notification.Builder(this, CHANNEL_ID)
                .setContentTitle("Proximity Lock")
                .setContentText("蓝牙连接模式运行中, 点此打开 App")
                .setSmallIcon(android.R.drawable.stat_sys_data_bluetooth)
                .setOngoing(true)
                .setContentIntent(openApp)
                .build()
        } else {
            @Suppress("DEPRECATION")
            Notification.Builder(this)
                .setContentTitle("Proximity Lock")
                .setContentText("蓝牙连接模式运行中, 点此打开 App")
                .setSmallIcon(android.R.drawable.stat_sys_data_bluetooth)
                .setOngoing(true)
                .setContentIntent(openApp)
                .build()
        }

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
