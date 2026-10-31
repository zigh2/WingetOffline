using System.Text.RegularExpressions;

namespace WingetOffline.Core.Matching;

public static class Normalizer
{
    private static readonly Regex ArchParen = new(
        @"\((?=[^)]*(?:x64|x86|amd64|arm64|bit|\d))[^)]*\)|\[(?=[^\]]*(?:x64|x86|amd64|arm64|bit|\d))[^\]]*\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex VersionToken = new(@"\bv?\d+(?:\.\d+)+\w*\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ArchWord = new(@"\b(?:x64|x86|amd64|arm64|win64|win32|64\s?bit|32\s?bit)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ScopeParen = new(@"\((?:user|system|machine|all users|per[- ]user)\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DottedNumber = new(@"\d+(?:\.\d+)+", RegexOptions.Compiled);
    private static readonly Regex AnyNumber = new(@"\d+", RegexOptions.Compiled);
    private static readonly Regex NonAlnum = new(@"[^\p{L}\p{Nd}]+", RegexOptions.Compiled);

    private static readonly HashSet<string> PublisherStop = new(StringComparer.Ordinal)
    {
        "inc", "llc", "ltd", "limited", "corp", "corporation", "co", "company", "gmbh", "sa", "ag", "bv", "plc", "pty", "the", "software"
    };

    /// <summary>اسم موحّد: أحرف صغيرة، بلا إصدارات ولا معمارية ولا رموز.</summary>
    public static string Name(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.ToLowerInvariant();
        s = ScopeParen.Replace(s, " ");
        s = ArchParen.Replace(s, " ");
        s = VersionToken.Replace(s, " ");
        s = ArchWord.Replace(s, " ");
        s = NonAlnum.Replace(s, " ");
        return s.Trim();
    }

    /// <summary>إصدار قابل للمقارنة: "ad 9.8.1" ← "9.8.1" ، "4.21 Final-1 build" ← "4.21".</summary>
    public static string ComparableVersion(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return "";
        v = v.Trim();
        bool startsLikeVersion = char.IsDigit(v[0]) || (v.Length > 1 && (v[0] == 'v' || v[0] == 'V') && char.IsDigit(v[1]));
        if (startsLikeVersion && !v.Any(char.IsWhiteSpace)) return v;
        var m = DottedNumber.Match(v);
        if (m.Success) return m.Value;
        m = AnyNumber.Match(v);
        return m.Success ? m.Value : v;
    }

    public static string Publisher(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var tokens = NonAlnum.Replace(s.ToLowerInvariant(), " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !PublisherStop.Contains(t));
        return string.Join(' ', tokens);
    }

    public static bool PublisherSimilar(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return false;
        if (a == b) return true;
        if (Math.Min(a.Length, b.Length) >= 3 && (a.Contains(b) || b.Contains(a))) return true;
        var tb = b.Split(' ');
        return a.Split(' ').Any(t => t.Length >= 4 && tb.Contains(t));
    }

    public static HashSet<string> Tokens(string normalizedName) =>
        new(normalizedName.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    public static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        int inter = a.Count(b.Contains);
        return (double)inter / (a.Count + b.Count - inter);
    }
}
