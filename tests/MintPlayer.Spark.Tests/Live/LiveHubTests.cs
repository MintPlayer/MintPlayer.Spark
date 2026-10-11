using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Live;
using NSubstitute;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Live;

/// <summary>
/// The change sources meet in <see cref="LiveHub"/> (D33): a change is routed to the subscriptions watching its
/// document or collection; under <c>Both</c> the second report of one write (id + change vector) is dropped;
/// each source is ignored when <c>Spark:Live:Source</c> excludes it, while an explicit notifier call is always
/// honoured; and a user holds at most <c>MaxSocketsPerUser</c> sockets.
/// </summary>
public class LiveHubTests
{
    private sealed class Probe(string[]? documents = null, string[]? collections = null) : LiveSubscription(null!, "probe")
    {
        public int Dirty;
        public override IReadOnlyCollection<string> Documents => documents ?? [];
        public override IReadOnlyCollection<string> Collections => collections ?? [];
        protected override void OnDirty() => Interlocked.Increment(ref Dirty);
        public override Task<LiveOutcome> EvaluateAsync(IServiceProvider services, CancellationToken cancellationToken) => Task.FromResult(LiveOutcome.Unchanged);
    }

    private static LiveHub Hub(SparkLiveSource source, int maxSockets = 5)
    {
        var options = Substitute.For<IOptionsMonitor<SparkLiveOptions>>();
        options.CurrentValue.Returns(new SparkLiveOptions { Source = source, MaxSocketsPerUser = maxSockets });
        // A substitute store: these tests register under Actions only, or never reach the Changes connection.
        return new LiveHub(Substitute.For<IDocumentStore>(), options, NullLogger<LiveHub>.Instance);
    }

    [Fact]
    public async Task A_change_reaches_the_subscriptions_watching_its_document_or_its_collection_only()
    {
        using var hub = Hub(SparkLiveSource.Actions);
        var onDocument = new Probe(documents: ["Notes/1"]);
        var onCollection = new Probe(collections: ["Notes"]);
        var elsewhere = new Probe(documents: ["Notes/2"], collections: ["Sites"]);
        foreach (var probe in new[] { onDocument, onCollection, elsewhere })
            await hub.RegisterAsync(probe);

        hub.Publish(new LiveChange("notes/1", "Notes", "A:1", FromChanges: false));

        onDocument.Dirty.Should().Be(1, "ids are matched case-insensitively, as RavenDB does");
        onCollection.Dirty.Should().Be(1);
        elsewhere.Dirty.Should().Be(0);
    }

    [Fact]
    public async Task Under_Both_the_echo_of_one_write_from_the_other_source_is_dropped()
    {
        using var hub = Hub(SparkLiveSource.Actions);
        var probe = new Probe(documents: ["Notes/1"]);
        await hub.RegisterAsync(probe);

        // The store event and the Changes notification of one write carry the same id and change vector. (On
        // an Actions hub, so no Changes connection is opened; de-duplication does not depend on the source.)
        hub.Publish(new LiveChange("Notes/1", "Notes", "A:7", FromChanges: false));
        hub.Publish(new LiveChange("Notes/1", "Notes", "A:7", FromChanges: false));
        hub.Publish(new LiveChange("Notes/1", "Notes", "A:8", FromChanges: false));

        probe.Dirty.Should().Be(2, "the second report of A:7 is an echo; A:8 is a new write");
        hub.Counters.Deduplicated.Should().Be(1);
    }

    [Fact]
    public async Task Each_source_is_ignored_when_the_configuration_excludes_it_but_the_notifier_never_is()
    {
        using var actionsOnly = Hub(SparkLiveSource.Actions);
        var probe = new Probe(documents: ["Notes/1"]);
        await actionsOnly.RegisterAsync(probe);

        actionsOnly.Publish(new LiveChange("Notes/1", "Notes", "A:1", FromChanges: true));
        probe.Dirty.Should().Be(0, "Source = Actions: a Changes notification is not a source");

        actionsOnly.Changed("Notes/1");
        probe.Dirty.Should().Be(1, "an explicit notifier call is intent, honoured under every source");

        actionsOnly.CollectionChanged("Notes");
        new Probe().Dirty.Should().Be(0);
    }

    [Fact]
    public void A_user_holds_at_most_MaxSocketsPerUser_sockets_and_a_closed_one_frees_its_slot()
    {
        using var hub = Hub(SparkLiveSource.Actions, maxSockets: 2);

        hub.TryAcquireSocket("user:ann").Should().BeTrue();
        hub.TryAcquireSocket("user:ann").Should().BeTrue();
        hub.TryAcquireSocket("user:ann").Should().BeFalse();
        hub.TryAcquireSocket("user:bob").Should().BeTrue("the count is per user");

        hub.ReleaseSocket("user:ann");
        hub.TryAcquireSocket("user:ann").Should().BeTrue();
    }

    [Fact]
    public async Task An_unregistered_subscription_is_no_longer_reached()
    {
        using var hub = Hub(SparkLiveSource.Actions);
        var probe = new Probe(collections: ["Notes"]);
        await hub.RegisterAsync(probe);
        hub.Unregister(probe);

        hub.Publish(new LiveChange("Notes/1", "Notes", null, FromChanges: false));

        probe.Dirty.Should().Be(0);
    }
}
