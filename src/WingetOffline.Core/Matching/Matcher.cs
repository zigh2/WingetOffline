using System.Diagnostics;
using Microsoft.Data.Sqlite;
using WingetOffline.Core.Catalog;
using WingetOffline.Core.Scanning;
using WingetOffline.Core.Versioning;

namespace WingetOffline.Core.Matching;

/// <summary>
/// يطابق البرامج المكتشفة مع الكتالوج بترتيب الدقة:
/// winget-folder ← ProductCode ← اسم Apps&amp;Features ← الأمر (للمحمولة) ← اسم+ناشر ← تطابق تقريبي.
/// </summary>
public sealed class Matcher
{
    private sealed record Pkg(string Id, string Name, string NormName, string NormPublisher, string? Latest, HashSet<string> Tokens);

    private readonly CatalogStore _db;
    private readonly List<Pkg> _all = new();
    private readonly Dictionary<string, Pkg> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Pkg>> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Pkg>> _byCommand = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _cmdsById = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, List<string>> _versionsCache = new(StringComparer.OrdinalIgnoreCase);

    public int PackageCount => _all.Count;

    public Matcher(CatalogStore db)
    {
        _db = db;

        using (var cmd = db.Conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name, publisher, latest_version FROM packages";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var name = S(r, 1) ?? "";
                var norm = Normalizer.Name(name);
                var p = new Pkg(r.GetString(0), name, norm, Normalizer.Publisher(S(r, 2)), S(r, 3), Normalizer.Tokens(norm));
                _all.Add(p);
                _byId[p.Id] = p;
                if (norm.Length > 0) Add(_byName, norm, p);
            }
        }

        using (var cmd = db.Conn.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT pkg_id, commands FROM installers WHERE commands IS NOT NULL AND commands <> ''";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!_byId.TryGetValue(r.GetString(0), out var p)) continue;
                foreach (var c in r.GetString(1).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    Add(_byCommand, c, p);
                    if (!_cmdsById.TryGetValue(p.Id, out var l)) _cmdsById[p.Id] = l = new List<string>();
                    if (!l.Contains(c, StringComparer.OrdinalIgnoreCase)) l.Add(c);
                }
            }
        }
    }

    public MatchResult Match(InstalledApp a)
    {
        // 0) برنامج محمول ثبّته winget: المعرّف في اسم المجلد
        if (a.HintPackageId != null && _byId.TryGetValue(a.HintPackageId, out var hint))
            return Make(a, hint, Confidence.High, "WinGet folder", FindExeVersion(a.InstallLocation, hint.Id),
                        "version read from exe; may differ from package version");

        // 1) ProductCode (أدق مفتاح)
        if (!string.IsNullOrEmpty(a.ProductCode))
        {
            var rows = ByProductCode(a.ProductCode);
            if (rows.Count > 0)
            {
                var g = rows.GroupBy(r => r.Pkg).OrderByDescending(x => x.Count()).First();
                if (_byId.TryGetValue(g.Key, out var p))
                {
                    var ver = g.OrderBy(r => r.Ver == a.DisplayVersion ? 0 : 1).First().Ver;
                    var multi = rows.Select(r => r.Pkg).Distinct().Count() > 1;
                    return Make(a, p, Confidence.High, "ProductCode", ver, multi ? "several packages share this ProductCode" : null);
                }
            }
        }

        var pub = Normalizer.Publisher(a.Publisher);

        // 2) اسم العرض في Apps & Features كما هو في المانيفست
        if (!string.IsNullOrWhiteSpace(a.DisplayName))
        {
            var rows = ByArpName(a.DisplayName);
            if (rows.Count > 0)
            {
                var exactVer = rows.Where(r => r.DisplayVersion == a.DisplayVersion).ToList();
                var pool = exactVer.Count > 0 ? exactVer : rows;
                var withPub = pool.Where(r => Normalizer.PublisherSimilar(pub, Normalizer.Publisher(r.Publisher))).ToList();
                if (withPub.Count > 0) pool = withPub;
                var best = pool.GroupBy(r => r.Pkg).OrderByDescending(x => x.Count()).First().First();
                if (_byId.TryGetValue(best.Pkg, out var p))
                {
                    bool known = exactVer.Count > 0 || VersionKnown(p.Id, a.DisplayVersion);
                    var conf = known ? Confidence.High : withPub.Count > 0 ? Confidence.Medium : Confidence.Low;
                    return Make(a, p, conf, exactVer.Count == 0 && known ? "ARP name+version" : "ARP name",
                                exactVer.Count > 0 ? best.Ver : null);
                }
            }
        }

        // 3) المحمولة: اسم الملف التنفيذي = أمر (Command) في المانيفست
        if (!string.IsNullOrEmpty(a.ExeName) && _byCommand.TryGetValue(a.ExeName, out var byCmd))
        {
            var norm0 = Normalizer.Name(a.DisplayName);
            var good = byCmd.FirstOrDefault(c => c.NormName == norm0 || Normalizer.PublisherSimilar(pub, c.NormPublisher));
            if (good != null) return Make(a, good, Confidence.Medium, "Command+Name/Publisher");
            if (byCmd.Count == 1) return Make(a, byCmd[0], Confidence.Low, "Command only");
        }

        // 4) الاسم الموحّد + الناشر + (تأكيد وجود الإصدار المثبّت في الكتالوج)
        var norm = Normalizer.Name(a.DisplayName);
        string? rejectedNote = null;
        if (norm.Length >= 2 && _byName.TryGetValue(norm, out var cands))
        {
            var withPub = cands.Where(c => Normalizer.PublisherSimilar(pub, c.NormPublisher)).ToList();
            if (withPub.Count > 0)
            {
                var confirmedPub = withPub.FirstOrDefault(c => VersionKnown(c.Id, a.DisplayVersion));
                var pick = confirmedPub ?? withPub[0];
                return Make(a, pick, confirmedPub != null ? Confidence.High : Confidence.Medium,
                            confirmedPub != null ? "Name+Publisher+Version" : "Name+Publisher", null,
                            withPub.Count > 1 && confirmedPub == null ? $"{withPub.Count} candidates" : null);
            }

            var confirmed = cands.FirstOrDefault(c => VersionKnown(c.Id, a.DisplayVersion));
            if (confirmed != null)
                return Make(a, confirmed, Confidence.Medium, "Name+Version", null, pub.Length > 0 ? "publisher differs" : null);

            // اسم مطابق فقط: نرفضه إن كان الناشر مختلفاً أو الإصدار المثبّت أحدث من آخر إصدار في الكتالوج
            var first = cands[0];
            bool newer = IsNewerThanLatest(a.DisplayVersion, first.Latest);
            if (pub.Length > 0 || newer)
                rejectedNote = $"candidate {first.Id} rejected: " +
                               (pub.Length > 0 ? "publisher differs and version not in catalog" : "installed version is newer than catalog");
            else
                return Make(a, first, Confidence.Low, "Name only", null, cands.Count > 1 ? $"{cands.Count} candidates" : null);
        }

        // 5) تطابق تقريبي (يشترط تشابه الناشر لتفادي الأخطاء)
        if (pub.Length > 0 && norm.Length >= 3)
        {
            var tokens = Normalizer.Tokens(norm);
            Pkg? bestPkg = null; double score = 0;
            foreach (var p in _all)
            {
                if (!Normalizer.PublisherSimilar(pub, p.NormPublisher)) continue;
                var j = Normalizer.Jaccard(tokens, p.Tokens);
                int inter = tokens.Count(p.Tokens.Contains);
                int min = Math.Min(tokens.Count, p.Tokens.Count);
                int diff = tokens.Count + p.Tokens.Count - 2 * inter;
                bool close = j >= 0.8 || (min >= 2 && inter == min && diff <= 1);
                if (close && j > score) { score = j; bestPkg = p; }
            }
            if (bestPkg != null)
            {
                bool known = VersionKnown(bestPkg.Id, a.DisplayVersion);
                return Make(a, bestPkg, known ? Confidence.Medium : Confidence.Low,
                            known ? "Fuzzy name+publisher+version" : "Fuzzy name+publisher", null, $"score {score:0.00}");
            }
        }

        return new MatchResult { App = a, Confidence = Confidence.None, Method = "No match", InstalledVersion = a.DisplayVersion, Note = rejectedNote };
    }

    // ---------- helpers ----------

    private MatchResult Make(InstalledApp a, Pkg p, Confidence conf, string method, string? matchedVersion = null, string? note = null)
    {
        var installed = matchedVersion ?? a.DisplayVersion;
        var status = UpdateStatus.Unknown;
        if (!string.IsNullOrWhiteSpace(installed) && !string.IsNullOrWhiteSpace(p.Latest))
            status = WingetVersionComparer.Instance.Compare(Normalizer.ComparableVersion(installed), p.Latest) < 0
                ? UpdateStatus.UpdateAvailable : UpdateStatus.UpToDate;

        if (conf == Confidence.Low && status == UpdateStatus.UpdateAvailable)
            note = (note == null ? "" : note + "; ") + "low-confidence match, verify before updating";

        return new MatchResult
        {
            App = a, PackageId = p.Id, PackageName = p.Name, Confidence = conf, Method = method,
            InstalledVersion = installed, LatestVersion = p.Latest, Status = status, Note = note,
        };
    }

    /// <summary>هل الإصدار المثبّت موجود فعلاً بين إصدارات الحزمة في الكتالوج؟ (قرينة قوية على صحة المطابقة)</summary>
    private bool VersionKnown(string pkgId, string? installed)
    {
        var inst = Normalizer.ComparableVersion(installed);
        if (inst.Length == 0) return false;
        if (!_versionsCache.TryGetValue(pkgId, out var list))
        {
            list = new List<string>();
            using var cmd = _db.Conn.CreateCommand();
            cmd.CommandText = "SELECT version FROM versions WHERE pkg_id = $p";
            cmd.Parameters.AddWithValue("$p", pkgId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetString(0));
            _versionsCache[pkgId] = list;
        }
        return list.Any(v => WingetVersionComparer.Instance.Compare(v, inst) == 0);
    }

    private static bool IsNewerThanLatest(string? installed, string? latest)
    {
        var inst = Normalizer.ComparableVersion(installed);
        return inst.Length > 0 && !string.IsNullOrWhiteSpace(latest) &&
               WingetVersionComparer.Instance.Compare(inst, latest) > 0;
    }

    private List<(string Pkg, string Ver)> ByProductCode(string key)
    {
        var variants = new HashSet<string>(StringComparer.Ordinal) { key, key.ToUpperInvariant() };
        if (key.EndsWith("_is1", StringComparison.OrdinalIgnoreCase)) variants.Add(key[..^4].ToUpperInvariant() + "_is1");
        var list = variants.ToList();
        var ph = string.Join(",", list.Select((_, i) => "$p" + i));

        using var cmd = _db.Conn.CreateCommand();
        cmd.CommandText = $"SELECT pkg_id, version FROM arp WHERE product_code IN ({ph}) " +
                          $"UNION SELECT pkg_id, version FROM installers WHERE product_code IN ({ph})";
        for (int i = 0; i < list.Count; i++) cmd.Parameters.AddWithValue("$p" + i, list[i]);

        var res = new List<(string, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) res.Add((r.GetString(0), r.GetString(1)));
        return res;
    }

    private List<(string Pkg, string Ver, string? Publisher, string? DisplayVersion)> ByArpName(string name)
    {
        using var cmd = _db.Conn.CreateCommand();
        cmd.CommandText = "SELECT pkg_id, version, publisher, display_version FROM arp WHERE display_name = $n LIMIT 200";
        cmd.Parameters.AddWithValue("$n", name);
        var res = new List<(string, string, string?, string?)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) res.Add((r.GetString(0), r.GetString(1), S(r, 2), S(r, 3)));
        return res;
    }

    private string? FindExeVersion(string? location, string pkgId)
    {
        if (string.IsNullOrEmpty(location) || !Directory.Exists(location) || !_cmdsById.TryGetValue(pkgId, out var cmds)) return null;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true };
        foreach (var c in cmds)
        {
            try
            {
                var f = Directory.EnumerateFiles(location, c + ".exe", opts).FirstOrDefault();
                if (f == null) continue;
                var vi = FileVersionInfo.GetVersionInfo(f);
                var v = vi.ProductVersion ?? vi.FileVersion;
                if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            }
            catch { /* نتجاوز */ }
        }
        return null;
    }

    private static void Add(Dictionary<string, List<Pkg>> d, string key, Pkg p)
    {
        if (!d.TryGetValue(key, out var l)) d[key] = l = new List<Pkg>();
        if (!l.Contains(p)) l.Add(p);
    }

    private static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}
