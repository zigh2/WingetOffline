using System.Text.Encodings.Web;
using System.Text.Json;

namespace WingetOffline.Core.Packing;

public sealed record UpdateEntry(string PackageId, string? Version, string? Installed, string? Name);

/// <summary>
/// ملف "التحديثات المطلوبة": يُصدَّر من الجهاز المعزول، ويقرؤه الجهاز المتصل لبناء حزمة المثبّتات.
/// يقبل أيضاً مخرجات <c>wgo scan --json</c> مباشرة.
/// </summary>
public static class UpdateList
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // العربية وبقية اللغات كما هي لا \uXXXX
    };

    public static void Write(string path, IEnumerable<UpdateEntry> entries)
    {
        var payload = new
        {
            format = 1,
            createdUtc = DateTime.UtcNow,
            items = entries.Select(e => new { e.PackageId, e.Version, e.Installed, e.Name }).ToList(),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, Json));
    }

    public static List<UpdateEntry> Read(string path, bool includeLowConfidence = false)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var result = new List<UpdateEntry>();
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var items))
        {
            foreach (var e in items.EnumerateArray())
            {
                var id = Str(e, "packageId");
                if (id == null) continue;
                // ملفات التصدير من صفحة Installed تحمل "confidence": المطابقة الضعيفة لا تدخل إلا بطلب صريح.
                if (!includeLowConfidence && Str(e, "confidence") is "Low" or "None") continue;
                result.Add(new UpdateEntry(id, Str(e, "version"), Str(e, "installed"), Str(e, "name")));
            }
        }
        else if (root.ValueKind == JsonValueKind.Array) // مخرجات wgo scan --json
        {
            foreach (var e in root.EnumerateArray())
            {
                var id = Str(e, "packageId");
                if (id == null || Str(e, "status") != "UPDATE") continue;
                if (!includeLowConfidence && Str(e, "confidence") is "Low" or "None") continue;
                result.Add(new UpdateEntry(id, Str(e, "latest"), Str(e, "installed"), Str(e, "name")));
            }
        }
        return result.GroupBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? (string.IsNullOrWhiteSpace(p.GetString()) ? null : p.GetString())
            : null;
}
