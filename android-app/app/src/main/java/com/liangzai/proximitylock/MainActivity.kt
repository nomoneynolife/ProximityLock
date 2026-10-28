package com.liangzai.proximitylock

import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothManager
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.widget.Button
import android.widget.EditText
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat
import java.util.UUID

/** UUID 固化存储: 存 SharedPreferences, 下次开机继续用 */
object UuidStore {
    const val DEFAULT = "E2C56DB5-DFFB-48D2-B060-D0F5A71096E0"
    private const val KEY = "beacon_uuid"
    private const val KEY_SEED = "rolling_seed"
    private const val KEY_AUTO_START = "auto_start"

    private fun prefs(ctx: Context) =
        ctx.getSharedPreferences("proximitylock", Context.MODE_PRIVATE)

    fun get(ctx: Context): String = prefs(ctx).getString(KEY, DEFAULT) ?: DEFAULT

    fun set(ctx: Context, value: String) {
        prefs(ctx).edit().putString(KEY, value).apply()
    }

    /** 滚动 UUID 种子, 空字符串 = 未启用滚动模式 (用固定 UUID) */
    fun getSeed(ctx: Context): String = prefs(ctx).getString(KEY_SEED, "") ?: ""

    fun setSeed(ctx: Context, value: String) {
        prefs(ctx).edit().putString(KEY_SEED, value.lowercase().trim()).apply()
    }

    fun isAutoStart(ctx: Context): Boolean = prefs(ctx).getBoolean(KEY_AUTO_START, true)

    fun setAutoStart(ctx: Context, enabled: Boolean) {
        prefs(ctx).edit().putBoolean(KEY_AUTO_START, enabled).apply()
    }
}

class MainActivity : AppCompatActivity() {

    companion object {
        private const val REQ_PERMS = 100
    }

    private lateinit var uuidEdit: EditText
    private lateinit var statusText: TextView
    private lateinit var toggleBtn: Button
    private lateinit var autoStartSwitch: android.widget.Switch
    private val adapter: BluetoothAdapter?
        get() = (getSystemService(BLUETOOTH_SERVICE) as? BluetoothManager)?.adapter

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        uuidEdit = findViewById(R.id.uuidEdit)
        statusText = findViewById(R.id.statusText)
        toggleBtn = findViewById(R.id.toggleBtn)
        autoStartSwitch = findViewById(R.id.autoStartSwitch)

        // 启动加载固化配置
        uuidEdit.setText(UuidStore.get(this))

        // 滚动 UUID 种子 (防伪造): 有种子 = 滚动模式, 固定 UUID 不再生效
        val seedEdit = findViewById<EditText>(R.id.seedEdit)
        seedEdit.setText(UuidStore.getSeed(this))

        findViewById<Button>(R.id.btnSeedPaste).setOnClickListener {
            val cm = getSystemService(CLIPBOARD_SERVICE) as ClipboardManager
            val text = cm.primaryClip?.getItemAt(0)?.text?.toString()?.trim() ?: ""
            if (!RollingUuid.isValidSeed(text)) {
                Toast.makeText(this, "剪贴板里不是 32 位 hex 种子 (去 PC 端「生成种子」后复制)", Toast.LENGTH_LONG).show()
                return@setOnClickListener
            }
            seedEdit.setText(text.lowercase())
            UuidStore.setSeed(this, text)
            restartServiceIfRunning()
            uuidEdit.isEnabled = false
            Toast.makeText(this, "种子已保存, 进入滚动 UUID 模式", Toast.LENGTH_SHORT).show()
        }

        findViewById<Button>(R.id.btnSeedGen).setOnClickListener {
            val seed = RollingUuid.randomSeed()
            seedEdit.setText(seed)
            UuidStore.setSeed(this, seed)
            val cm = getSystemService(CLIPBOARD_SERVICE) as ClipboardManager
            cm.setPrimaryClip(ClipData.newPlainText("seed", seed))
            restartServiceIfRunning()
            uuidEdit.isEnabled = false
            Toast.makeText(this, "已生成种子并复制到剪贴板, 把它粘贴到 PC 端", Toast.LENGTH_LONG).show()
        }

        findViewById<Button>(R.id.btnSeedCopy).setOnClickListener {
            val cm = getSystemService(CLIPBOARD_SERVICE) as ClipboardManager
            cm.setPrimaryClip(ClipData.newPlainText("seed", seedEdit.text.toString().trim()))
            Toast.makeText(this, "已复制, 粘贴到 PC 端种子框", Toast.LENGTH_SHORT).show()
        }

        findViewById<Button>(R.id.btnSeedClear).setOnClickListener {
            seedEdit.setText("")
            UuidStore.setSeed(this, "")
            restartServiceIfRunning()
            uuidEdit.isEnabled = true
            Toast.makeText(this, "已清除种子, 回到固定 UUID 模式", Toast.LENGTH_SHORT).show()
        }

        // 滚动模式下固定 UUID 无效, 输入框置灰提示
        uuidEdit.isEnabled = !RollingUuid.isValidSeed(UuidStore.getSeed(this))

        // 默认侦听开关: 打开 App 自动进入广播状态
        autoStartSwitch.isChecked = UuidStore.isAutoStart(this)
        autoStartSwitch.setOnCheckedChangeListener { _, checked ->
            UuidStore.setAutoStart(this, checked)
        }

        findViewById<Button>(R.id.btnRandom).setOnClickListener {
            uuidEdit.setText(UUID.randomUUID().toString().uppercase())
            Toast.makeText(this, "已生成随机 UUID, 和 PC 端填同一个后各自点保存", Toast.LENGTH_SHORT).show()
        }

        findViewById<Button>(R.id.btnCopy).setOnClickListener {
            val cm = getSystemService(CLIPBOARD_SERVICE) as ClipboardManager
            cm.setPrimaryClip(ClipData.newPlainText("uuid", uuidEdit.text.toString().trim()))
            Toast.makeText(this, "已复制", Toast.LENGTH_SHORT).show()
        }

        findViewById<Button>(R.id.btnDefault).setOnClickListener {
            uuidEdit.setText(UuidStore.DEFAULT)
            UuidStore.set(this, UuidStore.DEFAULT)
            restartServiceIfRunning()
            Toast.makeText(this, "已恢复默认并固化", Toast.LENGTH_SHORT).show()
        }

        findViewById<Button>(R.id.btnSave).setOnClickListener {
            val text = uuidEdit.text.toString().trim()
            try {
                UUID.fromString(text)
            } catch (_: IllegalArgumentException) {
                Toast.makeText(this, "UUID 格式不对, 没保存", Toast.LENGTH_SHORT).show()
                return@setOnClickListener
            }
            UuidStore.set(this, text.uppercase())
            uuidEdit.setText(text.uppercase())
            restartServiceIfRunning()
            Toast.makeText(this, "已保存固化, 下次开机继续用", Toast.LENGTH_SHORT).show()
        }

        toggleBtn.setOnClickListener {
            if (BeaconService.isRunning) {
                stopService(Intent(this, BeaconService::class.java))
                updateUi(false)
            } else {
                if (ensurePermissions()) startBeacon()
            }
        }

        // 电池白名单: 熄屏后系统省电会暂停 BLE 广播, 加入白名单能大幅缓解
        val power = getSystemService(Context.POWER_SERVICE) as android.os.PowerManager
        val batteryBtn = findViewById<Button>(R.id.btnBattery)
        batteryBtn.text = if (power.isIgnoringBatteryOptimizations(packageName))
            "已在电池白名单中" else "申请电池白名单 (防熄屏断广播)"
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

        // 默认侦听: 开关开着且广播没在跑 → 自动进入广播状态 (App 被后台杀掉后再打开也一样)
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
            if (Build.VERSION.SDK_INT >= 31) add(android.Manifest.permission.BLUETOOTH_ADVERTISE)
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

    /** UUID 变了, 广播在跑的话重启服务用新 UUID */
    private fun restartServiceIfRunning() {
        if (!BeaconService.isRunning) return
        stopService(Intent(this, BeaconService::class.java))
        ContextCompat.startForegroundService(this, Intent(this, BeaconService::class.java))
    }

    private fun ensurePermissions(): Boolean {
        val needed = buildList {
            if (Build.VERSION.SDK_INT >= 31) add(android.Manifest.permission.BLUETOOTH_ADVERTISE)
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
        val rolling = RollingUuid.isValidSeed(UuidStore.getSeed(this))
        statusText.text = when {
            running && rolling -> "广播中 · 滚动 UUID (每分钟更换) · PC 端正在监听"
            running -> "广播中 · 手机离开蓝牙范围后 PC 会自动锁屏"
            else -> "未运行 · 点下面按钮开启"
        }
    }
}
