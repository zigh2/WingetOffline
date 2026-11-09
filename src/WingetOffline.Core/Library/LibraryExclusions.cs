using System.Text;
using System.Text.RegularExpressions;

namespace WingetOffline.Core.Library;

public enum ExclusionKind
{
    /// <summary>لاحقة ملف: <c>.tmp</c></summary>
    Extension,
    /// <summary>نمط بعلامات * و? على اسم الملف أو المجلد فقط (بلا شرطة مائلة): <c>setup*.log</c></summary>
    Name,
    /// <summary>نمط بعلامات * و? على المسار الكامل: <c>C:\Windows\*</c></summary>
    Path,
    /// <summary>نص عادي: يستثني كل مسار يحويه: <c>node_modules</c></summary>
    Plain,
    /// <summary>تعبير نمطي (regex) يبدأ السطر بـ <c>re:</c> ويُطبَّق على المسار الكامل</summary>
    Regex,
}

/// <summary>قاعدة استثناء واحدة بعد تحليل سطرها.</summary>
public sealed class ExclusionRule
{
    private readonly Regex? _rx;
    private readonly string? _value;

    public int Line { get; }
    public string Text { get; }
    public ExclusionKind Kind { get; }

    internal ExclusionRule(int line, string text, ExclusionKind kind, Regex? rx, string? value)
    {
        Line = line; Text = text; Kind = kind; _rx = rx; _value = value;
    }

    /// <param name="path">المسار الكامل بشرطات عكسية (مُطبَّع)</param>
    internal bool Matches(string path, string name, bool isDirectory)
    {
        try
        {
            switch (Kind)
            {
                case ExclusionKind.Extension:
                    return !isDirectory && name.EndsWith(_value!, StringComparison.OrdinalIgnoreCase);
                case ExclusionKind.Name:
                    return _rx!.IsMatch(name);
                case ExclusionKind.Plain:
                    return path.Contains(_value!, StringComparison.OrdinalIgnoreCase)
                        || (isDirectory && (path + "\\").Contains(_value!, StringComparison.OrdinalIgnoreCase));
                default: // Path وRegex: المجلد يُختبر أيضاً بشرطة في آخره، فيطابق "C:\Windows\*" المجلد نفسه
                    return _rx!.IsMatch(path) || (isDirectory && _rx.IsMatch(path + "\\"));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return false; // تعبير نمطي كارثي: نعتبره لا يطابق بدل تعليق الفحص
        }
    }
}

/// <summary>
/// استثناءات المكتبة المحلية من مربع نص واحد، سطر لكل قاعدة (الأسطر التي تبدأ بـ # تعليقات).
/// أنواع القواعد (يُكتشف النوع من شكل السطر):
/// <list type="bullet">
/// <item><c>re:...</c> تعبير نمطي على المسار الكامل.</item>
/// <item><c>.tmp</c> لاحقة ملف.</item>
/// <item><c>C:\Windows\*</c> نمط على المسار الكامل (أي سطر فيه * أو ? وفيه شرطة مائلة).</item>
/// <item><c>setup*.log</c> نمط على اسم الملف أو المجلد (فيه * أو ? وبلا شرطة مائلة).</item>
/// <item>أي نص آخر: يستثني كل مسار يحتوي هذا النص.</item>
/// </list>
/// المطابقة غير حساسة لحالة الأحرف، ومتغيرات البيئة مثل %WINDIR% تُوسَّع في القواعد غير النمطية.
/// </summary>
public sealed class LibraryExclusions
{
    public const string RegexPrefix = "re:";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(300);
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline;

    /// <summary>القيمة الافتراضية لمربع النص: شرح قصير + المسارات التي لا يُتوقع فيها مثبّتات تخصّك.</summary>
    public static string DefaultText { get; } = string.Join("\r\n", new[]
    {
        @"# One rule per line. Lines that start with # are comments.",
        @"#   C:\Windows\*          path with wildcards (* ?): the folder and everything under it",
        @"#   .tmp                  file extension",
        @"#   setup*.log            wildcards on the file or folder NAME (no backslash in the rule)",
        @"#   node_modules          plain text: any path that contains it",
        @"#   re:\\(temp|cache)\\    regular expression on the full path (start the line with re:)",
        @"C:\Windows\*",
        @"C:\Program Files\*",
        @"C:\Program Files (x86)\*",
        @"C:\$Recycle.Bin\*",
        @"C:\System Volume Information\*",
    });

    public static LibraryExclusions None { get; } = new(Array.Empty<ExclusionRule>(), Array.Empty<string>());

    public IReadOnlyList<ExclusionRule> Rules { get; }
    /// <summary>أخطاء التحليل (تعبير نمطي غير صالح...). القواعد الخاطئة تُتجاهل والبقية تعمل.</summary>
    public IReadOnlyList<string> Errors { get; }
    public bool IsEmpty => Rules.Count == 0;

    private LibraryExclusions(IReadOnlyList<ExclusionRule> rules, IReadOnlyList<string> errors)
    {
        Rules = rules; Errors = errors;
    }

    public static LibraryExclusions Parse(string? text)
    {
        var rules = new List<ExclusionRule>();
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return new LibraryExclusions(rules, errors);

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int n = i + 1;

            if (line.StartsWith(RegexPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var pattern = line[RegexPrefix.Length..].Trim();
                if (pattern.Length == 0) { errors.Add($"Line {n}: empty regular expression"); continue; }
                try { rules.Add(new ExclusionRule(n, line, ExclusionKind.Regex, new Regex(pattern, Options, RegexTimeout), null)); }
                catch (ArgumentException ex) { errors.Add($"Line {n}: invalid regular expression ({ex.Message})"); }
                continue;
            }

            var expanded = line.Contains('%') ? Environment.ExpandEnvironmentVariables(line) : line;
            expanded = expanded.Replace('/', '\\');
            bool wild = expanded.IndexOfAny(new[] { '*', '?' }) >= 0;
            bool sep = expanded.Contains('\\');

            if (!wild && !sep && expanded.Length > 1 && expanded[0] == '.' && expanded.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0)
                rules.Add(new ExclusionRule(n, line, ExclusionKind.Extension, null, expanded));
            else if (wild && !sep)
                rules.Add(new ExclusionRule(n, line, ExclusionKind.Name, WildcardToRegex(expanded), null));
            else if (wild)
                rules.Add(new ExclusionRule(n, line, ExclusionKind.Path, WildcardToRegex(expanded), null));
            else
                rules.Add(new ExclusionRule(n, line, ExclusionKind.Plain, null, expanded));
        }
        return new LibraryExclusions(rules, errors);
    }

    private static Regex WildcardToRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        foreach (var c in pattern)
        {
            if (c == '*') sb.Append(".*");
            else if (c == '?') sb.Append('.');
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append('$');
        return new Regex(sb.ToString(), Options, RegexTimeout);
    }

    /// <summary>أول قاعدة تستثني هذا المجلد (فلا يُنزَل إليه)، أو null.</summary>
    public ExclusionRule? FindDirectory(string fullPath) => Find(fullPath, isDirectory: true);

    /// <summary>أول قاعدة تستثني هذا الملف، أو null.</summary>
    public ExclusionRule? FindFile(string fullPath) => Find(fullPath, isDirectory: false);

    public ExclusionRule? Find(string path, bool isDirectory)
    {
        if (Rules.Count == 0 || string.IsNullOrEmpty(path)) return null;
        var norm = path.Replace('/', '\\');
        var trimmed = norm.TrimEnd('\\');
        var name = trimmed[(trimmed.LastIndexOf('\\') + 1)..];   // لا نستخدم Path.GetFileName: فاصله يختلف بين الأنظمة
        foreach (var r in Rules)
            if (r.Matches(norm, name, isDirectory)) return r;
        return null;
    }
}
