using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace WingetOffline.Fleet;

/// <summary>
/// خادم الـ Agent: TLS بمصادقة متبادلة ببصمات مثبّتة، من عناوين خاصة فقط، وبطلبات قراءة فقط.
/// لا يقبل أي نوع طلب خارج <see cref="RequestTypes.ReadOnly"/>.
/// </summary>
public sealed class AgentServer : IDisposable
{
    private readonly X509Certificate2 _cert;
    private readonly TrustStore _admins;
    private readonly AuditLog _audit;
    private readonly Func<FleetRequest, CancellationToken, Task<FleetResponse>> _handler;
    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<string, DateTime> _nonces = new();
    private readonly ConcurrentDictionary<string, (int fails, DateTime until)> _penalty = new();
    private readonly SemaphoreSlim _slots = new(8);
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(5);
    public int Port { get; private set; }

    public AgentServer(X509Certificate2 cert, TrustStore admins, AuditLog audit, int port,
        Func<FleetRequest, CancellationToken, Task<FleetResponse>> handler, IPAddress? bind = null)
    {
        _cert = cert; _admins = admins; _audit = audit; _handler = handler;
        _listener = new TcpListener(bind ?? IPAddress.Any, port);
    }

    public void Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => AcceptLoop(_cts.Token));
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch { continue; }
            _ = Task.Run(() => Serve(client, ct), ct);
        }
    }

    private async Task Serve(TcpClient client, CancellationToken ct)
    {
        using var _c = client;
        var ep = client.Client.RemoteEndPoint as IPEndPoint;
        var who = ep?.Address.ToString() ?? "?";
        if (ep == null || !PrivateNetwork.IsAllowed(ep.Address)) { _audit.Append(who, "connect", "rejected: public address"); return; }
        if (_penalty.TryGetValue(who, out var p) && p.until > DateTime.UtcNow) { return; }
        if (!await _slots.WaitAsync(0, ct)) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            string? clientFp = null;
            using var ssl = new SslStream(client.GetStream(), false, (_, cert, _, _) =>
            {
                if (cert == null) return false;
                clientFp = Identity.Fingerprint(cert);
                return _admins.IsTrusted(clientFp);
            });
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _cert,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                }, timeout.Token);
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException)
            {
                Penalize(who);
                _audit.Append(who, "connect", $"rejected: not a trusted admin ({(clientFp ?? "no certificate")[..Math.Min(12, (clientFp ?? "no certificate").Length)]})");
                return;
            }

            var label = _admins.LabelOf(clientFp!) ?? clientFp![..12];
            var req = await Framing.ReadAsync<FleetRequest>(ssl, timeout.Token);
            var resp = await HandleAsync(req, label, timeout.Token);
            await Framing.WriteAsync(ssl, resp, timeout.Token);
            _audit.Append($"{label}@{who}", req.Type, resp.Ok ? "ok" : "error: " + resp.Error);
        }
        catch { /* اتصال انقطع أو طلب مشوّه: لا شيء يُكشف */ }
        finally { _slots.Release(); }
    }

    internal async Task<FleetResponse> HandleAsync(FleetRequest req, string actor, CancellationToken ct)
    {
        if (!RequestTypes.ReadOnly.Contains(req.Type))
            return new FleetResponse { Ok = false, Error = "request type not allowed" };
        var age = DateTime.UtcNow - req.SentUtc.ToUniversalTime();
        if (age > Freshness || age < -Freshness) return new FleetResponse { Ok = false, Error = "stale request" };
        if (string.IsNullOrEmpty(req.Nonce) || req.Nonce.Length > 64) return new FleetResponse { Ok = false, Error = "bad nonce" };
        PruneNonces();
        if (!_nonces.TryAdd(req.Nonce, DateTime.UtcNow)) return new FleetResponse { Ok = false, Error = "replayed request" };
        try { return await _handler(req, ct); }
        catch (Exception ex) { return new FleetResponse { Ok = false, Error = ex.GetType().Name }; }
    }

    private void PruneNonces()
    {
        var cutoff = DateTime.UtcNow - Freshness - Freshness;
        foreach (var kv in _nonces) if (kv.Value < cutoff) _nonces.TryRemove(kv.Key, out _);
    }

    private void Penalize(string who)
    {
        _penalty.AddOrUpdate(who, _ => (1, DateTime.UtcNow.AddSeconds(2)),
            (_, o) => (o.fails + 1, DateTime.UtcNow.AddSeconds(Math.Min(300, Math.Pow(2, o.fails + 1)))));
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _listener.Stop(); } catch { }
        try { _loop?.Wait(1000); } catch { }
        _cts?.Dispose();
    }
}
