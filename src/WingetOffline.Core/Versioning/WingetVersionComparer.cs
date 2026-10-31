using System.Text.RegularExpressions;

namespace WingetOffline.Core.Versioning;

/// <summary>
/// مقارن إصدارات يشبه قواعد winget: تقسيم على . و - و + ، كل جزء رقم + لاحقة.
/// الجزء الذي له لاحقة نصية (beta, rc) يُعدّ أقل من الجزء الرقمي الصرف.
/// </summary>
public sealed class WingetVersionComparer : IComparer<string>
{
    public static readonly WingetVersionComparer Instance = new();
    private static readonly Regex Part = new(@"^(\d*)(.*)$", RegexOptions.Compiled);

    public int Compare(string? a, string? b)
    {
        var pa = Split(a); var pb = Split(b);
        int n = Math.Max(pa.Count, pb.Count);
        for (int i = 0; i < n; i++)
        {
            var (na, sa) = i < pa.Count ? pa[i] : (0m, "");
            var (nb, sb) = i < pb.Count ? pb[i] : (0m, "");
            int c = na.CompareTo(nb);
            if (c != 0) return c;
            if (sa == sb) continue;
            if (sa.Length == 0) return 1;   // 1.0 > 1.0-beta
            if (sb.Length == 0) return -1;
            c = string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
        }
        return 0;
    }

    private static List<(decimal num, string suffix)> Split(string? v)
    {
        var list = new List<(decimal, string)>();
        if (string.IsNullOrWhiteSpace(v)) return list;
        v = v.Trim();
        if (v.Length > 1 && (v[0] == 'v' || v[0] == 'V') && char.IsDigit(v[1])) v = v[1..];
        foreach (var raw in v.Split('.', '-', '+', '_'))
        {
            var m = Part.Match(raw);
            decimal num = 0;
            if (m.Groups[1].Length > 0 && m.Groups[1].Length < 20) decimal.TryParse(m.Groups[1].Value, out num);
            list.Add((num, m.Groups[2].Value));
        }
        return list;
    }
}
