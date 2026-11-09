using System.Text.RegularExpressions;
using WingetOffline.Core.Matching;
using WingetOffline.Core.Scanning;
using WingetOffline.Core.Versioning;

namespace WingetOffline.Core.Library;

/// <summary>علاقة إصدار المكتبة بالإصدار المثبّت.</summary>
public enum LibraryRelation { None, Same, LibraryNewer, LibraryOlder, Unknown }

/// <summary>نتيجة مطابقة برنامج مثبّت مع ملفات المكتبة.</summary>
public sealed class LibraryMatch
{
    /// <summary>أفضل ملف: أعلى إصدار بين المرشّحين المطابقين.</summary>
    public LibraryEntry Best { get; init; } = null!;
    public LibraryRelation Relation { get; init; }
    public Confidence Confidence { get; init; }
    public string Method { get; init; } = "";
    /// <summary>كل الملفات المطابقة (إصدارات متعددة من البرنامج نفسه).</summary>
    public IReadOnlyList<LibraryEntry> Candidates { get; init; } = Array.Empty<LibraryEntry>();
    /// <summary>هل بين الملفات ما يساوي الإصدار المثبّت تماماً؟</summary>
    public bool HasSameVersion { get; init; }

    /// <summary>نص قصير للجدول: "Newer 26.05" / "Same" / "Older 25.1".</summary>
    public string ShortText => Relation switch
    {
        LibraryRelation.LibraryNewer => "Newer " + Best.Version,
        LibraryRelation.LibraryOlder => HasSameVersion ? "Same" : "Older " + Best.Version,
        LibraryRelation.Same => "Same",
        _ => string.IsNullOrWhiteSpace(Best.Version) ? "In library" : "In library " + Best.Version,
    };
}

/// <summary>علاقة ملف في المكتبة ببرنامج مثبّت (الاتجاه المعاكس لعرض صفحة Local).</summary>
public sealed record InstalledLink(string AppName, string? InstalledVersion, LibraryRelation Relation, Confidence Confidence);

public sealed class LibraryLinks
{
    public Dictionary<InstalledApp, LibraryMatch> ByApp { get; } = new();
    public Dictionary<string, InstalledLink> ByPath { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// يطابق البرامج المثبّتة مع ملفات المكتبة. المفاتيح بترتيب الدقة:
/// ProductCode لملفات MSI ← الاسم الموحّد + تشابه الناشر ← الاسم الموحّد وحده (ثقة أقل).
/// لا نطابق بالاسم عندما يختلف الناشر الصريح (برنامجان مختلفان باسم متشابه).
/// </summary>
public sealed class LibraryMatcher
{
    private static readonly Regex GuidRx = new(@"\{?([0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})\}?", RegexOptions.Compiled);

    private sealed record Item(LibraryEntry Entry, string Name, string Stripped, string Publisher);

    private readonly Dictionary<string, List<Item>> _byCode = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Item>> _byName = new(StringComparer.Ordinal);

    public int Count { get; }

    public LibraryMatcher(IEnumerable<LibraryEntry> entries)
    {
        foreach (var e in entries)
        {
            // ملفات الأرشيف والصور بلا اسم موثوق لا تُستخدم للمطابقة
            if (e.Confidence == LibraryConfidence.None || string.IsNullOrWhiteSpace(e.ProductName)) continue;
            var name = Normalizer.Name(e.ProductName);
            if (name.Length == 0) continue;
            var item = new Item(e, name, Normalizer.StripQualifiers(name), Normalizer.Publisher(e.Publisher));
            Count++;

            var code = Guid(e.ProductCode);
            if (code != null) Add(_byCode, code, item);
            Add(_byName, item.Name, item);
            if (item.Stripped != item.Name) Add(_byName, item.Stripped, item);
        }
    }

    private static void Add(Dictionary<string, List<Item>> d, string key, Item item)
    {
        if (!d.TryGetValue(key, out var l)) d[key] = l = new List<Item>();
        if (!l.Contains(item)) l.Add(item);
    }

    private static string? Guid(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var m = GuidRx.Match(s);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    /// <summary>يبحث عن ملفات المكتبة المطابقة للبرنامج؛ null إن لم يوجد.</summary>
    public LibraryMatch? Find(InstalledApp app, string? installedVersion)
    {
        List<Item>? cands = null;
        Confidence conf = Confidence.None;
        string method = "";

        // 1) ProductCode (يطابق الإصدار نفسه تماماً)
        var code = Guid(app.ProductCode) ?? Guid(app.RegistryKeyName);
        if (code != null && _byCode.TryGetValue(code, out var byCode))
        {
            cands = byCode; conf = Confidence.High; method = "ProductCode";
        }

        // 2) الاسم الموحّد
        if (cands == null)
        {
            var name = Normalizer.Name(app.DisplayName);
            if (name.Length == 0) return null;
            var stripped = Normalizer.StripQualifiers(name);
            _byName.TryGetValue(name, out var l1);
            _byName.TryGetValue(stripped, out var l2);
            var pool = (l1 ?? new List<Item>()).Concat(l2 ?? new List<Item>()).Distinct().ToList();
            if (pool.Count == 0) return null;

            var appPub = Normalizer.Publisher(app.Publisher);
            var good = new List<Item>();
            var unknownPub = new List<Item>();
            foreach (var it in pool)
            {
                if (appPub.Length > 0 && it.Publisher.Length > 0)
                {
                    if (Normalizer.PublisherSimilar(appPub, it.Publisher)) good.Add(it);
                    // ناشر مختلف صراحةً: ليس المنتج نفسه
                }
                else unknownPub.Add(it);
            }

            if (good.Count > 0) { cands = good; conf = Confidence.High; method = "Name+Publisher"; }
            else if (unknownPub.Count > 0)
            {
                cands = unknownPub;
                bool allFromContent = unknownPub.All(i => !i.Entry.Source.Equals("filename", StringComparison.Ordinal));
                conf = allFromContent ? Confidence.Medium : Confidence.Low;
                method = "Name";
            }
            else return null;
        }

        var entries = cands.Select(c => c.Entry).Distinct().ToList();
        var instVer = Normalizer.ComparableVersion(installedVersion);

        // أعلى إصدار أولاً؛ ما بلا إصدار في الأخير
        var ordered = entries
            .OrderByDescending(e => Normalizer.ComparableVersion(e.Version), WingetVersionComparer.Instance)
            .ThenByDescending(e => e.Confidence).ToList();
        var best = ordered[0];

        bool same = instVer.Length > 0 && entries.Any(e => Normalizer.ComparableVersion(e.Version).Length > 0
            && WingetVersionComparer.Instance.Compare(Normalizer.ComparableVersion(e.Version), instVer) == 0);
        var rel = Relate(best.Version, instVer);
        return new LibraryMatch { Best = best, Relation = rel, Confidence = conf, Method = method, Candidates = ordered, HasSameVersion = same };
    }

    private static LibraryRelation Relate(string? libVersion, string installedComparable)
    {
        var lv = Normalizer.ComparableVersion(libVersion);
        if (lv.Length == 0 || installedComparable.Length == 0) return LibraryRelation.Unknown;
        int c = WingetVersionComparer.Instance.Compare(lv, installedComparable);
        return c > 0 ? LibraryRelation.LibraryNewer : c < 0 ? LibraryRelation.LibraryOlder : LibraryRelation.Same;
    }

    /// <summary>
    /// يربط كل البرامج المثبّتة بالمكتبة في الاتجاهين: لكل برنامج أفضل ملف، ولكل ملف البرنامج الذي يطابقه.
    /// </summary>
    public LibraryLinks Link(IEnumerable<(InstalledApp App, string? Version)> installed)
    {
        var links = new LibraryLinks();
        foreach (var (app, version) in installed)
        {
            var m = Find(app, version);
            if (m == null) continue;
            links.ByApp[app] = m;
            var inst = Normalizer.ComparableVersion(version);
            foreach (var c in m.Candidates)
            {
                var rel = Relate(c.Version, inst);
                // إن طابق الملفُ أكثر من برنامج نُبقي الأعلى ثقة
                if (!links.ByPath.TryGetValue(c.Path, out var old) || old.Confidence < m.Confidence)
                    links.ByPath[c.Path] = new InstalledLink(app.DisplayName, version, rel, m.Confidence);
            }
        }
        return links;
    }
}
