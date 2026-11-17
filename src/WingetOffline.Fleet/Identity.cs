using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WingetOffline.Fleet;

/// <summary>هوية طويلة الأمد لكل طرف: شهادة ذاتية التوقيع، وتُثبَّت بصمتها SHA-256 عند الاقتران (pinning). لا سلطة شهادات.</summary>
public static class Identity
{
    public static X509Certificate2 Create(string commonName, int years = 5)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
        {
            new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2"),   // server + client auth
        }, false));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(years));
        // إعادة التحميل من PFX كي يكون المفتاح الخاص قابلاً للاستعمال مع SslStream على كل المنصات.
        return new X509Certificate2(cert.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }

    public static string Fingerprint(X509Certificate cert) =>
        Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));

    public static void Save(X509Certificate2 cert, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx));
        TryRestrict(path);
    }

    public static X509Certificate2 Load(string path) =>
        new(File.ReadAllBytes(path), (string?)null, X509KeyStorageFlags.Exportable);

    public static X509Certificate2 LoadOrCreate(string path, string commonName)
    {
        if (File.Exists(path)) return Load(path);
        var c = Create(commonName);
        Save(c, path);
        return c;
    }

    private static void TryRestrict(string path)
    {
        try { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch { /* أفضل جهد؛ على ويندوز يكفي مجلد بيانات المستخدم/الخدمة */ }
    }
}
