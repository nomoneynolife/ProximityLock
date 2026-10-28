package com.liangzai.proximitylock

import android.app.AlarmManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.os.Build
import android.util.Log

/**
 * 看门狗: 由 AlarmManager 定时唤醒 (闹钟活在系统侧, 进程被杀也会照常触发)。
 * 作用 = 检查服务是否还活着; 被 ROM 上滑杀掉就重新拉起来。
 * 用户点「完全退出」或「停止广播」后 wantRunning=false, 这里就不再救。
 */
class RestartReceiver : BroadcastReceiver() {

    companion object {
        private const val TAG = "RestartReceiver"
        const val ACTION_WATCHDOG = "com.liangzai.proximitylock.WATCHDOG"

        /** 调度下一次看门狗 (间隔 60 秒) */
        fun schedule(ctx: Context, delayMs: Long = 60_000L) {
            val am = ctx.getSystemService(Context.ALARM_SERVICE) as? AlarmManager ?: return
            val pi = pendingIntent(ctx)
            val triggerAt = System.currentTimeMillis() + delayMs
            try {
                // 能用精确闹钟就用 (Doze 下也准); 没有权限则退化为普通允许空闲闹钟
                if (Build.VERSION.SDK_INT >= 31 && am.canScheduleExactAlarms()) {
                    am.setExactAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, triggerAt, pi)
                } else if (Build.VERSION.SDK_INT >= 23) {
                    am.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, triggerAt, pi)
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

        private fun pendingIntent(ctx: Context): PendingIntent {
            val intent = Intent(ctx, RestartReceiver::class.java).setAction(ACTION_WATCHDOG)
            var flags = PendingIntent.FLAG_UPDATE_CURRENT
            if (Build.VERSION.SDK_INT >= 23) flags = flags or PendingIntent.FLAG_IMMUTABLE
            return PendingIntent.getBroadcast(ctx, 1001, intent, flags)
        }
    }

    override fun onReceive(context: Context, intent: Intent) {
        // 先排下一次, 保证链条不断 (即使下面的启动失败)
        schedule(context)

        if (!PrefStore.isWantRunning(context)) {
            cancel(context)   // 用户已明确退出, 停止看门狗
            return
        }

        if (BeaconService.isRunning) return   // 服务还活着, 什么都不用做

        Log.i(TAG, "检测到服务不在运行, 尝试拉起")
        try {
            val svc = Intent(context, BeaconService::class.java)
            if (Build.VERSION.SDK_INT >= 26) {
                context.startForegroundService(svc)
            } else {
                context.startService(svc)
            }
        } catch (e: Exception) {
            // Android 12+ 后台启动前台服务受限 (未加电池白名单时会抛这个)
            Log.w(TAG, "拉起服务失败: ${e.message}")
        }
    }
}
