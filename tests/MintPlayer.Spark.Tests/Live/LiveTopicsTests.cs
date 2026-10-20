using System.Net.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Live;
using MintPlayer.Spark.Live;
using MintPlayer.Spark.Tests.Organizations;

namespace MintPlayer.Spark.Tests.Live;

/// <summary>
/// Live topics (commerce PRD X2, spike S17): a broadcast value published once and retained, a private stream
/// per identity, the server's clock in frames, and socket caps keyed by identity.
/// </summary>
public class LiveTopicsTests : OrganizationsTestHost
{
    private static readonly Uri Server = new("http://localhost/");

    /// <summary>Anyone may follow <c>room:*</c> and its private streams; <c>staff:*</c> only a signed-in user.</summary>
    private sealed class RoomPolicy : ISparkLiveTopicPolicy
    {
        public string Prefix => "room:";
        public bool CanSubscribe(string topic, SparkLiveViewer viewer) => topic != "room:closed";
    }

    private sealed class StaffPolicy : ISparkLiveTopicPolicy
    {
        public string Prefix => "staff:";
        public bool CanSubscribe(string topic, SparkLiveViewer viewer) => viewer.UserId is not null;
    }

    private sealed record Progress(int ServedUpTo, int QueueLength);

    public LiveTopicsTests()
    {
        ExtraServices = services =>
        {
            services.AddSingleton<ISparkLiveTopicPolicy, RoomPolicy>();
            services.AddSingleton<ISparkLiveTopicPolicy, StaffPolicy>();
        };
    }

    private async Task<LiveTestClient> ConnectAsync(string? userId = null)
        => await LiveTestClient.ConnectAsync(Factory.CreateWebSocketClient(), Server, request =>
        {
            if (userId is not null)
                request.Headers[OgPrincipalStartupFilter.Header] = userId;
        });

    private ISparkLiveTopics Topics => Factory.GetService<ISparkLiveTopics>();

    [Fact]
    public async Task A_published_value_reaches_every_subscriber_and_a_late_joiner_gets_the_retained_one_at_once()
    {
        await using var first = await ConnectAsync();
        await first.SubscribeTopicAsync("q", "room:R1");
        (await first.TryNextAsync(TimeSpan.FromMilliseconds(500))).Should().BeNull("nothing was published yet");

        Topics.Publish("room:R1", new Progress(10, 500));
        var frame = await first.NextAsync("topic", "q");
        frame.GetProperty("data").GetProperty("servedUpTo").GetInt32().Should().Be(10, "the HTTP JSON options apply: camelCase");
        frame.GetProperty("serverTime").GetInt64().Should().BeGreaterThan(0);
        var seq = frame.GetProperty("seq").GetInt64();

        await using var late = await ConnectAsync();
        await late.SubscribeTopicAsync("q", "room:R1");
        (await late.NextAsync("topic", "q")).GetProperty("data").GetProperty("queueLength").GetInt32().Should().Be(500);

        Topics.Publish("room:R1", new Progress(20, 480));
        (await first.NextAsync("topic", "q")).GetProperty("seq").GetInt64().Should().BeGreaterThan(seq);
        (await late.NextAsync("topic", "q")).GetProperty("data").GetProperty("servedUpTo").GetInt32().Should().Be(20);
    }

    [Fact]
    public async Task An_unclaimed_topic_and_a_refused_one_are_the_same_unavailable()
    {
        await using var client = await ConnectAsync();

        await client.SubscribeTopicAsync("a", "nobody:x");
        (await client.NextAsync("error", "a")).GetProperty("code").GetString().Should().Be(LiveCodes.Unavailable);

        await client.SubscribeTopicAsync("b", "room:closed");
        (await client.NextAsync("error", "b")).GetProperty("code").GetString().Should().Be(LiveCodes.Unavailable);

        await client.SubscribeTopicAsync("c", "staff:board");
        (await client.NextAsync("error", "c")).GetProperty("code").GetString().Should().Be(LiveCodes.Unavailable, "the longest matching prefix decides, and staff: wants a user");

        await client.SubscribeTopicAsync("d", "has space");
        (await client.NextAsync("error", "d")).GetProperty("code").GetString().Should().Be(LiveCodes.Invalid);
    }

    [Fact]
    public async Task A_private_stream_reaches_only_its_own_identity()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");
        await using var annSocket = await ConnectAsync(ann.Id);
        await using var bobSocket = await ConnectAsync(bob.Id);
        await annSocket.SubscribePrivateAsync("me", "room:R1");
        await bobSocket.SubscribePrivateAsync("me", "room:R1");

        Topics.PublishPrivate("user:" + ann.Id, "room:R1", new { admitted = true });

        (await annSocket.NextAsync("topic", "me")).GetProperty("data").GetProperty("admitted").GetBoolean().Should().BeTrue();
        (await bobSocket.TryNextAsync(TimeSpan.FromSeconds(1))).Should().BeNull("a private value is for one identity only");

        // Retained for a socket of the same identity that connects later (the lifetime rotation).
        await using var annAgain = await ConnectAsync(ann.Id);
        await annAgain.SubscribePrivateAsync("me", "room:R1");
        (await annAgain.NextAsync("topic", "me")).GetProperty("data").GetProperty("admitted").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task An_address_never_gets_a_private_stream()
    {
        // Everyone behind one NAT shares the address: a private stream keyed on it would be shared too.
        await using var anonymous = await ConnectAsync();
        await anonymous.SubscribePrivateAsync("me", "room:R1");
        (await anonymous.NextAsync("error", "me")).GetProperty("code").GetString().Should().Be(LiveCodes.Unavailable);

        var act = () => Topics.PublishPrivate("ip:203.0.113.7", "room:R1", new { admitted = true });
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Every_socket_learns_the_server_clock_from_its_first_frame()
    {
        await using var client = await ConnectAsync();
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await client.SubscribeTopicAsync("q", "room:clock");
        await client.TryNextAsync(TimeSpan.FromMilliseconds(300));

        client.LastServerTime.HasValue.Should().BeTrue("the hello frame carries serverTime");
        client.LastServerTime!.Value.Should().BeInRange(before - 60_000, before + 60_000);
    }

    [Fact]
    public async Task Anonymous_sockets_count_per_address_with_the_address_cap_not_the_user_cap()
    {
        LiveOptions = o =>
        {
            o.MaxSocketsPerUser = 1;
            o.MaxSocketsPerAddress = 2;
        };
        await using var one = await ConnectAsync();
        await using var two = await ConnectAsync();
        await two.SubscribeTopicAsync("q", "room:R1");
        await two.TryNextAsync(TimeSpan.FromMilliseconds(300));
        two.State.Should().Be(WebSocketState.Open, "two people behind one address are not one user");

        await using var three = await ConnectAsync();
        (await three.ClosedAsync(TimeSpan.FromSeconds(5))).Should().Be((WebSocketCloseStatus)LiveCodes.CloseTooManySockets);
    }
}
