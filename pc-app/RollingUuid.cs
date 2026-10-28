using System.Security.Cryptography;

namespace ProximityLock;

/// <summary>
/// 滚动 UUID (防伪造): 当前 UUID = HMAC-SHA256(seed, 时间片号大端8字节) 前 16 字节,
/// 时间片号 = unix秒 / 60。与安卓端 RollingUuid.kt 算法逐字节一致, 两边必须同步改动。
/// </summary>
public static class RollingUuid
{
    public const long SliceSeconds = 60;

    /// <summary>当前时间片号 (按 Unix 时间对齐, 与安卓 System.currentTimeMillis()/1000 同源)</summary>
    public static long SliceAt(long? unixSeconds = null)
        => (unixSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()) / SliceSeconds;

    /// <summary>由种子 + 时间片推导 UUID。安卓端构造方式为 UUID(hmac[0..8], hmac[8..16]) 大端, 两者字符串相同。</summary>
    public static Guid Derive(string seedHex, long slice)
    {
        using var hmac = new HMACSHA256(HexToBytes(seedHex));
        Span<byte> msg = stackalloc byte[8];
        // 大端 8 字节 (与 Kotlin ByteBuffer.putLong 一致)
        for (int i = 7; i >= 0; i--)
        {
            msg[i] = (byte)(slice & 0xFF);
            slice >>= 8;
        }
        var outBytes = hmac.ComputeHash(msg.ToArray());
        return GuidFromBigEndian(outBytes.AsSpan(0, 16));
    }

    public static string RandomSeed()
    {
        var b = RandomNumberGenerator.GetBytes(16);
        return Convert.ToHexString(b).ToLowerInvariant();
    }

    public static bool IsValidSeed(string? text)
        => !string.IsNullOrWhiteSpace(text) && text.Trim().Length == 32
           && text.Trim().All(c => Uri.IsHexDigit(c));

    public static byte[] HexToBytes(string hex)
    {
        hex = hex.Trim();
        var b = new byte[hex.Length / 2];
        for (int i = 0; i < b.Length; i++)
            b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return b;
    }

    /// <summary>蓝牙大端字节流转 Guid (与 BeaconWatcher 里同一套逻辑, 保持字符串同序)</summary>
    public static Guid GuidFromBigEndian(ReadOnlySpan<byte> b)
    {
        Span<char> hex = stackalloc char[32];
        for (var i = 0; i < 16; i++)
        {
            b[i].TryFormat(hex.Slice(i * 2, 2), out _, "x2");
        }
        return new Guid($"{hex[0..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}");
    }
}
