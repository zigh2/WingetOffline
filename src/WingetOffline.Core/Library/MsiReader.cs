using System.Text;

namespace WingetOffline.Core.Library;

/// <summary>
/// يقرأ جدول Property ومعلومات الملخص من ملف MSI بكود مُدار (بلا msi.dll ولا COM).
/// MSI حاوية OLE مركّبة (Compound File)؛ نقرأ منها ثلاث تدفقات صغيرة فقط ولا نفتح CAB المضمّن.
/// </summary>
public static class MsiReader
{
    private const string NameAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz._";

    public static FileFacts? Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            return Read(fs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static FileFacts? Read(Stream stream)
    {
        try
        {
            var cfb = CompoundFile.TryOpen(stream);
            if (cfb == null) return null;

            var props = ReadPropertyTable(cfb);
            if (props == null || props.Count == 0) return null;

            props.TryGetValue("ProductName", out var name);
            props.TryGetValue("ProductVersion", out var ver);
            props.TryGetValue("Manufacturer", out var mfr);
            props.TryGetValue("ProductCode", out var code);
            props.TryGetValue("UpgradeCode", out var up);

            return new FileFacts
            {
                ProductName = Blank(name),
                Version = Blank(ver),
                Publisher = Blank(mfr),
                ProductCode = Blank(code),
                UpgradeCode = Blank(up),
                Architecture = ReadPlatform(cfb),
                InstallerType = "MSI",
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ------------------------------------------------------- جدول Property

    private static Dictionary<string, string>? ReadPropertyTable(CompoundFile cfb)
    {
        byte[]? pool = null, data = null, table = null;
        foreach (var name in cfb.StreamNames)
        {
            // الجداول النظامية (_StringPool و_StringData) تحمل بادئة الجدول أيضاً، فنقارن الاسم المفكوك وحده
            var decoded = DecodeName(name, out _);
            if (decoded == "_StringPool") pool = cfb.ReadStream(name);
            else if (decoded == "_StringData") data = cfb.ReadStream(name);
            else if (decoded == "Property") table = cfb.ReadStream(name);
        }
        if (pool == null || data == null || table == null || pool.Length < 4) return null;

        uint info = BitConverter.ToUInt32(pool, 0);
        int codepage = (int)(info & 0x7FFFFFFF);
        bool longRefs = (info & 0x80000000) != 0;
        int refSize = longRefs ? 3 : 2;
        var enc = codepage == 65001 ? Encoding.UTF8 : Encoding.Latin1;

        // مجمّع النصوص: كل مدخل (طول u16، عدد المراجع u16)؛ الطول 0 مع مراجع>0 يعني نصاً طويلاً
        var strings = new List<string> { "" };
        int dpos = 0;
        for (int p = 4; p + 4 <= pool.Length;)
        {
            int len = BitConverter.ToUInt16(pool, p);
            int refs = BitConverter.ToUInt16(pool, p + 2);
            p += 4;
            if (len == 0 && refs != 0)
            {
                if (p + 4 > pool.Length) break;
                len = (refs << 16) | BitConverter.ToUInt16(pool, p);
                p += 4;
            }
            if (dpos + len > data.Length) len = Math.Max(0, data.Length - dpos);
            strings.Add(len == 0 ? "" : enc.GetString(data, dpos, len));
            dpos += len;
        }

        string Str(int index) => index > 0 && index < strings.Count ? strings[index] : "";
        int Ref(int offset)
        {
            if (offset + refSize > table.Length) return 0;
            return refSize == 2 ? BitConverter.ToUInt16(table, offset)
                                : table[offset] | (table[offset + 1] << 8) | (table[offset + 2] << 16);
        }

        int rows = table.Length / (2 * refSize);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < rows; i++)
        {
            string key = Str(Ref(i * refSize));
            string val = Str(Ref((rows + i) * refSize));
            if (key.Length > 0) result[key] = val;
        }
        return result;
    }

    /// <summary>أسماء التدفقات في MSI مشفّرة بحروف في النطاق U+3800–U+4840؛ بادئة U+4840 تعني جدولاً.</summary>
    internal static string DecodeName(string raw, out bool isTable)
    {
        isTable = false;
        var sb = new StringBuilder();
        for (int i = 0; i < raw.Length; i++)
        {
            int v = raw[i];
            if (i == 0 && v == 0x4840) { isTable = true; continue; }
            if (v >= 0x3800 && v < 0x4800)
            {
                v -= 0x3800;
                sb.Append(NameAlphabet[v & 0x3F]);
                sb.Append(NameAlphabet[(v >> 6) & 0x3F]);
            }
            else if (v >= 0x4800 && v < 0x4840) sb.Append(NameAlphabet[v - 0x4800]);
            else sb.Append(raw[i]);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------ SummaryInformation

    /// <summary>خاصية Template في ملخص MSI (مثل "x64;1033" أو "Intel;1033") تحدد المعمارية.</summary>
    private static string? ReadPlatform(CompoundFile cfb)
    {
        var bytes = cfb.StreamNames.Contains("\u0005SummaryInformation") ? cfb.ReadStream("\u0005SummaryInformation") : null;
        if (bytes == null || bytes.Length < 48) return null;
        try
        {
            int sectionOffset = BitConverter.ToInt32(bytes, 44);
            int count = BitConverter.ToInt32(bytes, sectionOffset + 4);
            for (int i = 0; i < count && i < 64; i++)
            {
                int id = BitConverter.ToInt32(bytes, sectionOffset + 8 + i * 8);
                int off = BitConverter.ToInt32(bytes, sectionOffset + 12 + i * 8);
                if (id != 7) continue; // PIDSI_TEMPLATE
                int type = BitConverter.ToInt32(bytes, sectionOffset + off);
                if (type != 30) return null; // VT_LPSTR
                int len = BitConverter.ToInt32(bytes, sectionOffset + off + 4);
                var text = Encoding.Latin1.GetString(bytes, sectionOffset + off + 8, Math.Max(0, len)).TrimEnd('\0');
                var platform = text.Split(';')[0].Trim();
                if (platform.Length == 0) return "neutral";
                if (platform.Contains("Arm64", StringComparison.OrdinalIgnoreCase)) return "arm64";
                if (platform.Contains("x64", StringComparison.OrdinalIgnoreCase)
                    || platform.Contains("Intel64", StringComparison.OrdinalIgnoreCase)
                    || platform.Contains("AMD64", StringComparison.OrdinalIgnoreCase)) return "x64";
                if (platform.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return "x86";
                return null;
            }
        }
        catch (ArgumentException) { }
        return null;
    }
}

/// <summary>قارئ مبسّط لصيغة Compound File Binary (OLE2): يعدّد التدفقات ويقرأ أي تدفق منها.</summary>
internal sealed class CompoundFile
{
    private static readonly byte[] Signature = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
    private const uint EndOfChain = 0xFFFFFFFE, FreeSect = 0xFFFFFFFF;

    private readonly Stream _s;
    private readonly int _sectorSize, _miniSectorSize, _miniCutoff;
    private readonly uint[] _fat;
    private uint[]? _miniFat;
    private byte[]? _miniStream;
    private readonly Dictionary<string, (uint Start, long Size)> _streams = new(StringComparer.Ordinal);
    private (uint Start, long Size) _root;

    public IReadOnlyCollection<string> StreamNames => _streams.Keys;

    private CompoundFile(Stream s, int sectorShift, int miniShift, int miniCutoff, uint[] fat)
    {
        _s = s;
        _sectorSize = 1 << sectorShift;
        _miniSectorSize = 1 << miniShift;
        _miniCutoff = miniCutoff;
        _fat = fat;
    }

    public static CompoundFile? TryOpen(Stream s)
    {
        if (s.Length < 512) return null;
        var h = new byte[512];
        s.Position = 0;
        if (s.Read(h, 0, 512) != 512) return null;
        for (int i = 0; i < 8; i++) if (h[i] != Signature[i]) return null;

        int sectorShift = BitConverter.ToUInt16(h, 0x1E);
        int miniShift = BitConverter.ToUInt16(h, 0x20);
        if (sectorShift is not (9 or 12) || miniShift != 6) return null;
        int sectorSize = 1 << sectorShift;
        uint firstDir = BitConverter.ToUInt32(h, 0x30);
        int miniCutoff = BitConverter.ToInt32(h, 0x38);
        uint firstMiniFat = BitConverter.ToUInt32(h, 0x3C);
        uint miniFatCount = BitConverter.ToUInt32(h, 0x40);
        uint firstDifat = BitConverter.ToUInt32(h, 0x44);
        uint difatCount = BitConverter.ToUInt32(h, 0x48);

        long SectorOffset(uint sector) => (sector + 1L) * sectorSize;
        byte[] ReadSector(uint sector)
        {
            var b = new byte[sectorSize];
            s.Position = SectorOffset(sector);
            int n = 0;
            while (n < sectorSize) { int r = s.Read(b, n, sectorSize - n); if (r <= 0) throw new InvalidDataException("Truncated compound file"); n += r; }
            return b;
        }

        // قائمة قطاعات FAT: 109 في الترويسة + سلسلة DIFAT
        var fatSectors = new List<uint>();
        for (int i = 0; i < 109; i++)
        {
            uint v = BitConverter.ToUInt32(h, 0x4C + i * 4);
            if (v != FreeSect) fatSectors.Add(v);
        }
        uint d = firstDifat;
        for (uint i = 0; i < difatCount && d != EndOfChain && d != FreeSect && i < 4096; i++)
        {
            var sec = ReadSector(d);
            for (int k = 0; k < sectorSize / 4 - 1; k++)
            {
                uint v = BitConverter.ToUInt32(sec, k * 4);
                if (v != FreeSect) fatSectors.Add(v);
            }
            d = BitConverter.ToUInt32(sec, sectorSize - 4);
        }

        var fat = new uint[fatSectors.Count * (sectorSize / 4)];
        for (int i = 0; i < fatSectors.Count; i++)
        {
            var sec = ReadSector(fatSectors[i]);
            Buffer.BlockCopy(sec, 0, fat, i * sectorSize, sectorSize);
        }

        var cf = new CompoundFile(s, sectorShift, miniShift, miniCutoff, fat);
        cf.LoadDirectory(firstDir);
        if (miniFatCount > 0 && firstMiniFat != EndOfChain)
        {
            var mf = cf.ReadChain(firstMiniFat, long.MaxValue);
            cf._miniFat = new uint[mf.Length / 4];
            Buffer.BlockCopy(mf, 0, cf._miniFat, 0, cf._miniFat.Length * 4);
        }
        return cf;
    }

    private void LoadDirectory(uint firstDir)
    {
        var dir = ReadChain(firstDir, 64L * 1024 * 1024);
        for (int off = 0; off + 128 <= dir.Length; off += 128)
        {
            int type = dir[off + 66];
            if (type == 0) continue;
            int nameLen = BitConverter.ToUInt16(dir, off + 64);
            if (nameLen < 2 || nameLen > 64) continue;
            string name = Encoding.Unicode.GetString(dir, off, nameLen - 2);
            uint start = BitConverter.ToUInt32(dir, off + 116);
            long size = BitConverter.ToInt64(dir, off + 120) & 0xFFFFFFFFL; // الإصدار 3 يستخدم 32 بتاً
            if (type == 5) _root = (start, size);
            else if (type == 2 && !_streams.ContainsKey(name)) _streams[name] = (start, size);
        }
    }

    private byte[] ReadChain(uint start, long maxBytes)
    {
        using var ms = new MemoryStream();
        var buf = new byte[_sectorSize];
        uint sector = start;
        int guard = 0;
        while (sector != EndOfChain && sector != FreeSect && sector < _fat.Length && guard++ < 10_000_000)
        {
            _s.Position = (sector + 1L) * _sectorSize;
            int n = 0;
            while (n < _sectorSize) { int r = _s.Read(buf, n, _sectorSize - n); if (r <= 0) break; n += r; }
            if (n == 0) break;
            ms.Write(buf, 0, n);
            if (ms.Length >= maxBytes) break;
            sector = _fat[sector];
        }
        return ms.ToArray();
    }

    public byte[]? ReadStream(string name)
    {
        if (!_streams.TryGetValue(name, out var e)) return null;
        if (e.Size > 256L * 1024 * 1024) return null; // لا نحمّل تدفقات ضخمة (CAB مضمّن)
        if (e.Size < _miniCutoff)
        {
            if (_miniFat == null) return null;
            _miniStream ??= ReadChain(_root.Start, _root.Size);
            var outBuf = new byte[e.Size];
            int written = 0;
            uint ms = e.Start;
            int guard = 0;
            while (written < e.Size && ms != EndOfChain && ms < _miniFat.Length && guard++ < 1_000_000)
            {
                int off = (int)(ms * (long)_miniSectorSize);
                int take = (int)Math.Min(_miniSectorSize, e.Size - written);
                if (off + take > _miniStream.Length) break;
                Buffer.BlockCopy(_miniStream, off, outBuf, written, take);
                written += take;
                ms = _miniFat[ms];
            }
            return outBuf;
        }
        var all = ReadChain(e.Start, e.Size);
        return all.Length > e.Size ? all.AsSpan(0, (int)e.Size).ToArray() : all;
    }
}
