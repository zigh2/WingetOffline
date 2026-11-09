using System.Text.RegularExpressions;

namespace WingetOffline.Core.Library;

/// <summary>ما يمكن استنتاجه من اسم الملف وحده: "VLC-3.0.21-win64.exe" ← VLC / 3.0.21 / x64.</summary>
public sealed record ParsedName(string? Name, string? Version, string? Architecture);

public static class FileNameParser
{
    private static readonly Regex DottedVersion = new(@"(?<![A-Za-z0-9])[vV]?(\d+(?:\.\d+){1,3})(?![\d])", RegexOptions.Compiled);
    private static readonly Regex Separators = new(@"[\s_\-.]+", RegexOptions.Compiled);

    private static readonly Regex ArchX64 = new(@"(?<![a-z0-9])(x64|x86[-_]?64|amd64|win64|64[-_ ]?bit)(?![a-z0-9])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ArchX86 = new(@"(?<![a-z0-9])(x86|win32|i[3-6]86|32[-_ ]?bit)(?![a-z0-9])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ArchArm = new(@"(?<![a-z0-9])(arm64|aarch64)(?![a-z0-9])", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // كلمات لا تدخل في اسم المنتج إذا جاءت بعده
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "setup", "installer", "install", "offline", "online", "full", "web", "stable", "release", "final", "portable",
        "win", "windows", "x64", "x86", "amd64", "arm64", "win64", "win32", "64bit", "32bit", "64", "32", "bit",
        "multi", "multilingual", "en", "enus", "us", "ar", "msi", "exe", "latest", "pro", "x",
    };

    private static readonly string[] SuffixWords = { "Setup", "Installer", "Install" };

    public static ParsedName Parse(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(stem)) return new ParsedName(null, null, null);

        // الملفات المزدوجة الامتداد مثل .tar.gz: نقتطع الامتداد الثاني إن كان معروفاً
        if (stem.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];

        string? arch = ArchArm.IsMatch(stem) ? "arm64" : ArchX64.IsMatch(stem) ? "x64" : ArchX86.IsMatch(stem) ? "x86" : null;

        string? version = null;
        string head = stem;
        var m = DottedVersion.Match(stem);
        if (m.Success)
        {
            version = m.Groups[1].Value;
            head = stem[..m.Index];
        }

        var tokens = Separators.Split(head).Where(t => t.Length > 0).ToList();
        // نقطع عند أول كلمة ضجيج بعد بداية الاسم
        int cut = tokens.Count;
        for (int i = 1; i < tokens.Count; i++)
            if (Noise.Contains(tokens[i])) { cut = i; break; }
        tokens = tokens.Take(cut).ToList();

        // "VSCodeSetup" ← "VSCode"؛ "Setup" وحدها لا اسم لها
        if (tokens.Count > 0)
        {
            var last = tokens[^1];
            foreach (var suffix in SuffixWords)
                if (last.Length > suffix.Length + 1 && last.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                { tokens[^1] = last[..^suffix.Length]; break; }
        }
        if (tokens.Count == 1 && Noise.Contains(tokens[0])) tokens.Clear();   // "setup.exe" لا اسم لها

        var name = tokens.Count == 0 ? null : string.Join(' ', tokens);
        if (name != null && name.Length < 2) name = null;
        return new ParsedName(name, version, arch);
    }
}
