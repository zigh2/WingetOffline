using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WingetOffline.Core.Library;

/// <summary>ما عُرف عن محتوى ملف (لا عن مساره): ما قرأته القارئات وبصماته.</summary>
public sealed class CachedFile
{
    public long Size { get; set; }
    public long ModifiedTicks { get; set; }
    public int ScanVersion { get; set; }
    public LibraryKind Kind { get; set; }
    public FileFacts? Facts { get; set; }
    public List<string>? Sources { get; set; }
    public string? Error { get; set; }
    public string? Sha256 { get; set; }
    public string? Md5 { get; set; }
    public string? XxHash { get; set; }
    public string? PartialHash { get; set; }
    public string? PartialHashAlgorithm { get; set; }
    public long? PartialHashBytes { get; set; }
    public DateTime LastSeenUtc { get; set; }
}

/// <summary>
/// ذاكرة دائمة لما فُحص، مفتاحها <b>الحجم + تاريخ التعديل</b> لا المسار: الملف الذي نُقل أو أُعيدت تسميته
/// (وبقي حجمه وتاريخ تعديله) يُعرَّف وتُستعاد بصماته دون قراءته أو حساب بصمته ثانيةً.
/// تبقى سجلاتها بعد حذف الملف من المكتبة (180 يوماً افتراضياً من آخر مشاهدة) كي يُعرف إن عاد من مكان آخر.
/// الملف الذي يتغيّر محتواه يتغيّر حجمه أو تاريخه فيُعاد فحصه. نسخ الملفات التي لا تحفظ تاريخ التعديل تُفحص من جديد.
/// </summary>
public sealed class LibraryFileCache
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed class FileModel
    {
        public int Schema { get; set; } = SchemaVersion;
        public List<CachedFile> Files { get; set; } = new();
    }

    private readonly ConcurrentDictionary<string, CachedFile> _items = new();
    private readonly object _saveLock = new();
    private volatile bool _dirty;

    public string FilePath { get; }
    public int Count => _items.Count;

    public LibraryFileCache(string filePath) => FilePath = filePath;

    public static string DefaultPath => AppPaths.Combine("library-cache.json");

    private static string Key(long size, long ticks) => size + ":" + ticks;

    public static LibraryFileCache Load(string? path = null)
    {
        var cache = new LibraryFileCache(path ?? DefaultPath);
        try
        {
            if (File.Exists(cache.FilePath))
            {
                var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(cache.FilePath), Json);
                if (model != null && model.Schema == SchemaVersion)
                    foreach (var f in model.Files) cache._items[Key(f.Size, f.ModifiedTicks)] = f;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // ملف تالف: نبدأ فارغين؛ الفحص التالي يعيد بناءه
        }
        return cache;
    }

    public bool TryGet(long size, long modifiedTicks, out CachedFile file) =>
        _items.TryGetValue(Key(size, modifiedTicks), out file!);

    public void Put(CachedFile file)
    {
        file.LastSeenUtc = DateTime.UtcNow;
        _items[Key(file.Size, file.ModifiedTicks)] = file;
        _dirty = true;
    }

    /// <summary>يجدّد تاريخ آخر مشاهدة (ملف ما زال موجوداً ولم يتغيّر).</summary>
    public void Touch(long size, long modifiedTicks)
    {
        if (_items.TryGetValue(Key(size, modifiedTicks), out var f) && (DateTime.UtcNow - f.LastSeenUtc).TotalHours > 12)
        {
            f.LastSeenUtc = DateTime.UtcNow;
            _dirty = true;
        }
    }

    public void MarkDirty() => _dirty = true;

    /// <summary>يحذف ما لم يُشاهد منذ المدة المعطاة. يعيد عدد المحذوف.</summary>
    public int Prune(TimeSpan olderThan)
    {
        var limit = DateTime.UtcNow - olderThan;
        int removed = 0;
        foreach (var kv in _items)
            if (kv.Value.LastSeenUtc < limit && _items.TryRemove(kv.Key, out _)) removed++;
        if (removed > 0) _dirty = true;
        return removed;
    }

    public void Save()
    {
        if (!_dirty) return;
        lock (_saveLock)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(FilePath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new FileModel { Files = _items.Values.ToList() }, Json));
            File.Move(tmp, FilePath, overwrite: true);
            _dirty = false;
        }
    }
}
