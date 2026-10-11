using System.Net;
using MintPlayer.Spark.Http;

namespace MintPlayer.Spark.Tests.Http;

/// <summary>
/// Spike S1 (<c>docs/seo_analyzer_PRD.md</c>): the address rule of the SSRF-safe client. Only global
/// unicast passes; addresses that carry an IPv4 address are judged by it.
/// </summary>
public class SafeHttpPolicyTests
{
    private static SafeHttpPolicy Default => new(new SafeHttpOptions());

    [Theory]
    // IPv4
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("100.64.0.1")]          // CGNAT
    [InlineData("100.127.255.254")]
    [InlineData("127.0.0.1")]
    [InlineData("127.53.0.1")]
    [InlineData("169.254.169.254")]     // cloud metadata
    [InlineData("169.254.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.0.0.170")]
    [InlineData("192.0.2.1")]
    [InlineData("192.168.1.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.51.100.7")]
    [InlineData("203.0.113.9")]
    [InlineData("224.0.0.1")]           // multicast
    [InlineData("239.255.255.250")]
    [InlineData("240.0.0.1")]           // reserved
    [InlineData("255.255.255.255")]     // broadcast
    // IPv6
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::127.0.0.1")]         // IPv4-compatible (deprecated)
    [InlineData("fc00::1")]             // ULA
    [InlineData("fd12:3456::1")]
    [InlineData("fe80::1")]             // link-local
    [InlineData("fec0::1")]             // site-local
    [InlineData("ff02::1")]             // multicast
    [InlineData("2001:db8::1")]         // documentation
    [InlineData("3fff::1")]             // documentation (RFC 9637)
    [InlineData("2001:10::1")]          // ORCHID, inside 2001::/23
    [InlineData("64:ff9b:1::1")]        // local-use NAT64
    // IPv4 hidden in IPv6
    [InlineData("::ffff:127.0.0.1")]    // IPv4-mapped
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("64:ff9b::7f00:1")]     // NAT64 of 127.0.0.1
    [InlineData("64:ff9b::a9fe:a9fe")]  // NAT64 of 169.254.169.254
    [InlineData("2002:7f00:1::1")]      // 6to4 of 127.0.0.1
    [InlineData("2002:c0a8:101::1")]    // 6to4 of 192.168.1.1
    [InlineData("2001:0:7f00:1::5db8:d822")]  // Teredo, server 127.0.0.1
    [InlineData("2001:0:5db8:d822::f5ff:fffe")] // Teredo, client 10.0.0.1 (inverted)
    public void Refuses_non_global_addresses(string address)
        => Default.IsAddressAllowed(IPAddress.Parse(address)).Should().BeFalse();

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("100.63.255.255")]      // just below CGNAT
    [InlineData("172.32.0.1")]          // just above 172.16/12
    [InlineData("2606:2800:220:1::1")]
    [InlineData("2a00:1450:4001::1")]
    [InlineData("::ffff:93.184.216.34")]
    [InlineData("64:ff9b::5db8:d822")]  // NAT64 of 93.184.216.34
    [InlineData("2002:5db8:d822::1")]   // 6to4 of 93.184.216.34
    public void Allows_global_unicast(string address)
        => Default.IsAddressAllowed(IPAddress.Parse(address)).Should().BeTrue();

    [Fact]
    public void An_allowed_network_opens_exactly_that_network()
    {
        var policy = new SafeHttpPolicy(new SafeHttpOptions { AllowedNetworks = ["127.0.0.1/32"] });

        policy.IsAddressAllowed(IPAddress.Parse("127.0.0.1")).Should().BeTrue();
        policy.IsAddressAllowed(IPAddress.Parse("::ffff:127.0.0.1")).Should().BeTrue();
        policy.IsAddressAllowed(IPAddress.Parse("127.0.0.2")).Should().BeFalse();
        policy.IsAddressAllowed(IPAddress.Parse("::1")).Should().BeFalse();
        policy.IsAddressAllowed(IPAddress.Parse("10.0.0.1")).Should().BeFalse();
    }

    [Fact]
    public void A_blocked_network_wins_over_global_and_allowed_and_is_found_inside_ipv6()
    {
        var policy = new SafeHttpPolicy(new SafeHttpOptions
        {
            AllowedNetworks = ["93.184.216.0/24"],
            BlockedNetworks = ["93.184.216.34/32"],
        });

        policy.IsAddressAllowed(IPAddress.Parse("93.184.216.34")).Should().BeFalse();
        policy.IsAddressAllowed(IPAddress.Parse("64:ff9b::5db8:d822")).Should().BeFalse();
        policy.IsAddressAllowed(IPAddress.Parse("2002:5db8:d822::1")).Should().BeFalse();
        policy.IsAddressAllowed(IPAddress.Parse("93.184.216.35")).Should().BeTrue();
    }

    [Theory]
    [InlineData("ftp://example.com/", SafeHttpBlockReason.Scheme)]
    [InlineData("file:///etc/passwd", SafeHttpBlockReason.Scheme)]
    [InlineData("gopher://example.com/", SafeHttpBlockReason.Scheme)]
    [InlineData("http://example.com:22/", SafeHttpBlockReason.Port)]
    [InlineData("https://example.com:8443/", SafeHttpBlockReason.Port)]
    public void Refuses_other_schemes_and_ports(string uri, SafeHttpBlockReason reason)
    {
        var act = () => Default.CheckUri(new Uri(uri));
        act.Should().Throw<SafeHttpBlockedException>().Which.Reason.Should().Be(reason);
    }

    [Theory]
    [InlineData("http://example.com/")]
    [InlineData("https://example.com/")]
    [InlineData("https://example.com:443/x")]
    public void Allows_http_and_https_on_default_ports(string uri)
    {
        var act = () => Default.CheckUri(new Uri(uri));
        act.Should().NotThrow();
    }
}
