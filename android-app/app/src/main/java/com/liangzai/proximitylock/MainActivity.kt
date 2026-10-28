package com.liangzai.proximitylock

import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothManager
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.view.View
import android.widget.Button
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import androidx.appcompat.widget.SwitchCompat
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat

/** 自动开始开关固化: 存 SharedPreferences */
object PrefStore {
    private const val KEY_AUTO_START = "auto_start"
    private const val KEY_WANT_RUNNING = "want_running"

    private fun prefs(ctx: Context) =
        ctx.getSharedPreferences("proximitylock", Context.MODE_PRIVATE)

    fun isAutoStart(ctx: Context): Boolean = prefs(ctx).getBoolean(KEY_AUTO_START, true)

    fun setAutoStart(ctx: Context, enabled: Boolean) {
        prefs(ctx).edit().putBoolean(KEY_AUTO_START, enabled).apply()
    }

    /**
     * 用户是否希望服务运行 (默认 true)。
     * 看门狗/开机自启只在它为 true 时才拉起服务, 这样「完全退出」不会被自动救回来。
     */
    fun isWantRunning(ctx: Context): Boolean = prefs(ctx).getBoolean(KEY_WANT_RUNNING, true)

    fun setWantRunning(ctx: Context, enabled: Boolean) {
        prefs(ctx).edit().putBoolean(KEY_WANT_RUNNING, enabled).apply()
    }
}

class MainActivity : AppCompatActivity() {

    companion object {
        private const val REQ_PERMS = 100
    }

    private lateinit var statusText: TextView
    private lateinit var statusDot: View
    private lateinit var toggleBtn: Button
    private lateinit var autoStartSwitch: SwitchCompat
    private lateinit var tvBatteryState: TextView
    private val adapter: BluetoothAdapter?
        get() = (getSystemService(BLUETOOTH_SERVICE) as? BluetoothManager)?.adapter

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        statusText = findViewById(R.id.statusText)
        statusDot = findViewById(R.id.statusDot)
        toggleBtn = findViewById(R.id.toggleBtn)
        autoStartSwitch = findViewById(R.id.autoStartSwitch)
        tvBatteryState = findViewById(R.id.tvBatteryState)

        autoStartSwitch.isChecked = PrefStore.isAutoStart(this)
        autoStartSwitch.setOnCheckedChangeListener { _, checked ->
            PrefStore.setAutoStart(this, checked)
        }

        toggleBtn.setOnClickListener {
            if (BeaconService.isRunning) {
                PrefStore.setWantRunning(this, false)   // 用户主动停止, 看门狗别救
                stopService(Intent(this, BeaconService::class.java))
                updateUi(false)
            } else {
                PrefStore.setWantRunning(this, true)
                if (ensurePermissions()) startBeacon()
            }
        }

        // 电池白名单: 熄屏后系统省电会限制蓝牙, 加入白名单广播/连接更稳
        val power = getSystemService(Context.POWER_SERVICE) as android.os.PowerManager
        refreshBatteryState(power)
        findViewById<View>(R.id.rowBattery).setOnClickListener {
            if (!power.isIgnoringBatteryOptimizations(packageName)) {
                startActivity(Intent(
                    android.provider.Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS,
                    android.net.Uri.parse("package:$packageName")
                ))
            } else {
                Toast.makeText(this, "已经在电池白名单里了", Toast.LENGTH_SHORT).show()
            }
        }

        // 自启动 / 后台运行: 各家 ROM 私有页面, 只能跳转过去让用户自己点
        findViewById<View>(R.id.rowOem).setOnClickListener {
            OemSettings.openAutoStart(this)
            Toast.makeText(
                this,
                "把 ProximityLock 设为「允许自启动」+「允许后台高耗电」",
                Toast.LENGTH_LONG
            ).show()
        }
        // 长按 → 后台省电策略页 (vivo 等把高耗电单独放在一处)
        findViewById<View>(R.id.rowOem).setOnLongClickListener {
            OemSettings.openPowerSettings(this)
            Toast.makeText(this, "把本应用设为「允许后台高耗电 / 不受限制」", Toast.LENGTH_LONG).show()
            true
        }

        // 完全退出: 停后台服务 + 关掉本界面 (之后靠开机自启/手动打开才恢复)
        findViewById<View>(R.id.rowExit).setOnClickListener {
            PrefStore.setWantRunning(this, false)   // 标记"不要跑", 看门狗和开机自启都不再拉起
            stopService(Intent(this, BeaconService::class.java))
            Toast.makeText(this, "已退出, 后台服务已停止", Toast.LENGTH_SHORT).show()
            finishAndRemoveTask()
        }

        // 页脚点击 → 打开开源仓库
        findViewById<TextView>(R.id.tvFooter).setOnClickListener {
            try {
                startActivity(Intent(Intent.ACTION_VIEW,
                    android.net.Uri.parse("https://github.com/nomoneynolife/ProximityLock")))
            } catch (_: Exception) {
                Toast.makeText(this, "没有可用的浏览器", Toast.LENGTH_SHORT).show()
            }
        }
    }

    override fun onResume() {
        super.onResume()
        updateUi(BeaconService.isRunning)
        refreshBatteryState(getSystemService(Context.POWER_SERVICE) as android.os.PowerManager)

        // 自动开始: 开关开着且服务没在跑 → 自动进入广播状态
        if (autoStartSwitch.isChecked && !BeaconService.isRunning) {
            if (hasRequiredPermissions()) {
                startBeacon()
            } else {
                ensurePermissions() // 授权后 onRequestPermissionsResult 里会自动开
            }
        }
    }

    private fun refreshBatteryState(power: android.os.PowerManager) {
        val granted = power.isIgnoringBatteryOptimizations(packageName)
        tvBatteryState.text = if (granted) "已加入白名单 · 熄屏不限制" else "未加入 · 点击申请"
        tvBatteryState.setTextColor(
            ContextCompat.getColor(this, if (granted) R.color.ok else R.color.ink_soft)
        )
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
            showStatus("蓝牙没开，请先打开蓝牙", false)
            return
        }
        if (a.bluetoothLeAdvertiser == null) {
            showStatus("这台手机不支持 BLE 广播", false)
            return
        }
        PrefStore.setWantRunning(this, true)
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
            showStatus("权限被拒了，自动解锁开不了", false)
        }
    }

    private fun showStatus(text: String, running: Boolean) {
        statusText.text = text
        statusDot.setBackgroundResource(if (running) R.drawable.dot_ok else R.drawable.dot_warn)
    }

    private fun updateUi(running: Boolean) {
        if (running) {
            toggleBtn.text = "停止解锁"
            toggleBtn.setBackgroundResource(R.drawable.bg_btn_stop)
            statusText.text = "自动解锁已开启 · 等待 PC 连接"
            statusDot.setBackgroundResource(R.drawable.dot_ok)
        } else {
            toggleBtn.text = "自动解锁"
            toggleBtn.setBackgroundResource(R.drawable.bg_btn_primary)
            statusText.text = "自动解锁未开启"
            statusDot.setBackgroundResource(R.drawable.dot_idle)
        }
    }
}
