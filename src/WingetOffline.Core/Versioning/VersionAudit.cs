using System.Globalization;
using WingetOffline.Core.Catalog;

namespace WingetOffline.Core.Versioning;

public sealed record AuditViolation(string PackageId, string OlderByDate, string OlderDate, string NewerByDate, string NewerDate, int GapDays);

/// <summary>تقرير التدقيق. فجوة قصيرة (حتى 30 يوماً) تدل غالباً على فروع موازية أو إصلاحات؛ فجوة طويلة قد تدل على تواريخ خاطئة أو قاعدة مقارنة خاطئة.</summary>
public sealed record AuditReport(int PackagesChecked, int PackagesWithViolations, List<AuditViolation> Samples,
                                 List<AuditViolation> All, int GapUpTo30Days, int Gap31To365Days, int GapOver365Days);

/// <summary>
/// يتحقق من مقارِن الإصدارات على بيانات حقيقية: لكل حزمة لها تواريخ إصدار، يجب ألا يكون إصدار تاريخه أقدم
/// أعلى (بحسب المقارِن) من إصدار تاريخه أحدث. وجود مخالفات لا يعني خطأً دائماً (تصحيحات لفروع قديمة)،
/// لكن نسبة مرتفعة أو أنماطاً متكررة تدل على قاعدة مقارنة خاطئة.
/// </summary>
public static class VersionAudit
{
    public static AuditReport Run(CatalogStore db, int sampleLimit = 25)
    {
        var cmp = WingetVersionComparer.Instance;
        int checkedPackages = 0, violating = 0, gapShort = 0, gapMid = 0, gapLong = 0;
        var samples = new List<AuditViolation>();
        var all = new List<AuditViolation>();

        lock (db.Sync)
        {
            using var cmd = db.Conn.CreateCommand();
            cmd.CommandText = "SELECT pkg_id, version, release_date FROM versions " +
                              "WHERE release_date IS NOT NULL AND release_date <> '' ORDER BY pkg_id, release_date";
            using var r = cmd.ExecuteReader();

            string? current = null;
            var group = new List<(string Version, string Date)>();

            void Flush()
            {
                if (current == null || group.Count < 2) { group.Clear(); return; }
                checkedPackages++;
                AuditViolation? first = null;
                for (int i = 0; i + 1 < group.Count; i++)
                {
                    var a = group[i]; var b = group[i + 1];
                    if (a.Date == b.Date) continue;                       // نفس اليوم: ترتيب غير محدد
                    if (cmp.Compare(a.Version, b.Version) > 0)
                    {
                        int gap = DateTime.TryParse(a.Date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d1) &&
                                  DateTime.TryParse(b.Date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2)
                            ? (int)(d2 - d1).TotalDays : -1;
                        first = new AuditViolation(current, a.Version, a.Date, b.Version, b.Date, gap);
                        break;
                    }
                }
                if (first != null)
                {
                    violating++;
                    all.Add(first);
                    if (first.GapDays >= 0)
                    {
                        if (first.GapDays <= 30) gapShort++;
                        else if (first.GapDays <= 365) gapMid++;
                        else gapLong++;
                    }
                    if (samples.Count < sampleLimit) samples.Add(first);
                }
                group.Clear();
            }

            while (r.Read())
            {
                var id = r.GetString(0);
                if (id != current) { Flush(); current = id; }
                group.Add((r.GetString(1), r.GetString(2)));
            }
            Flush();
        }
        return new AuditReport(checkedPackages, violating, samples, all, gapShort, gapMid, gapLong);
    }
}
