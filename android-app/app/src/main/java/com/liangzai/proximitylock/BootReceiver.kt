package com.liangzai.proximitylock

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import androidx.core.content.ContextCompat

/**
 * 开机自启: 手机重启后自动恢复蓝牙连接服务, App 从此不用再打开。
 * 「打开 App 自动开始广播」开关关着的话就不启动。
 */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val action = intent.action ?: return
        if (action != Intent.ACTION_BOOT_COMPLETED &&
            action != "android.intent.action.QUICKBOOT_POWERON" &&
            action != Intent.ACTION_MY_PACKAGE_REPLACED &&
            action != "android.intent.action.LOCKED_BOOT_COMPLETED"
        ) return

        if (!PrefStore.isAutoStart(context)) return
        if (!PrefStore.isWantRunning(context)) return   // 用户点过「完全退出」就不自启
        if (BeaconService.isRunning) return

        try {
            ContextCompat.startForegroundService(
                context, Intent(context, BeaconService::class.java))
        } catch (_: SecurityException) {
            // 权限还没给 (新装/被吊销), 首次仍需打开 App 授权一次
        }
    }
}
