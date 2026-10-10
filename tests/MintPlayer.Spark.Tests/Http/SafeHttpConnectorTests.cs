using System.Net;
using System.Net.Sockets;
using MintPlayer.Spark.Http;

namespace MintPlayer.Spark.Tests.Http;

/// <summary>
/// Spike S1 (<c>docs/seo_analyzer_PRD.md</c>): the connect callback resolves once, vets the answer and
/// dials only a vetted address. Nothing here opens a socket: the resolver and the dialler are fakes
/// that record what they were asked.
/// </summary>
public class SafeHttpConnectorTests
{
    private static readonly IPAddress Public = IPAddress.Parse("93.184.216.34");
    private static readonly IPAddress Private = IPAddress.Parse("10.0.0.1");

    /// <summary>Answers each lookup with the next entry; the last entry repeats.</summary>
    private sealed class ScriptedResolver(params IPAddress[][] answers) : ISafeHttpDnsResolver
    {
        public int Calls { get; private set; }

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
            => Task.FromResult(answers[Math.Min(Calls++, answers.Length - 1)]);
    }

    private sealed class FailingResolver : ISafeHttpDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
            => throw new SocketException((int)SocketError.HostNotFound);
    }

    private readonly List<IPAddress> dialled = [];

    private SafeHttpConnector Connector(ISafeHttpDnsResolver resolver, SafeHttpOptions? options = null)
        => new(new SafeHttpPolicy(options ?? new SafeHttpOptions()), resolver, (address, _, _) =>
        {
            dialled.Add(address);
            return ValueTask.FromResult<Stream>(new MemoryStream());
        });

    [Fact]
    public async Task Dns_rebinding_cannot_move_the_connection_after_the_check()
    {
        // A rebinding name answers public to the check and private to whatever asks next. The connector
        // must ask once and dial what it checked.
        var resolver = new ScriptedResolver([Public], [Private]);

        await Connector(resolver).ConnectAsync("rebind.test", 443, CancellationToken.None);

        resolver.Calls.Should().Be(1);
        dialled.Should().Equal(Public);
    }

    [Fact]
    public async Task A_rebinding_name_is_refused_on_the_next_connection()
    {
        var resolver = new ScriptedResolver([Public], [Private]);
        var connector = Connector(resolver);

        await connector.ConnectAsync("rebind.test", 443, CancellationToken.None);
        var second = async () => await connector.ConnectAsync("rebind.test", 443, CancellationToken.None);

        (await second.Should().ThrowAsync<SafeHttpBlockedException>()).Which.Reason.Should().Be(SafeHttpBlockReason.Address);
        dialled.Should().Equal(Public);
    }

    [Fact]
    public async Task A_mixed_answer_dials_only_the_public_address()
    {
        var resolver = new ScriptedResolver([Private, IPAddress.Loopback, Public]);

        await Connector(resolver).ConnectAsync("mixed.test", 443, CancellationToken.None);

        dialled.Should().Equal(Public);
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("[::1]")]
    [InlineData("::1")]
    [InlineData("[::ffff:127.0.0.1]")]
    [InlineData("[64:ff9b::a9fe:a9fe]")]
    public async Task An_ip_literal_is_vetted_without_a_lookup(string host)
    {
        var resolver = new ScriptedResolver([Public]);

        var act = async () => await Connector(resolver).ConnectAsync(host, 80, CancellationToken.None);

        (await act.Should().ThrowAsync<SafeHttpBlockedException>()).Which.Reason.Should().Be(SafeHttpBlockReason.Address);
        resolver.Calls.Should().Be(0);
        dialled.Should().BeEmpty();
    }

    [Fact]
    public async Task A_name_resolving_only_to_private_addresses_is_refused_without_naming_them()
    {
        var resolver = new ScriptedResolver([Private, IPAddress.Parse("169.254.169.254")]);

        var act = async () => await Connector(resolver).ConnectAsync("internal.test", 443, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<SafeHttpBlockedException>()).Which;
        thrown.Reason.Should().Be(SafeHttpBlockReason.Address);
        thrown.Message.Should().NotContain("10.0.0.1");
        thrown.Message.Should().NotContain("169.254");
        dialled.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unresolvable_or_empty_name_is_a_resolution_failure()
    {
        var failing = async () => await Connector(new FailingResolver()).ConnectAsync("nx.test", 443, CancellationToken.None);
        var empty = async () => await Connector(new ScriptedResolver([[]])).ConnectAsync("empty.test", 443, CancellationToken.None);

        (await failing.Should().ThrowAsync<SafeHttpBlockedException>()).Which.Reason.Should().Be(SafeHttpBlockReason.Resolution);
        (await empty.Should().ThrowAsync<SafeHttpBlockedException>()).Which.Reason.Should().Be(SafeHttpBlockReason.Resolution);
    }

    [Fact]
    public async Task A_port_outside_the_list_is_refused_before_resolving()
    {
        var resolver = new ScriptedResolver([Public]);

        var act = async () => await Connector(resolver).ConnectAsync("example.test", 6379, CancellationToken.None);

        (await act.Should().ThrowAsync<SafeHttpBlockedException>()).Which.Reason.Should().Be(SafeHttpBlockReason.Port);
        resolver.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Loopback_connects_only_when_explicitly_allowed()
    {
        var options = new SafeHttpOptions { AllowedNetworks = ["127.0.0.1/32"] };

        await Connector(new ScriptedResolver([IPAddress.Loopback]), options).ConnectAsync("local.test", 443, CancellationToken.None);

        dialled.Should().Equal(IPAddress.Loopback);
    }
}
