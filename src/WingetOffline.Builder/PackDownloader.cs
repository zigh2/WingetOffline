using System.Security.Cryptography;
using WingetOffline.Core.Packing;

/// <summary>نتيجة تنزيل مثبّت واحد.</summary>
internal sealed record DownloadResult(PackItem Item, string? FilePath, bool Success, string Message, bool Cached)
{
    public string? FileName => FilePath == null ? null : Path.GetFileName(FilePath);
}

/// <summary>
/// تنزيل مثبّتات مع التحقق من SHA256 الموجود في الكتالوج. هذا هو الجزء الوحيد في المشروع الذي يستعمل الشبكة،
/// ويعمل على الجهاز المتصل فقط (Builder).
/// </summary>
internal static class PackDownloader
{
    public static async Task<DownloadResult> DownloadAsync(HttpClient http, PackItem item, string outDir,
                                                           Action<string>? report = null, CancellationToken ct = default)
    {
        var inst = item.Installer;
        var path = Path.Combine(outDir, PackNaming.FileNameFor(item));

        // ملف سليم موجود من قبل: لا نعيد تنزيله
        if (File.Exists(path) && await HashFileAsync(path, ct) is { } existing &&
            existing.Equals(inst.Sha256, StringComparison.OrdinalIgnoreCase))
            return new DownloadResult(item, path, true, "already downloaded and verified", true);

        if (!Uri.TryCreate(inst.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return new DownloadResult(item, null, false, "unsupported download URL: " + inst.Url, false);

        var part = path + ".part";
        Exception? lastError = null;

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                long? total = resp.Content.Headers.ContentLength;

                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long done = 0;
                var lastReport = DateTime.UtcNow;
                await using (var src = await resp.Content.ReadAsStreamAsync(ct))
                await using (var dst = File.Create(part))
                {
                    var buffer = new byte[81920];
                    int n;
                    while ((n = await src.ReadAsync(buffer, ct)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                        hash.AppendData(buffer, 0, n);
                        done += n;
                        if (report != null && (DateTime.UtcNow - lastReport).TotalMilliseconds > 400)
                        {
                            lastReport = DateTime.UtcNow;
                            report($"{item.PackageId} {Mb(done)}{(total > 0 ? " / " + Mb(total.Value) : "")}");
                        }
                    }
                }

                var actual = Convert.ToHexString(hash.GetHashAndReset());
                if (!actual.Equals(inst.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(part);
                    // لا نعيد المحاولة: الملف المنزَّل لا يطابق الكتالوج (تغيّر المصدر أو عبث بالطريق)
                    return new DownloadResult(item, null, false,
                        $"SHA256 mismatch (expected {inst.Sha256}, got {actual}); file discarded", false);
                }

                File.Move(part, path, true);
                return new DownloadResult(item, path, true, $"downloaded {Mb(done)}", false);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is IOException ||
                                       (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                lastError = ex;
                if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }
        }

        TryDelete(part);
        return new DownloadResult(item, null, false, "download failed: " + lastError?.Message, false);
    }

    private static async Task<string?> HashFileAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var fs = File.OpenRead(path);
            return Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
        }
        catch (IOException) { return null; }
    }

    private static string Mb(long bytes) => $"{bytes / 1048576.0:0.0} MB";
    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
}
