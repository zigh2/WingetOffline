using System.Net;
using System.Net.Sockets;

namespace WingetOffline.Fleet;

/// <summary>يقبل الشبكات الخاصة فقط (RFC 1918 وlink-local وloopback وfc00::/7). أي عنوان عام يُرفض.</summary>
public static class PrivateNetwork
{
    public static bool IsAllowed(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            return ip.IsIPv6LinkLocal || (b[0] & 0xFE) == 0xFC;
        }
        return false;
    }
}
