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
    /// <summary>اختصار: true يعني بصمة SHA256 كاملة لكل ملف (يُهمَل إن حُدّدت <see cref="Hashing"/>).</summary>
    public bool ComputeHashes { get; init; }
    /// <summary>سياسة البصمات: كاملة أو سريعة (أول جزء من الملف الكبير فقط) وSHA256 أو MD5.</summary>
    public LibraryHashPolicy? Hashing { get; init; }
    /// <summary>أكبر ملف يُبصَم كاملاً (الوضع Full). الأكبر يُترك دون بصمة.</summary>
    public long MaxHashBytes { get; init; } = 4L * 1024 * 1024 * 1024;
    /// <summary>
    /// ذاكرة ما فُحص بالحجم + تاريخ التعديل: الملف المنقول أو المُعاد تسميته لا يُقرأ ولا يُبصَم ثانيةً.
    /// null: بلا ذاكرة.
    /// </summary>
    public LibraryFileCache? Cache { get; init; }
    public int Parallelism { get; init; } = 4;
    /// <summary>
    /// true: سجلات الملفات الواقعة خارج المجلدات المعطاة تبقى كما هي (فحص مجلد واحد من سطر الأوامر لا يمحو بقية المكتبة).
    /// false (الافتراضي في الواجهة): المكتبة تساوي مجلداتها تماماً.
    /// </summary>
    public bool KeepEntriesOutsideRoots { get; init; }
    /// <summary>
    /// true: سجلات الملفات على أقراص لم تعد موجودة (قرص خارجي مفصول اختفى حرفه) تبقى كما هي.
    /// يلزم عند البحث في كامل الكمبيوتر لأن الأقراص المفصولة لا تظهر أصلاً في قائمة الأقراص.
    /// </summary>
    public bool KeepEntriesOnMissingDrives { get; init; }
    /// <summary>استثناءات المسارات واللواحق والنصوص. المجلد المستثنى لا يُنزَل إليه أصلاً (مهم لسرعة فحص قرص كامل).</summary>
    public LibraryExclusions? Exclusions { get; init; }
}

public sealed class LibrarySyncResult
{
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }
    public int Removed { get; set; }
    public int Hashed { get; set; }
    /// <summary>ملفات استُعيدت بصمتها من الذاكرة (نُقلت أو أُعيدت تسميتها) دون حساب.</summary>
    public int HashReused { get; set; }
    /// <summary>ملفات عُرّفت من الذاكرة دون قراءة محتواها (نُقلت أو أُعيدت تسميتها).</summary>
    public int FromCache { get; set; }
    public int Errors { get; set; }
    /// <summary>مجلدات لم يُنزَل إليها بسبب قاعدة استثناء.</summary>
    public int ExcludedDirectories { get; set; }
    /// <summary>ملفات مكتبة (بامتداد معروف) استُثنيت بقاعدة.</summary>
    public int ExcludedFiles { get; set; }
    /// <summary>ملفات سحابية غير محمَّلة (OneDrive...) تُركت كي لا يؤدي فتحها إلى تنزيلها.</summary>
    public int SkippedOnlineOnly { get; set; }
    /// <summary>عدد العناصر (ملفات ومجلدات) التي مرّ بها التعداد.</summary>
    public long Visited { get; set; }
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

                var found = new List<(FileInfo Info, bool OnlineOnly)>();
                Walk(root, options.Exclusions, found, result, progress, ct);
                if (ct.IsCancellationRequested) return Cancelled(result, started);

                foreach (var (info, onlineOnly) in found)
                {
                    var path = info.FullName;
                    if (keep.ContainsKey(path)) continue;

                    if (onlineOnly)
                    {
                        // ملف سحابي غير محمَّل: لا نفتحه (الفتح ينزّله). نُبقي سجله القديم إن وُجد.
                        result.SkippedOnlineOnly++;
                        if (existing.TryGetValue(path, out var cached)) { cached.Root = root; keep[path] = cached; }
                        continue;
                    }

                    if (!options.Rescan && existing.TryGetValue(path, out var old)
                        && old.Size == info.Length && old.ModifiedUtc == info.LastWriteTimeUtc && old.ScanVersion == LibraryScanner.Version)
                    {
                        old.Root = root;
                        keep[path] = old;
                        result.Unchanged++;
                        options.Cache?.Touch(old.Size, old.ModifiedUtc.Ticks);
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
            var counters = new RecallCounters();
            var fresh = new LibraryEntry?[todo.Count];
            var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Parallelism), CancellationToken = ct };
            try
            {
                Parallel.For(0, todo.Count, po, i =>
                {
                    var (file, root, _) = todo[i];
                    fresh[i] = ReadOrRecall(file, root, options, counters);
                    int n = Interlocked.Increment(ref done);
                    if (n % 5 == 0 || n == todo.Count) progress?.Report(new LibraryProgress("Reading files", n, todo.Count, file.Name));
                });
            }
            catch (OperationCanceledException) { result.Cancelled = true; }
            result.FromCache = counters.FromCache;
            result.HashReused += counters.HashReused;

            for (int i = 0; i < todo.Count; i++)
            {
                var f = fresh[i];
                if (f == null) { keep.Remove(todo[i].File.FullName); continue; }  // لم يُقرأ بسبب الإلغاء: يبقى القديم إن وُجد
                if (existing.TryGetValue(f.Path, out var prev))
                {
                    // نحتفظ ببصمة وربط winget السابقين إن لم يتغيّر الملف
                    if (prev.Size == f.Size && prev.ModifiedUtc == f.ModifiedUtc)
                    {
                        f.Sha256 ??= prev.Sha256; f.Md5 ??= prev.Md5;
                        if (f.PartialHash == null) { f.PartialHash = prev.PartialHash; f.PartialHashAlgorithm = prev.PartialHashAlgorithm; f.PartialHashBytes = prev.PartialHashBytes; }
                        f.WingetId = prev.WingetId; f.WingetVersion = prev.WingetVersion;
                    }
                    result.Updated++;
                }
                else result.Added++;
                if (f.Error != null) result.Errors++;
                keep[f.Path] = f;
            }
            // 3) البصمات (اختياري): من الذاكرة أولاً، ثم الحساب
            var policy = options.Hashing ?? (options.ComputeHashes ? LibraryHashPolicy.FullSha256 : LibraryHashPolicy.Off);
            if (policy.Mode != LibraryHashMode.Off && !result.Cancelled)
            {
                var needHash = keep.Values.Where(e => e.ScannedUtc != default && !offline.Any(o => IsUnder(e.Path, o))
                                                      && policy.Needs(e, options.MaxHashBytes)).ToList();
                int h = 0;
                foreach (var e in needHash)
                {
                    if (ct.IsCancellationRequested) { result.Cancelled = true; break; }
                    h++;

                    CachedFile? cached = null;
                    options.Cache?.TryGet(e.Size, e.ModifiedUtc.Ticks, out cached);
                    if (cached != null)
                    {
                        CopyHashes(cached, e);
                        if (!policy.Needs(e, options.MaxHashBytes)) { result.HashReused++; continue; }
                    }

                    bool partial = policy.WantsPartial(e.Size);
                    progress?.Report(new LibraryProgress(partial
                        ? $"Hashing {policy.AlgorithmName} (first {policy.QuickHeadBytes / (1024 * 1024)} MB)"
                        : $"Hashing {policy.AlgorithmName}", h, needHash.Count, e.FileName));
                    try
                    {
                        var hex = LibraryHasher.Compute(e.Path, policy.Algorithm, partial ? policy.QuickHeadBytes : null, ct);
                        if (partial) { e.PartialHash = hex; e.PartialHashAlgorithm = policy.AlgorithmName; e.PartialHashBytes = policy.QuickHeadBytes; }
                        else if (policy.Algorithm == LibraryHashAlgorithm.Md5) e.Md5 = hex;
                        else e.Sha256 = hex;
                        result.Hashed++;
                        if (cached != null) { CopyHashes(e, cached); options.Cache!.MarkDirty(); }
                    }
                    catch (OperationCanceledException) { result.Cancelled = true; break; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { e.Error ??= ex.Message; }
                }
            }

            // 4) سجلات المجلدات غير المتاحة تبقى كما هي
            foreach (var e in existing.Values)
                if (!keep.ContainsKey(e.Path)
                    && (offline.Any(o => IsUnder(e.Path, o))
                        || (options.KeepEntriesOutsideRoots && !rootList.Any(r => IsUnder(e.Path, r)))
                        || (options.KeepEntriesOnMissingDrives && DriveIsMissing(e.Path))))
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
        if (options.Cache != null)
        {
            options.Cache.Prune(TimeSpan.FromDays(180));
            try { options.Cache.Save(); } catch (IOException) { /* الذاكرة اختيارية */ }
        }
        return result;
    }

    /// <summary>يقرأ الملف، أو يعرّفه من الذاكرة إن سبق فحص محتوى بالحجم وتاريخ التعديل نفسيهما (منقولاً أو مُعاد التسمية).</summary>
    private static LibraryEntry ReadOrRecall(FileInfo file, string root, LibrarySyncOptions options, RecallCounters counters)
    {
        var cache = options.Cache;
        var kind = LibraryScanner.KindOf(file.FullName);
        CachedFile? hit = null;
        bool known = cache != null && cache.TryGet(file.Length, file.LastWriteTimeUtc.Ticks, out hit);

        if (known && !options.Rescan && hit!.ScanVersion == LibraryScanner.Version && hit.Kind == kind)
        {
            var e = LibraryScanner.BuildEntry(file, root, hit.Facts, hit.Sources ?? new List<string>(), hit.Error);
            CopyHashes(hit, e);
            Interlocked.Increment(ref counters.FromCache);
            if (e.Sha256 != null || e.Md5 != null || e.PartialHash != null) Interlocked.Increment(ref counters.HashReused);
            return e;
        }

        var sources = new List<string>();
        var facts = LibraryScanner.ReadFacts(file, kind, sources, out var error);
        var entry = LibraryScanner.BuildEntry(file, root, facts, sources, error);
        if (cache != null)
        {
            var record = new CachedFile
            {
                Size = file.Length, ModifiedTicks = file.LastWriteTimeUtc.Ticks, ScanVersion = LibraryScanner.Version, Kind = kind,
                Facts = facts, Sources = sources, Error = error,
            };
            if (known) CopyHashes(hit!, record);   // إعادة قراءة قسرية لا تُفقد البصمات المحسوبة
            cache.Put(record);
            if (known) CopyHashes(record, entry);
        }
        return entry;
    }

    private sealed class RecallCounters
    {
        public int FromCache;
        public int HashReused;
    }

    private static void CopyHashes(CachedFile from, LibraryEntry to)
    {
        to.Sha256 ??= from.Sha256;
        to.Md5 ??= from.Md5;
        if (to.PartialHash == null && from.PartialHash != null)
        { to.PartialHash = from.PartialHash; to.PartialHashAlgorithm = from.PartialHashAlgorithm; to.PartialHashBytes = from.PartialHashBytes; }
    }

    private static void CopyHashes(LibraryEntry from, CachedFile to)
    {
        to.Sha256 = from.Sha256 ?? to.Sha256;
        to.Md5 = from.Md5 ?? to.Md5;
        if (from.PartialHash != null) { to.PartialHash = from.PartialHash; to.PartialHashAlgorithm = from.PartialHashAlgorithm; to.PartialHashBytes = from.PartialHashBytes; }
    }

    private static void CopyHashes(CachedFile from, CachedFile to)
    {
        to.Sha256 ??= from.Sha256; to.Md5 ??= from.Md5;
        if (to.PartialHash == null) { to.PartialHash = from.PartialHash; to.PartialHashAlgorithm = from.PartialHashAlgorithm; to.PartialHashBytes = from.PartialHashBytes; }
    }

    /// <summary>
    /// يعدّد الملفات بنزول يدوي إلى المجلدات كي نقطع المجلدات المستثناة قبل دخولها، ولا نتبع
    /// الوصلات (junctions/symlinks) فلا ندور في حلقات. يجمع فقط الملفات ذات امتداد المكتبة.
    /// </summary>
    private static void Walk(string root, LibraryExclusions? ex, List<(FileInfo, bool)> found, LibrarySyncResult result,
                             IProgress<LibraryProgress>? progress, CancellationToken ct)
    {
        const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
        const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

        var eo = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = FileAttributes.System };
        var stack = new Stack<string>();
        stack.Push(root);
        long lastReport = 0;

        while (stack.Count > 0)
        {
            if (ct.IsCancellationRequested) return;
            var dir = stack.Pop();
            try
            {
                foreach (var item in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", eo))
                {
                    result.Visited++;
                    if (progress != null && result.Visited - lastReport >= 5000)
                    {
                        lastReport = result.Visited;
                        progress.Report(new LibraryProgress($"Listing files ({result.Visited:N0} items, {found.Count:N0} installers found)", 0, 0, dir));
                        if (ct.IsCancellationRequested) return;
                    }

                    if (item is DirectoryInfo d)
                    {
                        if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if (ex != null && ex.FindDirectory(d.FullName) != null) { result.ExcludedDirectories++; continue; }
                        stack.Push(d.FullName);
                    }
                    else if (item is FileInfo f)
                    {
                        if (!LibraryScanner.IsLibraryFile(f.Name)) continue;
                        if (ex != null && ex.FindFile(f.FullName) != null) { result.ExcludedFiles++; continue; }
                        bool online = (f.Attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0;
                        found.Add((f, online));
                    }
                }
            }
            catch (Exception ex2) when (ex2 is IOException or UnauthorizedAccessException)
            {
                // مجلد اختفى أو مُنع الدخول إليه أثناء التعداد: نكمل بالباقي
            }
        }
    }

    private static bool DriveIsMissing(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && !Directory.Exists(root);
        }
        catch (ArgumentException) { return false; }
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
