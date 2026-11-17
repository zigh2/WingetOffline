using System.Text.Json;
using System.Text.Json.Serialization;

namespace WingetOffline.Fleet;

public static class FleetJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}

/// <summary>برنامج واحد مثبّت على جهاز مُدار (لقطة للقراءة فقط).</summary>
public sealed class FleetApp
{
    public string Name { get; set; } = "";
    public string? Publisher { get; set; }
    public string? Version { get; set; }
    public string? PackageId { get; set; }          // معرّف الحزمة في الكتالوج إن طُوبق
    public string? LatestVersion { get; set; }
    public string Status { get; set; } = "Unknown"; // Unknown | UpToDate | UpdateAvailable
    public string? Architecture { get; set; }
    public string? Scope { get; set; }
}

/// <summary>ما يرفعه الـ Agent: هوية الجهاز وبرامجه. لا أوامر ولا مسارات حساسة.</summary>
public sealed class InventorySnapshot
{
    public int Schema { get; set; } = 1;
    public string DeviceId { get; set; } = "";       // معرّف عشوائي ثابت يولّده الـ Agent عند أول تشغيل
    public string HostName { get; set; } = "";
    public string Os { get; set; } = "";
    public string AgentVersion { get; set; } = "";
    public string? CatalogBuilt { get; set; }        // تاريخ بناء الكتالوج المستعمل في المطابقة
    public DateTime ScannedUtc { get; set; }
    public List<FleetApp> Apps { get; set; } = new();
}

/// <summary>طلب من المسؤول. في 0.2 نوعان للقراءة فقط: ping وinventory. أي نوع آخر يُرفض صراحةً.</summary>
public sealed class FleetRequest
{
    public string Type { get; set; } = "";           // "ping" | "inventory"
    public string Nonce { get; set; } = "";          // يمنع إعادة الإرسال
    public DateTime SentUtc { get; set; }
}

public sealed class FleetResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public InventorySnapshot? Inventory { get; set; }
}

public static class RequestTypes
{
    public const string Ping = "ping";
    public const string Inventory = "inventory";
    /// <summary>القائمة البيضاء الوحيدة في 0.2 (قراءة فقط). التثبيت/الإزالة تأتي في 0.3 بعد مراجعة أمنية.</summary>
    public static readonly IReadOnlySet<string> ReadOnly = new HashSet<string> { Ping, Inventory };
}
