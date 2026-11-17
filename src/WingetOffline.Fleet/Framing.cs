using System.Buffers.Binary;
using System.Text.Json;

namespace WingetOffline.Fleet;

/// <summary>إطار = 4 بايت طول (big-endian) + JSON UTF-8. حدّ أقصى للحجم يمنع استنزاف الذاكرة.</summary>
public static class Framing
{
    public const int MaxFrameBytes = 8 * 1024 * 1024;

    public static async Task WriteAsync<T>(Stream s, T value, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, FleetJson.Options);
        if (payload.Length > MaxFrameBytes) throw new InvalidDataException("Frame too large.");
        var head = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(head, payload.Length);
        await s.WriteAsync(head, ct);
        await s.WriteAsync(payload, ct);
        await s.FlushAsync(ct);
    }

    public static async Task<T> ReadAsync<T>(Stream s, CancellationToken ct)
    {
        var head = new byte[4];
        await ReadExactAsync(s, head, ct);
        var len = BinaryPrimitives.ReadInt32BigEndian(head);
        if (len <= 0 || len > MaxFrameBytes) throw new InvalidDataException("Bad frame length.");
        var buf = new byte[len];
        await ReadExactAsync(s, buf, ct);
        return JsonSerializer.Deserialize<T>(buf, FleetJson.Options) ?? throw new InvalidDataException("Empty frame.");
    }

    private static async Task ReadExactAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        int n = 0;
        while (n < buf.Length)
        {
            int r = await s.ReadAsync(buf.AsMemory(n), ct);
            if (r == 0) throw new EndOfStreamException();
            n += r;
        }
    }
}
