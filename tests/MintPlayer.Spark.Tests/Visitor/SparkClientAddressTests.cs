using System.Net;
using MintPlayer.Spark.Abstractions.Visitor;

namespace MintPlayer.Spark.Tests.Visitor;

/// <summary>
/// The network-address key every per-address limit uses (commerce PRD X10, F5): IPv4 as it is, IPv6 grouped
/// to the /64 one subscriber owns, IPv4-mapped IPv6 as the IPv4 address it carries.
/// </summary>
public class SparkClientAddressTests
{
    [Theory]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:3::1", "2001:db8:1:3::/64")]
    [InlineData("::1", "::/64")]
    public void An_IPv6_address_is_keyed_by_its_64(string address, string key)
        => SparkClientAddress.Key(IPAddress.Parse(address)).Should().Be(key);

    [Fact]
    public void Two_addresses_in_one_64_share_a_key_and_the_neighbouring_64_does_not()
    {
        // Privacy extensions (RFC 8981) rotate the low 64 bits by themselves: one host, many exact addresses.
        var a = SparkClientAddress.Key(IPAddress.Parse("2001:db8:0:1:1111:2222:3333:4444"));
        var b = SparkClientAddress.Key(IPAddress.Parse("2001:db8:0:1:9999:8888:7777:6666"));
        var neighbour = SparkClientAddress.Key(IPAddress.Parse("2001:db8:0:2:1111:2222:3333:4444"));

        a.Should().Be(b);
        neighbour.Should().NotBe(a);
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    public void An_IPv4_address_is_keyed_as_it_is_on_either_stack(string address, string key)
        => SparkClientAddress.Key(IPAddress.Parse(address)).Should().Be(key);

    [Fact]
    public void No_address_is_unknown()
        => SparkClientAddress.Key(null).Should().Be("unknown");
}
