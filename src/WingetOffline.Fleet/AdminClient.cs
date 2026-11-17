using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace WingetOffline.Fleet;

/// <summary>عميل المسؤول: يتصل بجهاز عنوانه خاص فقط، ويقبل شهادة الجهاز فقط إن طابقت البصمة المثبّتة.</summary>
public sealed class AdminClient
{
    private readonly X509Certificate2 _identity;
    public AdminClient(X509Certificate2 identity) => _identity = identity;

    public async Task<FleetResponse> SendAsync(string host, int port, string expectedFingerprint, string type, CancellationToken ct, TimeSpan? timeout = null)
    {
        if (!RequestTypes.ReadOnly.Contains(type)) throw new ArgumentException("Only read-only requests exist in this version.", nameof(type));
        var pinned = TrustStore.Normalize(expectedFingerprint);

        var addrs = IPAddress.TryParse(host, out var one) ? new[] { one } : await Dns.GetHostAddressesAsync(host, ct);
        var ip = addrs.FirstOrDefault(PrivateNetwork.IsAllowed)
                 ?? throw new InvalidOperationException("Refusing to connect: the address is not on a private network.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        using var tcp = new TcpClient(ip.AddressFamily);
        await tcp.ConnectAsync(ip, port, cts.Token);
        using var ssl = new SslStream(tcp.GetStream(), false,
            (_, cert, _, _) => cert != null && string.Equals(Identity.Fingerprint(cert), pinned, StringComparison.OrdinalIgnoreCase));
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "wingetoffline-agent",           // الاسم غير مهم: الاعتماد على البصمة لا على الاسم
            ClientCertificates = new X509CertificateCollection { _identity },
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        }, cts.Token);

        var req = new FleetRequest { Type = type, Nonce = Guid.NewGuid().ToString("N"), SentUtc = DateTime.UtcNow };
        await Framing.WriteAsync(ssl, req, cts.Token);
        return await Framing.ReadAsync<FleetResponse>(ssl, cts.Token);
    }
}
