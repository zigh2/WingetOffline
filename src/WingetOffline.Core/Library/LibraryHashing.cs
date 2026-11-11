using System.Security.Cryptography;

namespace WingetOffline.Core.Library;

public enum LibraryHashMode
{
    /// <summary>لا بصمات.</summary>
    Off,
    /// <summary>بصمة الملف كاملاً (الوحيدة التي تطابق الكتالوج).</summary>
    Full,
    /// <summary>الملف الصغير كاملاً، والكبير (فوق الحد) على أول جزء منه فقط.</summary>
    Quick,
}

/// <summary>
/// XxHash64: الأسرع وغير تشفيري (الافتراضي؛ يصلح لتعريف الملفات وكشف التغيّر لا للأمان ولا لمطابقة الكتالوج).
/// Sha256: تشفيري وهو الوحيد الذي يطابق الكتالوج. Md5: للتوافق مع مواقع تنشر MD5.
/// </summary>
public enum LibraryHashAlgorithm { XxHash64, Sha256, Md5 }

/// <summary>
/// سياسة حساب البصمات. الوضع السريع (Quick): الملفات حتى <see cref="QuickThresholdBytes"/> (30MB افتراضياً) تُبصَم كاملة،
/// وما فوقها يُبصَم على أول <see cref="QuickHeadBytes"/> (10MB افتراضياً) فقط، فيُحسب الجزء الأول وحده مهما كان الملف ضخماً.
/// </summary>
public sealed class LibraryHashPolicy
{
    public LibraryHashMode Mode { get; init; } = LibraryHashMode.Off;
    public LibraryHashAlgorithm Algorithm { get; init; } = LibraryHashAlgorithm.XxHash64;
    public long QuickThresholdBytes { get; init; } = 30L * 1024 * 1024;
    public long QuickHeadBytes { get; init; } = 10L * 1024 * 1024;

    public static LibraryHashPolicy Off { get; } = new();
    public static LibraryHashPolicy FullSha256 { get; } = new() { Mode = LibraryHashMode.Full, Algorithm = LibraryHashAlgorithm.Sha256 };

    /// <summary>هل يُبصَم هذا الملف على أول جزء منه فقط؟</summary>
    public bool WantsPartial(long size) => Mode == LibraryHashMode.Quick && size > QuickThresholdBytes;

    /// <summary>هل يُبصَم هذا الملف كاملاً؟</summary>
    public bool WantsFull(long size) => Mode == LibraryHashMode.Full || (Mode == LibraryHashMode.Quick && size <= QuickThresholdBytes);

    public string AlgorithmName => Algorithm switch { LibraryHashAlgorithm.Md5 => "MD5", LibraryHashAlgorithm.Sha256 => "SHA256", _ => "XXH64" };

    /// <summary>هل ينقص هذا السجل بصمة تطلبها السياسة؟</summary>
    internal bool Needs(LibraryEntry e, long maxFullBytes)
    {
        if (Mode == LibraryHashMode.Off) return false;
        if (WantsPartial(e.Size))
            return e.PartialHash == null || e.PartialHashAlgorithm != AlgorithmName || e.PartialHashBytes != QuickHeadBytes;
        if (WantsFull(e.Size))
        {
            if (e.Size > maxFullBytes) return false;
            return Algorithm switch
            {
                LibraryHashAlgorithm.Sha256 => e.Sha256 == null,
                LibraryHashAlgorithm.Md5 => e.Md5 == null,
                _ => e.XxHash == null,
            };
        }
        return false;
    }
}

public static class LibraryHasher
{
    /// <param name="firstBytes">null: الملف كاملاً؛ وإلا أول هذا العدد من البايتات فقط.</param>
    public static string Compute(string path, LibraryHashAlgorithm algorithm, long? firstBytes, CancellationToken ct = default)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20);
        var buf = new byte[1 << 20];
        long remaining = firstBytes ?? long.MaxValue;

        if (algorithm == LibraryHashAlgorithm.XxHash64)
        {
            var x = new XxHash64();
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, remaining));
                if (n <= 0) break;
                x.Append(buf.AsSpan(0, n));
                remaining -= n;
            }
            return x.CurrentHashHex();
        }

        using var h = IncrementalHash.CreateHash(algorithm == LibraryHashAlgorithm.Md5 ? HashAlgorithmName.MD5 : HashAlgorithmName.SHA256);
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, remaining));
            if (n <= 0) break;
            h.AppendData(buf, 0, n);
            remaining -= n;
        }
        return Convert.ToHexString(h.GetHashAndReset());
    }
}
