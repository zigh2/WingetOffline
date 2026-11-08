namespace WingetOffline.Core.Versioning;

/// <summary>
/// مقارِن إصدارات: الإصدار سلسلة رموز (أرقام وكلمات) تفصل بينها . - _ ومسافات؛ وما بعد أول + هو "رقم البناء"
/// ويُقارَن فقط عند تساوي النواة (كما في SemVer). القواعد:
/// • الغياب يساوي 0 (1.0 = 1.0.0) • الكلمة (beta, rc, a…) وسم ما قبل الإصدار فهي أقدم من الغياب ومن أي رقم
/// • كلمتان تُقارَنان نصياً دون حساسية لحالة الأحرف • بادئة حرف واحد قبل رقم تُتجاهل (v1.2 و M138.0)
/// • الرقم ملتصقاً بالكلمة يُفصل ("draft42" = draft ثم 42).
/// </summary>
public sealed class WingetVersionComparer : IComparer<string>
{
    public static readonly WingetVersionComparer Instance = new();

    public int Compare(string? a, string? b)
    {
        var (coreA, buildA) = Parse(a);
        var (coreB, buildB) = Parse(b);
        int c = CompareTokens(coreA, coreB);
        return c != 0 ? c : CompareTokens(buildA, buildB);
    }

    private readonly record struct Token(bool IsNumber, decimal Number, string Word);

    private static (List<Token> Core, List<Token> Build) Parse(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return (new List<Token>(), new List<Token>());
        v = v.Trim();
        if (v.Length > 1 && char.IsLetter(v[0]) && IsDigit(v[1])) v = v[1..];   // v1.2 / M138.0.7204.300

        int plus = v.IndexOf('+');
        var core = plus >= 0 ? v[..plus] : v;
        var build = plus >= 0 ? v[(plus + 1)..] : "";
        return (Tokenize(core), Tokenize(build));
    }

    private static bool IsDigit(char c) => c >= '0' && c <= '9';

    private static List<Token> Tokenize(string s)
    {
        var list = new List<Token>();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (IsDigit(c))
            {
                int j = i;
                while (j < s.Length && IsDigit(s[j])) j++;
                var digits = s[i..j];
                decimal n = digits.Length > 28 ? decimal.MaxValue
                          : decimal.TryParse(digits, out var parsed) ? parsed : 0;
                list.Add(new Token(true, n, ""));
                i = j;
            }
            else if (char.IsLetter(c))
            {
                int j = i;
                while (j < s.Length && char.IsLetter(s[j])) j++;
                list.Add(new Token(false, 0, s[i..j]));
                i = j;
            }
            else i++;   // فواصل ورموز أخرى
        }
        return list;
    }

    private static int CompareTokens(List<Token> a, List<Token> b)
    {
        int n = Math.Max(a.Count, b.Count);
        for (int i = 0; i < n; i++)
        {
            if (i >= a.Count)   // a انتهى: يساوي 0 أمام الرقم، وهو أحدث من الكلمة (وسم ما قبل الإصدار)
            {
                var t = b[i];
                if (t.IsNumber) { if (t.Number != 0) return -1; continue; }
                return 1;
            }
            if (i >= b.Count)
            {
                var t = a[i];
                if (t.IsNumber) { if (t.Number != 0) return 1; continue; }
                return -1;
            }

            var x = a[i]; var y = b[i];
            if (x.IsNumber && y.IsNumber)
            {
                int c = x.Number.CompareTo(y.Number);
                if (c != 0) return c;
            }
            else if (!x.IsNumber && !y.IsNumber)
            {
                int c = string.Compare(x.Word, y.Word, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
            }
            else return x.IsNumber ? 1 : -1;   // رقم أحدث من كلمة
        }
        return 0;
    }
}
