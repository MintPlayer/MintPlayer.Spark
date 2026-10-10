using System.Net;
using System.Net.Sockets;

namespace MintPlayer.Spark.Http;

/// <summary>
/// The checks of one SSRF-safe client, parsed once from its <see cref="SafeHttpOptions"/>: which URIs
/// may be requested (scheme, port) and which addresses may be connected to.
/// <para>
/// The address rule is an allow-list: only global unicast passes. Everything else is refused: loopback,
/// RFC 1918, CGNAT, link-local (incl. the cloud metadata address 169.254.169.254), multicast, broadcast,
/// documentation and reserved ranges, IPv6 ULA, link-local and multicast. Addresses that carry an IPv4
/// address (IPv4-mapped, NAT64, 6to4, Teredo) are judged by that IPv4 address, so a private target cannot
/// hide inside an IPv6 literal.
/// </para>
/// </summary>
internal sealed class SafeHttpPolicy
{
    private readonly HashSet<int> ports;
    private readonly System.Net.IPNetwork[] allowed;
    private readonly System.Net.IPNetwork[] blocked;

    public SafeHttpPolicy(SafeHttpOptions options)
    {
        ports = [.. options.AllowedPorts];
        allowed = [.. options.AllowedNetworks.Select(System.Net.IPNetwork.Parse)];
        blocked = [.. options.BlockedNetworks.Select(System.Net.IPNetwork.Parse)];
    }

    public bool IsPortAllowed(int port) => ports.Contains(port);

    /// <summary>Throws unless <paramref name="uri"/> is absolute http(s) on an allowed port.</summary>
    public void CheckUri(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new SafeHttpBlockedException(SafeHttpBlockReason.Scheme);
        if (!IsPortAllowed(uri.Port))
            throw new SafeHttpBlockedException(SafeHttpBlockReason.Port);
    }

    /// <summary>Whether a connection to <paramref name="address"/> is allowed.</summary>
    public bool IsAddressAllowed(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (blocked.Length > 0 && (Contains(blocked, address) || EmbeddedIPv4(address).Any(v4 => Contains(blocked, v4))))
            return false;
        if (Contains(allowed, address))
            return true;
        return IsGlobalUnicast(address);
    }

    private static bool Contains(System.Net.IPNetwork[] networks, IPAddress address)
    {
        foreach (var network in networks)
            if (network.Contains(address))
                return true;
        return false;
    }

    /// <summary>Global unicast, judged without any configured exception.</summary>
    internal static bool IsGlobalUnicast(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
            return IsGlobalIPv4(address.GetAddressBytes());
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return false;

        var b = address.GetAddressBytes();

        // NAT64 (64:ff9b::/96, RFC 6052) is outside 2000::/3 but is how an IPv6-only network reaches the
        // IPv4 internet: allowed exactly when the IPv4 address it carries is.
        if (IsNat64(b))
            return IsGlobalIPv4(b[12..16]);

        // Only 2000::/3 is global unicast. This refuses ::, ::1, IPv4-compatible ::a.b.c.d, the local-use
        // NAT64 prefix 64:ff9b:1::/48, fc00::/7, fe80::/10, fec0::/10 and ff00::/8 in one test.
        if ((b[0] & 0xE0) != 0x20)
            return false;

        // 2002::/16, 6to4: the IPv4 address follows the prefix.
        if (b[0] == 0x20 && b[1] == 0x02)
            return IsGlobalIPv4(b[2..6]);

        if (b[0] == 0x20 && b[1] == 0x01)
        {
            // 2001::/32, Teredo: the server's IPv4 address in bits 32-63, the client's (inverted) in the last 32.
            if (b[2] == 0 && b[3] == 0)
                return IsGlobalIPv4(b[4..8]) && IsGlobalIPv4([.. b[12..16].Select(x => (byte)~x)]);
            // 2001:db8::/32, documentation.
            if (b[2] == 0x0d && b[3] == 0xb8)
                return false;
            // The rest of 2001::/23, IETF protocol assignments (ORCHID, benchmarking, AMT, …).
            if (b[2] < 0x02)
                return false;
        }

        // 3fff::/20, documentation (RFC 9637).
        if (b[0] == 0x3f && b[1] == 0xff && (b[2] & 0xF0) == 0)
            return false;

        return true;
    }

    private static bool IsNat64(byte[] b)
        => b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b[4..12].All(x => x == 0);

    /// <summary>The IPv4 addresses an IPv6 address carries (NAT64, 6to4, Teredo server and client).</summary>
    private static IEnumerable<IPAddress> EmbeddedIPv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            yield break;
        var b = address.GetAddressBytes();
        if (IsNat64(b))
            yield return new IPAddress(b[12..16]);
        else if (b[0] == 0x20 && b[1] == 0x02)
            yield return new IPAddress(b[2..6]);
        else if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0)
        {
            yield return new IPAddress(b[4..8]);
            yield return new IPAddress([.. b[12..16].Select(x => (byte)~x)]);
        }
    }

    // Each refused IPv4 range as (first address, prefix length): IANA special-purpose registry entries
    // that are not globally reachable, plus multicast and the reserved 240/4 (which holds broadcast).
    private static readonly (uint Network, int Prefix)[] NonGlobalIPv4 =
    [
        (0x00000000, 8),   // 0.0.0.0/8 "this network"
        (0x0A000000, 8),   // 10.0.0.0/8 private
        (0x64400000, 10),  // 100.64.0.0/10 CGNAT
        (0x7F000000, 8),   // 127.0.0.0/8 loopback
        (0xA9FE0000, 16),  // 169.254.0.0/16 link-local, incl. 169.254.169.254 (cloud metadata)
        (0xAC100000, 12),  // 172.16.0.0/12 private
        (0xC0000000, 24),  // 192.0.0.0/24 IETF protocol assignments
        (0xC0000200, 24),  // 192.0.2.0/24 TEST-NET-1
        (0xC0586300, 24),  // 192.88.99.0/24 6to4 relay anycast (deprecated)
        (0xC0A80000, 16),  // 192.168.0.0/16 private
        (0xC6120000, 15),  // 198.18.0.0/15 benchmarking
        (0xC6336400, 24),  // 198.51.100.0/24 TEST-NET-2
        (0xCB007100, 24),  // 203.0.113.0/24 TEST-NET-3
        (0xE0000000, 4),   // 224.0.0.0/4 multicast
        (0xF0000000, 4),   // 240.0.0.0/4 reserved, incl. 255.255.255.255 broadcast
    ];

    private static bool IsGlobalIPv4(byte[] b)
    {
        var value = (uint)b[0] << 24 | (uint)b[1] << 16 | (uint)b[2] << 8 | b[3];
        foreach (var (network, prefix) in NonGlobalIPv4)
        {
            var mask = uint.MaxValue << (32 - prefix);
            if ((value & mask) == network)
                return false;
        }
        return true;
    }
}
