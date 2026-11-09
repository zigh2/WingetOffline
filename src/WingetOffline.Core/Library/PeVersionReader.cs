using System.Reflection.PortableExecutable;
using System.Text;

namespace WingetOffline.Core.Library;

/// <summary>
/// يقرأ مورد الإصدار (VS_VERSION_INFO) من ملف PE (exe/dll) بكود مُدار بالكامل: لا يعتمد على ويندوز،
/// ولا يحمّل الملف كله في الذاكرة (يقرأ جداول المورد فقط)، ويكشف نوع المثبّت من التوقيعات.
/// </summary>
public static class PeVersionReader
{
    private const int RtVersion = 16;

    /// <summary>يقرأ من ملف؛ يعيد null إن لم يكن PE صالحاً.</summary>
    public static FileFacts? Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            return Read(fs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    public static FileFacts? Read(Stream stream)
    {
        try
        {
            if (stream.Length < 64) return null;
            stream.Position = 0;
            int m0 = stream.ReadByte(), m1 = stream.ReadByte();
            if (m0 != 'M' || m1 != 'Z') return null;
            stream.Position = 0;

            using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
            var headers = pe.PEHeaders;
            if (headers.PEHeader == null) return null;

            var facts = new FileFacts { Architecture = ArchOf(headers.CoffHeader.Machine) };

            var dir = headers.PEHeader.ResourceTableDirectory;
            if (dir.Size > 0 && dir.RelativeVirtualAddress > 0)
            {
                var blob = FindVersionResource(pe, dir.RelativeVirtualAddress);
                if (blob != null) ParseVersionInfo(blob, facts);
            }

            facts.InstallerType = SniffInstaller(stream, headers);
            return facts;
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or InvalidOperationException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private static string? ArchOf(Machine m) => m switch
    {
        Machine.Amd64 => "x64",
        Machine.I386 => "x86",
        Machine.Arm64 => "arm64",
        Machine.Arm or Machine.ArmThumb2 => "arm",
        _ => null,
    };

    // ------------------------------------------------------------ شجرة الموارد

    private static byte[]? FindVersionResource(PEReader pe, int rootRva)
    {
        // المستوى 1: النوع (RT_VERSION=16) ← المستوى 2: الاسم ← المستوى 3: اللغة ← عنصر بيانات
        var l1 = ReadDir(pe, rootRva, 0);
        if (l1 == null) return null;
        foreach (var e1 in l1)
        {
            if (e1.IsNamed || e1.Id != RtVersion || !e1.IsDirectory) continue;
            var l2 = ReadDir(pe, rootRva, e1.Offset);
            if (l2 == null) continue;
            foreach (var e2 in l2)
            {
                if (!e2.IsDirectory) continue;
                var l3 = ReadDir(pe, rootRva, e2.Offset);
                if (l3 == null || l3.Count == 0) continue;
                // نفضّل الإنكليزية الأمريكية (0x0409) ثم اللغة المحايدة ثم الأولى
                var pick = l3.FirstOrDefault(x => !x.IsNamed && x.Id == 0x0409)
                           ?? l3.FirstOrDefault(x => !x.IsNamed && x.Id == 0)
                           ?? l3[0];
                if (pick.IsDirectory) continue;
                var entryBlock = pe.GetSectionData(rootRva + pick.Offset);
                if (entryBlock.Length < 16) return null;
                var entry = entryBlock.GetContent(0, 16).ToArray();
                int dataRva = BitConverter.ToInt32(entry.AsSpan(0, 4));
                int size = BitConverter.ToInt32(entry.AsSpan(4, 4));
                if (size <= 0 || size > 1 << 20) return null;
                var dataBlock = pe.GetSectionData(dataRva);
                if (dataBlock.Length < size) return null;
                return dataBlock.GetContent(0, size).ToArray();
            }
        }
        return null;
    }

    private sealed record DirEntry(bool IsNamed, int Id, bool IsDirectory, int Offset);

    private static List<DirEntry>? ReadDir(PEReader pe, int rootRva, int offset)
    {
        var block = pe.GetSectionData(rootRva + offset);
        if (block.Length < 16) return null;
        var head = block.GetContent(0, 16).ToArray();
        int named = BitConverter.ToUInt16(head.AsSpan(12, 2));
        int ids = BitConverter.ToUInt16(head.AsSpan(14, 2));
        int count = named + ids;
        if (count == 0 || count > 4096 || block.Length < 16 + count * 8) return null;

        var body = block.GetContent(16, count * 8).ToArray();
        var list = new List<DirEntry>(count);
        for (int i = 0; i < count; i++)
        {
            uint name = BitConverter.ToUInt32(body.AsSpan(i * 8, 4));
            uint off = BitConverter.ToUInt32(body.AsSpan(i * 8 + 4, 4));
            list.Add(new DirEntry((name & 0x80000000) != 0, (int)(name & 0xFFFF), (off & 0x80000000) != 0, (int)(off & 0x7FFFFFFF)));
        }
        return list;
    }

    // --------------------------------------------------------- VS_VERSIONINFO

    private static void ParseVersionInfo(byte[] d, FileFacts facts)
    {
        if (d.Length < 40) return;
        int rootLen = Math.Min(U16(d, 0), d.Length);
        int valueLen = U16(d, 2);
        int pos = Align4(6 + (Utf16Z(d, 6, out _).Length + 1) * 2);
        string fixedVersion = "";
        if (valueLen >= 52 && pos + 52 <= d.Length && BitConverter.ToUInt32(d, pos) == 0xFEEF04BD)
        {
            uint ms = BitConverter.ToUInt32(d, pos + 8), ls = BitConverter.ToUInt32(d, pos + 12);
            fixedVersion = $"{ms >> 16}.{ms & 0xFFFF}.{ls >> 16}.{ls & 0xFFFF}";
        }
        pos = Align4(pos + valueLen);

        var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (pos + 6 <= rootLen)
        {
            int len = U16(d, pos);
            if (len < 6 || pos + len > d.Length) break;
            string key = Utf16Z(d, pos + 6, out int keyEnd);
            if (key == "StringFileInfo")
            {
                int tpos = Align4(keyEnd);
                int end = pos + len;
                bool first = true;
                while (tpos + 6 <= end)
                {
                    int tlen = U16(d, tpos);
                    if (tlen < 6 || tpos + tlen > end) break;
                    string tkey = Utf16Z(d, tpos + 6, out int tkeyEnd);
                    // نقرأ جدول اللغة الأول (عادةً 040904B0) ونكمّل من غيره ما نقص
                    int spos = Align4(tkeyEnd);
                    int tend = tpos + tlen;
                    while (spos + 6 <= tend)
                    {
                        int slen = U16(d, spos);
                        if (slen < 6 || spos + slen > tend) break;
                        int svalLen = U16(d, spos + 2);
                        string skey = Utf16Z(d, spos + 6, out int skeyEnd);
                        int vpos = Align4(skeyEnd);
                        int maxChars = Math.Max(0, Math.Min(svalLen, (spos + slen - vpos) / 2));
                        string val = Encoding.Unicode.GetString(d, vpos, maxChars * 2).TrimEnd('\0').Trim();
                        if (val.Length > 0 && (first || !strings.ContainsKey(skey))) strings[skey] = val;
                        spos = Align4(spos + slen);
                    }
                    first = false;
                    tpos = Align4(tpos + tlen);
                }
            }
            pos = Align4(pos + len);
        }

        string? S(string k) => strings.TryGetValue(k, out var v) && v.Length > 0 ? v : null;
        facts.ProductName = S("ProductName");
        facts.Publisher = S("CompanyName");
        facts.Description = S("FileDescription");
        facts.OriginalFileName = S("OriginalFilename");
        facts.Version = CleanVersion(S("ProductVersion")) ?? CleanVersion(S("FileVersion")) ?? (fixedVersion.Length > 0 && fixedVersion != "0.0.0.0" ? fixedVersion : null);
    }

    /// <summary>"1, 2, 3, 4" ← "1.2.3.4"، و"4.28 (build 7)" تبقى كما هي؛ يرفض ما لا يحوي رقماً.</summary>
    internal static string? CleanVersion(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        v = v.Trim();
        if (System.Text.RegularExpressions.Regex.IsMatch(v, @"^\d+(\s*,\s*\d+)+$")) v = string.Join('.', v.Split(',', StringSplitOptions.TrimEntries));
        return v.Any(char.IsDigit) && v != "0.0.0.0" && v != "0" ? v : null;
    }

    private static int U16(byte[] d, int p) => p + 2 <= d.Length ? BitConverter.ToUInt16(d, p) : 0;
    private static int Align4(int p) => (p + 3) & ~3;

    private static string Utf16Z(byte[] d, int p, out int endPos)
    {
        int i = p;
        while (i + 1 < d.Length && (d[i] != 0 || d[i + 1] != 0)) i += 2;
        endPos = Math.Min(d.Length, i + 2);
        return Encoding.Unicode.GetString(d, p, Math.Max(0, i - p));
    }

    // ---------------------------------------------------------- نوع المثبّت

    private static readonly (string Marker, string Type)[] Markers =
    {
        ("Inno Setup Setup Data", "Inno Setup"),
        ("JR.Inno.Setup", "Inno Setup"),
        ("Nullsoft Install System", "NSIS"),
        ("NullsoftInst", "NSIS"),
        ("InstallShield", "InstallShield"),
        ("Advanced Installer", "Advanced Installer"),
        ("Setup Factory", "Setup Factory"),
        ("WixBurn", "WiX Burn"),
        ("7-Zip SFX", "7-Zip SFX"),
        ("WinRAR SFX", "WinRAR SFX"),
    };

    /// <summary>كشف تقريبي: اسم قسم .wixburn، ثم نصوص مميّزة في أول 4MB (ASCII وUTF-16).</summary>
    private static string? SniffInstaller(Stream s, PEHeaders headers)
    {
        if (headers.SectionHeaders.Any(h => h.Name.Equals(".wixburn", StringComparison.OrdinalIgnoreCase))) return "WiX Burn";

        int len = (int)Math.Min(s.Length, 4 * 1024 * 1024);
        var buf = new byte[len];
        s.Position = 0;
        int read = 0;
        while (read < len)
        {
            int n = s.Read(buf, read, len - read);
            if (n <= 0) break;
            read += n;
        }
        var span = new ReadOnlySpan<byte>(buf, 0, read);
        foreach (var (marker, type) in Markers)
        {
            if (span.IndexOf(Encoding.ASCII.GetBytes(marker)) >= 0) return type;
            if (span.IndexOf(Encoding.Unicode.GetBytes(marker)) >= 0) return type;
        }
        return null;
    }
}
