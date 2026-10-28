package com.liangzai.proximitylock

import android.app.AlarmManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.os.Build
import android.util.Log
import androidx.core.content.ContextCompat

/**
 * 看门狗: 由 AlarmManager 定时唤醒 (闹钟活在系统侧, 进程被杀也会照常触发)。
 * 作用 = 检查服务是否还活着; 被 ROM 上滑杀掉就重新拉起来。
 * 用户点「停止解锁」或「完全退出」后 wantRunning=false, 这里就不再救。
 */
class RestartReceiver : BroadcastReceiver() {

    companion object {
        private const val TAG = "RestartReceiver"
        const val ACTION_WATCHDOG = "com.liangzai.proximitylock.WATCHDOG"

        /** 常规心跳间隔 (秒级恢复靠 onTaskRemoved/onDestroy 的 1s 快速通道) */
        const val HEARTBEAT_MS = 20_000L

        /** 调度下一次看门狗 */
        fun schedule(ctx: Context, delayMs: Long = HEARTBEAT_MS) {
            val am = ctx.getSystemService(Context.ALARM_SERVICE) as? AlarmManager ?: return
            val pi = pendingIntent(ctx)
            val triggerAt = System.currentTimeMillis() + delayMs
            try {
                if (canExact(am)) {
                    // 精确闹钟: 秒级触发, Doze 下也能响
                    am.setExactAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, triggerAt, pi)
                } else if (Build.VERSION.SDK_INT >= 23) {
                    // 没给精确闹钟权限: 在 [triggerAt, triggerAt+5s] 窗口内触发
                    am.setWindow(AlarmManager.RTC_WAKEUP, triggerAt, 5_000L, pi)
                } else {
                    am.set(AlarmManager.RTC_WAKEUP, triggerAt, pi)
                }
            } catch (e: Exception) {
                Log.w(TAG, "调度看门狗失败: ${e.message}")
            }
        }

        fun cancel(ctx: Context) {
            val am = ctx.getSystemService(Context.ALARM_SERVICE) as? AlarmManager ?: return
            am.cancel(pendingIntent(ctx))
        }

        /** 是否拥有精确闹钟权限 (Android 12+ 需要用户手动授予) */
        fun canExact(am: AlarmManager): Boolean =
            if (Build.VERSION.SDK_INT >= 31) am.canScheduleExactAlarms() else true

        private fun pendingIntent(ctx: Context): PendingIntent {
            val intent = Intent(ctx, RestartReceiver::class.java).setAction(ACTION_WATCHDOG)
            var flags = PendingIntent.FLAG_UPDATE_CURRENT
            if (Build.VERSION.SDK_INT >= 23) flags = flags or PendingIntent.FLAG_IMMUTABLE
            return PendingIntent.getBroadcast(ctx, 1001, intent, flags)
        }
    }

    override fun onReceive(context: Context, intent: Intent) {
        if (!PrefStore.isWantRunning(context)) {
            cancel(context)   // 用户已明确退出, 停止看门狗
            return
        }

        if (BeaconService.isRunning) {
            schedule(context)   // 服务活着, 只续下一轮心跳
            return
        }

        Log.i(TAG, "检测到服务不在运行, 尝试拉起")
        val started = try {
            ContextCompat.startForegroundService(
                context, Intent(context, BeaconService::class.java))
            true
        } catch (e: Exception) {
            // 常见: ForegroundServiceStartNotAllowedException —— 电池白名单/自启动没给
            Log.w(TAG, "拉起服务失败: ${e.message}")
            false
        }

        // 成功: 20 秒后再确认; 失败: 3 秒后重试 (最多一直重试, 直到用户手动退出)
        schedule(context, if (started) HEARTBEAT_MS else 3_000L)
    }
}
