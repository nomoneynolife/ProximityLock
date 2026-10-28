package com.liangzai.proximitylock

import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.provider.Settings

/**
 * 国产 ROM 的「自启动 / 后台运行」白名单页面没有统一 API,
 * 只能按厂商的私有组件名逐个尝试, 都失败就退到系统"应用详情"页。
 */
object OemSettings {

    private data class Target(val pkg: String, val cls: String)

    // 各厂商自启动管理页 (按命中率排序)
    private val AUTOSTART_PAGES = listOf(
        // vivo / iQOO (OriginOS / Funtouch)
        Target("com.vivo.permissionmanager", "com.vivo.permissionmanager.activity.BgStartUpManagerActivity"),
        Target("com.iqoo.secure", "com.iqoo.secure.ui.phoneoptimize.BgStartUpManager"),
        Target("com.iqoo.secure", "com.iqoo.secure.ui.phoneoptimize.AddWhiteListActivity"),
        // 小米 / 红米 (MIUI / HyperOS)
        Target("com.miui.securitycenter", "com.miui.permcenter.autostart.AutoStartManagementActivity"),
        // 华为 / 荣耀
        Target("com.huawei.systemmanager", "com.huawei.systemmanager.startupmgr.ui.StartupNormalAppListActivity"),
        Target("com.hihonor.systemmanager", "com.hihonor.systemmanager.startupmgr.ui.StartupNormalAppListActivity"),
        // OPPO / realme / 一加
        Target("com.coloros.safecenter", "com.coloros.safecenter.permission.startup.StartupAppListActivity"),
        Target("com.oppo.safe", "com.oppo.safe.permission.startup.StartupAppListActivity"),
        Target("com.oneplus.security", "com.oneplus.security.chainlaunch.view.ChainLaunchAppListActivity"),
        // 魅族
        Target("com.meizu.safe", "com.meizu.safe.permission.SmartBGActivity"),
    )

    /** 后台高耗电 / 省电策略页面 (vivo 系常与自启动分开) */
    private val POWER_PAGES = listOf(
        Target("com.vivo.permissionmanager", "com.vivo.permissionmanager.activity.PurviewTabActivity"),
        Target("com.iqoo.secure", "com.iqoo.secure.ui.phoneoptimize.AddWhiteListActivity"),
        Target("com.miui.powerkeeper", "com.miui.powerkeeper.ui.HiddenAppsConfigActivity"),
    )

    /** 打开自启动管理页; 全部失败则打开本应用的系统详情页 */
    fun openAutoStart(ctx: Context): Boolean =
        tryPages(ctx, AUTOSTART_PAGES) || openAppDetails(ctx)

    /** 打开后台省电/高耗电白名单页; 失败则退回电池优化页 */
    fun openPowerSettings(ctx: Context): Boolean =
        tryPages(ctx, POWER_PAGES) || openAppDetails(ctx)

    fun openAppDetails(ctx: Context): Boolean = try {
        ctx.startActivity(
            Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
                Uri.parse("package:${ctx.packageName}"))
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        )
        true
    } catch (_: Exception) {
        false
    }

    private fun tryPages(ctx: Context, pages: List<Target>): Boolean {
        for (t in pages) {
            try {
                ctx.startActivity(
                    Intent().setComponent(ComponentName(t.pkg, t.cls))
                        .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                )
                return true
            } catch (_: Exception) {
                // 该机型没有这个页面, 试下一个
            }
        }
        return false
    }
}
