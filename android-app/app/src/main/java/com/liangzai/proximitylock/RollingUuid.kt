package com.liangzai.proximitylock

import java.nio.ByteBuffer
import java.security.SecureRandom
import java.util.UUID
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/**
 * 滚动 UUID (防伪造):
 *   当前 UUID = HMAC-SHA256(seed, 时间片号大端8字节) 的前 16 字节
 *   时间片号 = unix秒 / 60, 即每 60 秒换一个 UUID。
 * 种子 (32个hex字符 = 16字节) 通过剪贴板在 PC 和手机间同步一次,
 * 空口只出现"一分钟就作废"的 UUID, 抓到也伪造不了下一个。
 *
 * 与 PC 端 RollingUuid.cs 算法逐字节一致, 两边必须同步改动。
 */
object RollingUuid {
    const val SLICE_SECONDS = 60L

    /** 当前时间片号 (按 Unix 时间对齐) */
    fun sliceAt(unixSeconds: Long = System.currentTimeMillis() / 1000): Long =
        unixSeconds / SLICE_SECONDS

    /** 由种子 + 时间片推导 UUID */
    fun derive(seedHex: String, slice: Long): UUID {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(seedHex.hexToBytes(), "HmacSHA256"))
        val msg = ByteBuffer.allocate(8).putLong(slice).array()
        val out = mac.doFinal(msg)
        val bb = ByteBuffer.wrap(out)
        return UUID(bb.long, bb.long)
    }

    fun randomSeed(): String {
        val b = ByteArray(16)
        SecureRandom().nextBytes(b)
        return b.joinToString("") { "%02x".format(it.toInt() and 0xFF) }
    }

    fun isValidSeed(text: String): Boolean =
        text.length == 32 && text.all { it.isDigit() || it in 'a'..'f' || it in 'A'..'F' }
}

fun String.hexToBytes(): ByteArray =
    ByteArray(length / 2) { i ->
        ((Character.digit(this[i * 2], 16) shl 4) + Character.digit(this[i * 2 + 1], 16)).toByte()
    }
