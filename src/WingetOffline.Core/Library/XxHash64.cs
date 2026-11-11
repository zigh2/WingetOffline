using System.Buffers.Binary;
using System.Numerics;

namespace WingetOffline.Core.Library;

/// <summary>
/// xxHash64 (seed 0) بكود مُدار كامل بلا اعتماد خارجي، متوافق مع التطبيق المرجعي (XXH64).
/// خوارزمية غير تشفيرية فائقة السرعة: تصلح لتعريف الملفات وكشف التغيّر والتكرار، ولا تصلح للأمان أو لمطابقة الكتالوج.
/// البث (Append على أجزاء) يعطي النتيجة نفسها كأن البيانات أُعطيت دفعة واحدة. الصيغة النصية: 16 خانة سداسية كبيرة (canonical).
/// </summary>
public sealed class XxHash64
{
    private const ulong P1 = 11400714785074694791UL;
    private const ulong P2 = 14029467366897019727UL;
    private const ulong P3 = 1609587929392839161UL;
    private const ulong P4 = 9650029242287828579UL;
    private const ulong P5 = 2870177450012600261UL;

    private ulong _v1, _v2, _v3, _v4;
    private ulong _total;
    private readonly byte[] _buf = new byte[32];
    private int _bufLen;

    public XxHash64() => Reset();

    public void Reset()
    {
        _v1 = unchecked(P1 + P2);
        _v2 = P2;
        _v3 = 0;
        _v4 = unchecked(0UL - P1);
        _total = 0;
        _bufLen = 0;
    }

    private static ulong Round(ulong acc, ulong input) => BitOperations.RotateLeft(unchecked(acc + input * P2), 31) * P1;

    private static ulong Merge(ulong h, ulong v) => unchecked((h ^ Round(0, v)) * P1 + P4);

    public void Append(ReadOnlySpan<byte> data)
    {
        _total += (ulong)data.Length;

        if (_bufLen + data.Length < 32)
        {
            data.CopyTo(_buf.AsSpan(_bufLen));
            _bufLen += data.Length;
            return;
        }

        if (_bufLen > 0)
        {
            int fill = 32 - _bufLen;
            data[..fill].CopyTo(_buf.AsSpan(_bufLen));
            Stripe(_buf);
            data = data[fill..];
            _bufLen = 0;
        }

        while (data.Length >= 32)
        {
            Stripe(data);
            data = data[32..];
        }

        if (data.Length > 0)
        {
            data.CopyTo(_buf);
            _bufLen = data.Length;
        }
    }

    private void Stripe(ReadOnlySpan<byte> s)
    {
        _v1 = Round(_v1, BinaryPrimitives.ReadUInt64LittleEndian(s));
        _v2 = Round(_v2, BinaryPrimitives.ReadUInt64LittleEndian(s[8..]));
        _v3 = Round(_v3, BinaryPrimitives.ReadUInt64LittleEndian(s[16..]));
        _v4 = Round(_v4, BinaryPrimitives.ReadUInt64LittleEndian(s[24..]));
    }

    public ulong CurrentHash()
    {
        ulong h;
        if (_total >= 32)
        {
            h = BitOperations.RotateLeft(_v1, 1) + BitOperations.RotateLeft(_v2, 7)
              + BitOperations.RotateLeft(_v3, 12) + BitOperations.RotateLeft(_v4, 18);
            h = Merge(h, _v1); h = Merge(h, _v2); h = Merge(h, _v3); h = Merge(h, _v4);
        }
        else h = P5; // seed 0

        unchecked { h += _total; }

        var rest = _buf.AsSpan(0, _bufLen);
        while (rest.Length >= 8)
        {
            h ^= Round(0, BinaryPrimitives.ReadUInt64LittleEndian(rest));
            h = unchecked(BitOperations.RotateLeft(h, 27) * P1 + P4);
            rest = rest[8..];
        }
        if (rest.Length >= 4)
        {
            h ^= unchecked((ulong)BinaryPrimitives.ReadUInt32LittleEndian(rest) * P1);
            h = unchecked(BitOperations.RotateLeft(h, 23) * P2 + P3);
            rest = rest[4..];
        }
        foreach (var b in rest)
        {
            h ^= unchecked(b * P5);
            h = unchecked(BitOperations.RotateLeft(h, 11) * P1);
        }

        h ^= h >> 33; h = unchecked(h * P2);
        h ^= h >> 29; h = unchecked(h * P3);
        h ^= h >> 32;
        return h;
    }

    /// <summary>الصيغة القانونية: 16 خانة سداسية كبيرة (big-endian) بأحرف كبيرة.</summary>
    public string CurrentHashHex() => CurrentHash().ToString("X16");

    public static string HashHex(ReadOnlySpan<byte> data)
    {
        var x = new XxHash64();
        x.Append(data);
        return x.CurrentHashHex();
    }
}
