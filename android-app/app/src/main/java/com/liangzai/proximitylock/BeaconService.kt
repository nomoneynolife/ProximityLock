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

        // 连接模式: PC 端连接该 GATT 服务并读取令牌验证身份 (与 PC 端 GattWatcher 对应)
        val SERVICE_UUID: UUID = UUID.fromString("9A660001-C77B-4A9D-B5E3-1D6B7A9E2C11")
        val CHAR_UUID: UUID = UUID.fromString("9A660002-C77B-4A9D-B5E3-1D6B7A9E2C11")
    }

    private val advertiser by lazy {
        (getSystemService(BLUETOOTH_SERVICE) as BluetoothManager)
            .adapter?.bluetoothLeAdvertiser
    }

    private var gattServer: android.bluetooth.BluetoothGattServer? = null

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
        startAdvertising()
        isRunning = true
        return START_STICKY
    }

    override fun onDestroy() {
        rotateHandler.removeCallbacks(rotateRunnable)
        advertiser?.stopAdvertising(callback)
        gattServer?.close()
        gattServer = null
        isRunning = false
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    private val rotateHandler = android.os.Handler(android.os.Looper.getMainLooper())

    /** 时间片切换时重启广播, 换上新的滚动 UUID */
    private val rotateRunnable = Runnable {
        if (isRunning) startAdvertising()
    }

    private fun startAdvertising() {
        val seed = UuidStore.getSeed(this)
        val rolling = RollingUuid.isValidSeed(seed)

        // 滚动模式: 按种子+时间片推导 UUID; 否则用固定 UUID
        val uuid = if (rolling) {
            RollingUuid.derive(seed, RollingUuid.sliceAt())
        } else {
            try {
                UUID.fromString(UuidStore.get(this))
            } catch (_: IllegalArgumentException) {
                UUID.fromString(UuidStore.DEFAULT)
            }
        }

        advertiser?.stopAdvertising(callback)

        val payload = buildIBeaconPayload(uuid)
        val settings = AdvertiseSettings.Builder()
            .setAdvertiseMode(AdvertiseSettings.ADVERTISE_MODE_LOW_LATENCY)
            .setTxPowerLevel(AdvertiseSettings.ADVERTISE_TX_POWER_HIGH)
            // 可连接: 连接模式下 PC 端要能连上来 (iBeacon 走广播包, 服务 UUID 走扫描响应)
            .setConnectable(true)
            .build()

        val data = AdvertiseData.Builder()
            .setIncludeDeviceName(false)
            .addManufacturerData(0x004C, payload) // Apple 厂商 ID, iBeacon 走这里
            .build()
        // 注: 服务 UUID 不放广播里 (31 字节放不下 iBeacon+UUID),
        // PC 端连接后用 GetGattServicesAsync 主动发现, 不依赖广播里的 UUID。

        advertiser?.startAdvertising(settings, data, callback)
        startGattServer()

        // 滚动模式: 定时到下一个分钟整点换 UUID
        rotateHandler.removeCallbacks(rotateRunnable)
        if (rolling) {
            val nextBoundaryMs = (RollingUuid.sliceAt() + 1) * RollingUuid.SLICE_SECONDS * 1000
            val delay = (nextBoundaryMs - System.currentTimeMillis()).coerceAtLeast(1000L)
            rotateHandler.postDelayed(rotateRunnable, delay)
        }
    }

    /** 连接模式: PC 读取令牌特征值验证"连上的真是我们的手机" */
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
                RollingUuid.tokenBytes(UuidStore.getSeed(this@BeaconService), currentFixedUuid())
            } else null
            gattServer?.sendResponse(
                device, requestId,
                if (value != null) android.bluetooth.BluetoothGatt.GATT_SUCCESS
                else android.bluetooth.BluetoothGatt.GATT_FAILURE,
                0, value)
        }
    }

    private fun currentFixedUuid(): UUID = try {
        UUID.fromString(UuidStore.get(this))
    } catch (_: IllegalArgumentException) {
        UUID.fromString(UuidStore.DEFAULT)
    }

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
