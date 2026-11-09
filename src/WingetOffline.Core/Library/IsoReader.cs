using System.Text;

namespace WingetOffline.Core.Library;

/// <summary>
/// يقرأ واصف الحجم الأساسي (ISO 9660) من ISO بقراءة 64KB فقط، مهما كان حجم الملف:
/// اسم القرص (Volume ID) والناشر والتطبيق وتاريخ الإنشاء.
/// </summary>
public static class IsoReader
{
    public sealed record IsoInfo(string VolumeId, string? Publisher, string? Application, DateTime? Created);

    public static IsoInfo? Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 12);
            // الواصفات تبدأ في القطاع 16 (الإزاحة 0x8000) وحجم القطاع 2048
            for (int sector = 16; sector < 32; sector++)
            {
                long off = sector * 2048L;
                if (off + 2048 > fs.Length) return null;
                var b = new byte[2048];
                fs.Position = off;
                if (fs.Read(b, 0, 2048) != 2048) return null;

                if (b[1] != 'C' || b[2] != 'D' || b[3] != '0' || b[4] != '0' || b[5] != '1') continue;
                if (b[0] == 255) return null;      // نهاية الواصفات
                if (b[0] != 1) continue;           // نبحث عن الواصف الأساسي (النوع 1)

                string Field(int start, int len) => Encoding.ASCII.GetString(b, start, len).Trim(' ', '\0');
                return new IsoInfo(Field(40, 32), NullIfEmpty(Field(318, 128)), NullIfEmpty(Field(574, 128)), ParseDate(Field(813, 16)));
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    private static DateTime? ParseDate(string s)
    {
        if (s.Length < 14 || !s.Take(14).All(char.IsDigit)) return null;
        return DateTime.TryParseExact(s[..14], "yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) && d.Year > 1980
            ? d : null;
    }
}
