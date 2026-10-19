using System.Net;
using System.Net.Sockets;

namespace MintPlayer.Spark.Abstractions.Visitor;

/// <summary>
/// The caller's network address as a limit key (commerce PRD X10, F5): an IPv4 address as it is, an IPv6
/// address grouped to its /64.
/// </summary>
/// <remarks>
/// <para>
/// One IPv6 subscriber is handed a whole /64 (RFC 6177 recommends at least that per site; most ISPs give a
/// /56 or a /48), and privacy extensions (RFC 8981) rotate the low 64 bits on their own. Keyed on the exact
/// address, one host walks through as many budgets as it likes without changing networks. The /64 is the
/// smallest unit an ISP assigns, so it is the smallest one a limit may trust.
/// </para>
/// <para>
/// An IPv4-mapped IPv6 address (<c>::ffff:203.0.113.7</c>, what a dual-stack socket reports for an IPv4
/// peer) is keyed as the IPv4 address it carries, so the same caller has one key on either stack.
/// </para>
/// </remarks>
public static class SparkClientAddress
{
    /// <summary>The key for <paramref name="address"/>: <c>203.0.113.7</c>, <c>2001:db8:1:2::/64</c>, or <c>unknown</c>.</summary>
    public static string Key(IPAddress? address)
    {
        if (address is null)
            return "unknown";
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return address.ToString();

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        bytes[8..].Clear();
        return new IPAddress(bytes).ToString() + "/64";
    }
}
