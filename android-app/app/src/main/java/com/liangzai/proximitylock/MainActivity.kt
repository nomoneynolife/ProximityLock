package com.liangzai.proximitylock

import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothManager
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.widget.Button
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat

/** 自动开始开关固化: 存 SharedPreferences */
object PrefStore {
    private const val KEY_AUTO_START = "auto_start"

    private fun prefs(ctx: Context) =
        ctx.getSharedPreferences("proximitylock", Context.MODE_PRIVATE)

    fun isAutoStart(ctx: Context): Boolean = prefs(ctx).getBoolean(KEY_AUTO_START, true)

    fun setAutoStart(ctx: Context, enabled: Boolean) {
        prefs(ctx).edit().putBoolean(KEY_AUTO_START, enabled).apply()
    }
}

class MainActivity : AppCompatActivity() {

    companion object {
        private const val REQ_PERMS = 100
    }

    private lateinit var statusText: TextView
    private lateinit var toggleBtn: Button
    private lateinit var autoStartSwitch: android.widget.Switch
    private val adapter: BluetoothAdapter?
        get() = (getSystemService(BLUETOOTH_SERVICE) as? BluetoothManager)?.adapter

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        statusText = findViewById(R.id.statusText)
        toggleBtn = findViewById(R.id.toggleBtn)
        autoStartSwitch = findViewById(R.id.autoStartSwitch)

        autoStartSwitch.isChecked = PrefStore.isAutoStart(this)
        autoStartSwitch.setOnCheckedChangeListener { _, checked ->
            PrefStore.setAutoStart(this, checked)
        }

        toggleBtn.setOnClickListener {
            if (BeaconService.isRunning) {
                stopService(Intent(this, BeaconService::class.java))
                updateUi(false)
            } else {
                if (ensurePermissions()) startBeacon()
            }
        }

        // 电池白名单: 熄屏后系统省电会限制蓝牙, 加入白名单广播/连接更稳
        val power = getSystemService(Context.POWER_SERVICE) as android.os.PowerManager
        val batteryBtn = findViewById<Button>(R.id.btnBattery)
        batteryBtn.text = if (power.isIgnoringBatteryOptimizations(packageName))
            "已在电池白名单中" else "申请电池白名单 (广播更稳定)"
        batteryBtn.setOnClickListener {
            if (!power.isIgnoringBatteryOptimizations(packageName)) {
                startActivity(Intent(
                    android.provider.Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS,
                    android.net.Uri.parse("package:$packageName")
                ))
            } else {
                Toast.makeText(this, "已经加过了, 不用重复申请", Toast.LENGTH_SHORT).show()
            }
        }
    }

    override fun onResume() {
        super.onResume()
        updateUi(BeaconService.isRunning)

        // 自动开始: 开关开着且服务没在跑 → 自动进入广播状态
        if (autoStartSwitch.isChecked && !BeaconService.isRunning) {
            if (hasRequiredPermissions()) {
                startBeacon()
            } else {
                ensurePermissions() // 授权后 onRequestPermissionsResult 里会自动开
            }
        }
    }

    private fun hasRequiredPermissions(): Boolean {
        val needed = buildList {
            if (Build.VERSION.SDK_INT >= 31) {
                add(android.Manifest.permission.BLUETOOTH_ADVERTISE)
                add(android.Manifest.permission.BLUETOOTH_CONNECT)
            }
            if (Build.VERSION.SDK_INT >= 33) add(android.Manifest.permission.POST_NOTIFICATIONS)
        }
        return needed.all {
            ContextCompat.checkSelfPermission(this, it) == PackageManager.PERMISSION_GRANTED
        }
    }

    private fun startBeacon() {
        val a = adapter
        if (a == null || !a.isEnabled) {
            statusText.text = "蓝牙没开, 先去打开蓝牙"
            return
        }
        if (a.bluetoothLeAdvertiser == null) {
            statusText.text = "这台手机不支持 BLE 广播 (bluetoothLeAdvertiser 为空)"
            return
        }
        ContextCompat.startForegroundService(this, Intent(this, BeaconService::class.java))
        updateUi(true)
    }

    private fun ensurePermissions(): Boolean {
        val needed = buildList {
            if (Build.VERSION.SDK_INT >= 31) {
                add(android.Manifest.permission.BLUETOOTH_ADVERTISE)
                add(android.Manifest.permission.BLUETOOTH_CONNECT)
            }
            if (Build.VERSION.SDK_INT >= 33) add(android.Manifest.permission.POST_NOTIFICATIONS)
        }
        val missing = needed.filter {
            ContextCompat.checkSelfPermission(this, it) != PackageManager.PERMISSION_GRANTED
        }
        if (missing.isEmpty()) return true
        ActivityCompat.requestPermissions(this, missing.toTypedArray(), REQ_PERMS)
        return false
    }

    override fun onRequestPermissionsResult(
        requestCode: Int, permissions: Array<out String>, grantResults: IntArray
    ) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == REQ_PERMS && grantResults.all { it == PackageManager.PERMISSION_GRANTED }) {
            startBeacon()
        } else {
            statusText.text = "权限被拒了, 广播开不了"
        }
    }

    private fun updateUi(running: Boolean) {
        toggleBtn.text = if (running) "停止广播" else "开始广播"
        statusText.text = if (running) {
            "运行中 · PC 配对连接后: 连着=解锁, 断开=锁屏"
        } else {
            "未运行 · 点下面按钮开启"
        }
    }
}
