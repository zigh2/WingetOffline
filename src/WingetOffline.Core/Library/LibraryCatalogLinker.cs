using WingetOffline.Core.Catalog;

namespace WingetOffline.Core.Library;

/// <summary>
/// يربط ملفات المكتبة التي لها بصمة SHA256 بحزم الكتالوج (مطابقة تامة)، فيرتفع ما عُرف عنها إلى ثقة High
/// ويُسجَّل معرّف winget وإصداره.
/// </summary>
public static class LibraryCatalogLinker
{
    /// <summary>يعيد عدد الملفات التي طابقت الكتالوج. لا يغيّر شيئاً لملف بلا بصمة.</summary>
    public static int Link(IEnumerable<LibraryEntry> entries, CatalogStore db)
    {
        var byHash = entries.Where(e => !string.IsNullOrEmpty(e.Sha256))
            .GroupBy(e => e.Sha256!.ToUpperInvariant()).ToDictionary(g => g.Key, g => g.ToList());
        if (byHash.Count == 0) return 0;

        int linked = 0;
        var hashes = byHash.Keys.ToList();
        lock (db.Sync)
        {
            for (int offset = 0; offset < hashes.Count; offset += 300)
            {
                var batch = hashes.Skip(offset).Take(300).ToList();
                // بعض المانيفستات تكتب البصمة بأحرف صغيرة: نبحث بالصيغتين
                var variants = batch.Concat(batch.Select(h => h.ToLowerInvariant())).ToList();
                var ph = string.Join(",", variants.Select((_, i) => "$h" + i));

                using var cmd = db.Conn.CreateCommand();
                cmd.CommandText =
                    "SELECT i.sha256, i.pkg_id, i.version, p.name, p.publisher FROM installers i " +
                    "JOIN packages p ON p.id = i.pkg_id WHERE i.sha256 IN (" + ph + ")";
                for (int i = 0; i < variants.Count; i++) cmd.Parameters.AddWithValue("$h" + i, variants[i]);

                var done = new HashSet<string>();
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var sha = r.GetString(0).ToUpperInvariant();
                    if (!done.Add(sha) || !byHash.TryGetValue(sha, out var list)) continue;
                    string id = r.GetString(1), version = r.GetString(2);
                    string? name = r.IsDBNull(3) ? null : r.GetString(3);
                    string? publisher = r.IsDBNull(4) ? null : r.GetString(4);
                    foreach (var e in list)
                    {
                        e.WingetId = id;
                        e.WingetVersion = version;
                        // المطابقة بالبصمة أدق من أي قراءة: نعتمد اسم الحزمة وإصدارها
                        if (e.Confidence < LibraryConfidence.High || e.ProductName == null)
                        {
                            if (!string.IsNullOrWhiteSpace(name)) e.ProductName = name;
                            e.Publisher ??= publisher;
                            e.Version = version;
                            e.Confidence = LibraryConfidence.High;
                        }
                        if (!e.Source.Contains("catalog", StringComparison.Ordinal)) e.Source = e.Source.Length == 0 ? "catalog" : e.Source + "+catalog";
                        linked++;
                    }
                }
            }
        }
        return linked;
    }
}
