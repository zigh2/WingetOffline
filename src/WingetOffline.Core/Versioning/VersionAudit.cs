using WingetOffline.Core.Catalog;

namespace WingetOffline.Core.Versioning;

public sealed record AuditViolation(string PackageId, string OlderByDate, string OlderDate, string NewerByDate, string NewerDate);
public sealed record AuditReport(int PackagesChecked, int PackagesWithViolations, List<AuditViolation> Samples);

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
        int checkedPackages = 0, violating = 0;
        var samples = new List<AuditViolation>();

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
                        first = new AuditViolation(current, a.Version, a.Date, b.Version, b.Date);
                        break;
                    }
                }
                if (first != null)
                {
                    violating++;
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
        return new AuditReport(checkedPackages, violating, samples);
    }
}
