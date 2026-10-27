package com.liangzai.proximitylock

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.bluetooth.BluetoothManager
import android.bluetooth.le.AdvertiseCallback
import android.bluetooth.le.AdvertiseData
import android.bluetooth.le.AdvertiseSettings
import android.content.Intent
import android.os.Build
import android.os.IBinder
import java.nio.ByteBuffer
import java.util.UUID

/**
 * 前台服务: 把手机伪装成一个 iBeacon 持续广播。
 * PC 端 ProximityLock 监听该 UUID 的 RSSI 来判定手机远近。
 */
class BeaconService : Service() {

    companion object {
        @Volatile
        var isRunning = false
            private set

        private const val CHANNEL_ID = "beacon_channel"
        private const val NOTIF_ID = 1
        private const val TX_POWER_CALIBRATION: Byte = -59 // 距手机 1 米处的参考 RSSI
    }

    private val advertiser by lazy {
        (getSystemService(BLUETOOTH_SERVICE) as BluetoothManager)
            .adapter?.bluetoothLeAdvertiser
    }

    private val callback = object : AdvertiseCallback() {
        override fun onStartSuccess(settingsInEffect: AdvertiseSettings?) {
            // 广播成功, 无需处理
        }
        override fun onStartFailure(errorCode: Int) {
            isRunning = false
            stopSelf()
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        startAsForeground()

        // 每次启动都从固化配置读 UUID, 用户改过就跟随
        val uuid = try {
            UUID.fromString(UuidStore.get(this))
        } catch (_: IllegalArgumentException) {
            UUID.fromString(UuidStore.DEFAULT)
        }

        val payload = buildIBeaconPayload(uuid)
        val settings = AdvertiseSettings.Builder()
            .setAdvertiseMode(AdvertiseSettings.ADVERTISE_MODE_LOW_LATENCY)
            .setTxPowerLevel(AdvertiseSettings.ADVERTISE_TX_POWER_HIGH)
            .setConnectable(false)
            .build()

        val data = AdvertiseData.Builder()
            .setIncludeDeviceName(false)
            .addManufacturerData(0x004C, payload) // Apple 厂商 ID, iBeacon 走这里
            .build()

        advertiser?.startAdvertising(settings, data, callback)
        isRunning = true
        return START_STICKY
    }

    override fun onDestroy() {
        advertiser?.stopAdvertising(callback)
        isRunning = false
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    /** 组装 iBeacon 帧: 0x02,0x15 + UUID(16B) + Major(2B) + Minor(2B) + TxPower(1B) */
    private fun buildIBeaconPayload(uuid: UUID): ByteArray {
        val bb = ByteBuffer.allocate(23)
        bb.put(0x02).put(0x15)
        val bytes = ByteBuffer.allocate(16).putLong(uuid.mostSignificantBits)
            .putLong(uuid.leastSignificantBits).array()
        bb.put(bytes)
        bb.putShort(1)   // Major
        bb.putShort(1)   // Minor
        bb.put(TX_POWER_CALIBRATION)
        return bb.array()
    }

    private fun startAsForeground() {
        val nm = getSystemService(NOTIFICATION_SERVICE) as NotificationManager
        if (Build.VERSION.SDK_INT >= 26) {
            nm.createNotificationChannel(
                NotificationChannel(CHANNEL_ID, "接近解锁广播", NotificationManager.IMPORTANCE_LOW)
            )
        }
        val notification: Notification = if (Build.VERSION.SDK_INT >= 33) {
            Notification.Builder(this, CHANNEL_ID)
                .setContentTitle("Proximity Lock")
                .setContentText("正在广播 Beacon, PC 端正在监听")
                .setSmallIcon(android.R.drawable.stat_sys_data_bluetooth)
                .setOngoing(true)
                .build()
        } else {
            @Suppress("DEPRECATION")
            Notification.Builder(this)
                .setContentTitle("Proximity Lock")
                .setContentText("正在广播 Beacon, PC 端正在监听")
                .setSmallIcon(android.R.drawable.stat_sys_data_bluetooth)
                .setOngoing(true)
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
