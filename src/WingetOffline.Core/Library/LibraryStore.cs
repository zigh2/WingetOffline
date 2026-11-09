using System.Text.Json;
using System.Text.Json.Serialization;

namespace WingetOffline.Core.Library;

/// <summary>تقدّم المزامنة (للواجهة وسطر الأوامر).</summary>
public sealed record LibraryProgress(string Phase, int Done, int Total, string? Current);

/// <summary>خيارات مزامنة المكتبة.</summary>
public sealed class LibrarySyncOptions
{
    /// <summary>إعادة قراءة كل الملفات حتى غير المتغيّرة.</summary>
    public bool Rescan { get; init; }
    /// <summary>حساب SHA256 للملفات التي لا بصمة لها (بطيء للملفات الكبيرة، لكنه يتيح المطابقة المباشرة مع winget).</summary>
    public bool ComputeHashes { get; init; }
    public long MaxHashBytes { get; init; } = 4L * 1024 * 1024 * 1024;
    public int Parallelism { get; init; } = 4;
    /// <summary>
    /// true: سجلات الملفات الواقعة خارج المجلدات المعطاة تبقى كما هي (فحص مجلد واحد من سطر الأوامر لا يمحو بقية المكتبة).
    /// false (الافتراضي في الواجهة): المكتبة تساوي مجلداتها تماماً.
    /// </summary>
    public bool KeepEntriesOutsideRoots { get; init; }
}

public sealed class LibrarySyncResult
{
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }
    public int Removed { get; set; }
    public int Hashed { get; set; }
    public int Errors { get; set; }
    public List<string> OfflineRoots { get; } = new();
    public TimeSpan Duration { get; set; }
    public bool Cancelled { get; set; }
    public int Scanned => Added + Updated;
}

/// <summary>
/// المكتبة المحلية: قائمة ملفات المثبّتات التي عُرفت، محفوظة في library.json.
/// المزامنة تزايدية: الملف غير المتغيّر (المسار + الحجم + تاريخ التعديل + نسخة القارئ) لا يُقرأ ثانيةً،
/// فمكتبة بآلاف الملفات لا تُفحص كاملةً إلا في المرة الأولى. المجلد غير المتاح (قرص خارجي مفصول)
/// تبقى سجلاته ولا تُحذف.
/// </summary>
public sealed class LibraryStore
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed class FileModel
    {
        public int Schema { get; set; } = SchemaVersion;
        public DateTime SavedUtc { get; set; }
        public DateTime? LastSyncUtc { get; set; }
        public List<LibraryEntry> Entries { get; set; } = new();
    }

    public string FilePath { get; }
    public IReadOnlyList<LibraryEntry> Entries => _entries;
    public DateTime? LastSyncUtc { get; private set; }

    private List<LibraryEntry> _entries = new();
    private readonly object _saveLock = new();

    public LibraryStore(string filePath) => FilePath = filePath;

    public static string DefaultPath => AppPaths.Combine("library.json");

    public static LibraryStore Load(string? path = null)
    {
        var store = new LibraryStore(path ?? DefaultPath);
        try
        {
            if (File.Exists(store.FilePath))
            {
                var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(store.FilePath), Json);
                if (model != null && model.Schema == SchemaVersion)
                {
                    store._entries = model.Entries.Where(e => !string.IsNullOrEmpty(e.Path)).ToList();
                    store.LastSyncUtc = model.LastSyncUtc;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // ملف تالف: نبدأ بمكتبة فارغة (المزامنة التالية تعيد بناءها)
        }
        return store;
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(FilePath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = FilePath + ".tmp";
        lock (_saveLock)   // حفظان متزامنان (مزامنة + ربط كتالوج) لا يتصادمان على الملف المؤقت
        {
            var model = new FileModel { SavedUtc = DateTime.UtcNow, LastSyncUtc = LastSyncUtc, Entries = _entries };
            File.WriteAllText(tmp, JsonSerializer.Serialize(model, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
    }

    /// <summary>يحذف سجلات الملفات التي ليست تحت أي مجلد من القائمة (بعد إزالة مجلد من المكتبة) ويحفظ. يعيد عددها.</summary>
    public int Prune(IEnumerable<string> roots)
    {
        var full = roots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => Path.GetFullPath(r.Trim())).ToList();
        var kept = _entries.Where(e => full.Any(r => IsUnder(e.Path, r))).ToList();
        int removed = _entries.Count - kept.Count;
        if (removed > 0) { _entries = kept; Save(); }
        return removed;
    }

    public void Clear() { _entries = new List<LibraryEntry>(); LastSyncUtc = null; }

    /// <summary>يستبدل سجلات (مثل ما أضافه ربط الكتالوج) ثم يحفظ.</summary>
    public void ReplaceAll(IEnumerable<LibraryEntry> entries) { _entries = entries.ToList(); }

    // --------------------------------------------------------------- المزامنة

    public LibrarySyncResult Sync(IEnumerable<string> roots, LibrarySyncOptions? options = null,
                                  IProgress<LibraryProgress>? progress = null, CancellationToken ct = default)
    {
        options ??= new LibrarySyncOptions();
        var started = DateTime.UtcNow;
        var result = new LibrarySyncResult();
        var existing = new Dictionary<string, LibraryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _entries) existing[e.Path] = e;

        var rootList = roots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => Path.GetFullPath(r.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var keep = new Dictionary<string, LibraryEntry>(StringComparer.OrdinalIgnoreCase);
        var offline = new List<string>();
        var todo = new List<(FileInfo File, string Root, bool IsUpdate)>();

        try
        {
            // 1) تعداد الملفات وتحديد ما يحتاج قراءة
            foreach (var root in rootList)
            {
                if (ct.IsCancellationRequested) return Cancelled(result, started);
                if (!Directory.Exists(root)) { offline.Add(root); result.OfflineRoots.Add(root); continue; }

                progress?.Report(new LibraryProgress("Listing files", 0, 0, root));
                var options2 = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
                foreach (var path in Directory.EnumerateFiles(root, "*", options2))
                {
                    if (ct.IsCancellationRequested) return Cancelled(result, started);
                    if (!LibraryScanner.IsLibraryFile(path) || keep.ContainsKey(path)) continue;

                    FileInfo info;
                    try { info = new FileInfo(path); if (!info.Exists) continue; }
                    catch (IOException) { continue; }

                    if (!options.Rescan && existing.TryGetValue(path, out var old)
                        && old.Size == info.Length && old.ModifiedUtc == info.LastWriteTimeUtc && old.ScanVersion == LibraryScanner.Version)
                    {
                        old.Root = root;
                        keep[path] = old;
                        result.Unchanged++;
                    }
                    else
                    {
                        // موضع مؤقت يمنع التكرار؛ يُستبدل بالسجل الجديد بعد القراءة
                        keep[path] = existing.TryGetValue(path, out var prev) ? prev : new LibraryEntry { Path = path };
                        todo.Add((info, root, existing.ContainsKey(path)));
                    }
                }
            }

            // 2) قراءة الملفات الجديدة أو المتغيّرة بالتوازي
            int done = 0;
            var fresh = new LibraryEntry?[todo.Count];
            var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Parallelism), CancellationToken = ct };
            try
            {
                Parallel.For(0, todo.Count, po, i =>
                {
                    var (file, root, _) = todo[i];
                    fresh[i] = LibraryScanner.Identify(file, root);
                    int n = Interlocked.Increment(ref done);
                    if (n % 5 == 0 || n == todo.Count) progress?.Report(new LibraryProgress("Reading files", n, todo.Count, file.Name));
                });
            }
            catch (OperationCanceledException) { result.Cancelled = true; }

            for (int i = 0; i < todo.Count; i++)
            {
                var f = fresh[i];
                if (f == null) { keep.Remove(todo[i].File.FullName); continue; }  // لم يُقرأ بسبب الإلغاء: يبقى القديم إن وُجد
                if (existing.TryGetValue(f.Path, out var prev))
                {
                    // نحتفظ ببصمة وربط winget السابقين إن لم يتغيّر الملف
                    if (prev.Size == f.Size && prev.ModifiedUtc == f.ModifiedUtc) { f.Sha256 = prev.Sha256; f.WingetId = prev.WingetId; f.WingetVersion = prev.WingetVersion; }
                    result.Updated++;
                }
                else result.Added++;
                if (f.Error != null) result.Errors++;
                keep[f.Path] = f;
            }
            // 3) البصمات (اختياري)
            if (options.ComputeHashes && !result.Cancelled)
            {
                var needHash = keep.Values.Where(e => e.Sha256 == null && e.Size <= options.MaxHashBytes && e.ScannedUtc != default
                                                      && !offline.Any(o => IsUnder(e.Path, o))).ToList();
                int h = 0;
                foreach (var e in needHash)
                {
                    if (ct.IsCancellationRequested) { result.Cancelled = true; break; }
                    progress?.Report(new LibraryProgress("Computing SHA256", ++h, needHash.Count, e.FileName));
                    try
                    {
                        using var fs = new FileStream(e.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20);
                        e.Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs));
                        result.Hashed++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { e.Error ??= ex.Message; }
                }
            }

            // 4) سجلات المجلدات غير المتاحة تبقى كما هي
            foreach (var e in existing.Values)
                if (!keep.ContainsKey(e.Path)
                    && (offline.Any(o => IsUnder(e.Path, o))
                        || (options.KeepEntriesOutsideRoots && !rootList.Any(r => IsUnder(e.Path, r)))))
                    keep[e.Path] = e;

            if (!result.Cancelled)
                result.Removed = existing.Keys.Count(k => !keep.ContainsKey(k));
            else
                foreach (var e in existing.Values) if (!keep.ContainsKey(e.Path)) keep[e.Path] = e; // الإلغاء لا يحذف شيئاً

            _entries = keep.Values.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
            LastSyncUtc = DateTime.UtcNow;
        }
        finally
        {
            result.Duration = DateTime.UtcNow - started;
        }
        Save();
        return result;
    }

    private static LibrarySyncResult Cancelled(LibrarySyncResult r, DateTime started)
    {
        r.Cancelled = true;
        r.Duration = DateTime.UtcNow - started;
        return r;
    }

    /// <summary>هل المسار تحت المجلد؟ (للفلترة في الواجهة)</summary>
    public static bool IsUnderPublic(string path, string root) => IsUnder(path, Path.GetFullPath(root));

    internal static bool IsUnder(string path, string root)
    {
        var r = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }
}
