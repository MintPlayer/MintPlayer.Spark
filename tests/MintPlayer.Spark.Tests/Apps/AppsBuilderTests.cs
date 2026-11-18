using MintPlayer.Spark.Apps;
using MintPlayer.Spark.Apps.Catalog;
using MintPlayer.Webhooks;

namespace MintPlayer.Spark.Tests.Apps;

/// <summary>
/// #691 (<c>docs/apps_webhooks_PRD.md</c> D6, D18, D21): what <c>AddApps(apps => …)</c> accepts, and the catalog's rules for
/// event names. Every mistake fails at startup, with the type and the name in the message.
/// </summary>
public class AppsBuilderTests
{
    private static Action Act(Action action) => action;

    private sealed record Undecorated(string Id);

    private sealed class FakeSigner(string version) : IWebhookSigner
    {
        public string Version => version;
        public byte[] Sign(WebhookSigningInput input) => [1, 2, 3];
    }

    private sealed class NoOwners : IWebhookOwnerProvider
    {
        public string Kind => "nobody";
        public Task<IReadOnlyList<WebhookOwnerDescription>> ListManageableAsync(string userId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WebhookOwnerDescription>>([]);
        public Task<bool> CanManageAsync(string key, string userId, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    [Fact]
    public void Events_come_from_an_assembly_from_one_type_or_under_a_name_of_your_own()
    {
        var builder = new AppsBuilder()
            .AddEventsFromAssemblyContaining<OrderShipped>()
            .AddEvent<Undecorated>("thing.happened", "things:read", "A thing happened.");

        var names = builder.Events.Select(e => e.Name).ToList();
        foreach (var name in new[] { "order.shipped", "invoice.paid", "build.finished", "thing.happened" })
            names.Should().Contain(name);
        builder.Events.Single(e => e.Name == "thing.happened").Should().Be(new WebhookEventRegistration("thing.happened", typeof(Undecorated), "things:read", "A thing happened."));
        Act(() => builder.AddEvent<Undecorated>(" ")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Entity_events_need_at_least_one_lifecycle_and_an_owner()
    {
        var builder = new AppsBuilder();
        Act(() => builder.AddEntityEvents<Undecorated>(0, null, _ => null)).Should().Throw<ArgumentException>();
        Act(() => builder.AddEntityEvents<Undecorated>(EntityEvents.All, null, null!)).Should().Throw<ArgumentNullException>();

        builder.AddEntityEvents<Undecorated>(EntityEvents.Created | EntityEvents.Deleted, "things:read", _ => null, "thing");

        builder.Events.Select(e => e.Name).Should().Equal(["thing.created", "thing.deleted"]);
    }

    [Fact]
    public void A_signer_is_an_extra_scheme_and_owner_providers_are_listed()
    {
        var builder = new AppsBuilder().AddOwnerProvider<NoOwners>();
        builder.OwnerProviders.Should().Equal([typeof(NoOwners)]);

        Act(() => builder.AddSigner(null!)).Should().Throw<ArgumentNullException>();
        Act(() => builder.AddSigner(new FakeSigner(HmacWebhookSigner.SchemeVersion))).Should().Throw<ArgumentException>();
        builder.AddSigner(new FakeSigner("v2a"), "whpk_test");
        builder.Signers.Should().ContainSingle().Which.PublicKey.Should().Be("whpk_test");
    }

    [Fact]
    public void The_catalog_refuses_a_type_without_a_name_the_ping_name_and_names_or_types_declared_twice()
    {
        Act(() => WebhookEventRegistration.FromAttribute(typeof(Undecorated))).Should().Throw<InvalidOperationException>();
        // No decorated bad fixture: AddEventsFromAssembly scans this assembly, so one would break every host that scans it.
        Act(() => new WebhookEventCatalog([new(WebhookEventCatalog.PingEvent, typeof(OrderShipped), null, null)])).Should().Throw<InvalidOperationException>();
        Act(() => new WebhookEventCatalog([new("a.b", typeof(OrderShipped), null, null), new("a.c", typeof(OrderShipped), null, null)]))
            .Should().Throw<InvalidOperationException>();
        Act(() => new WebhookEventCatalog([new("a.b", typeof(OrderShipped), null, null), new("a.b", typeof(InvoicePaid), null, null)]))
            .Should().Throw<InvalidOperationException>();

        var catalog = new WebhookEventCatalog([new("a.b", typeof(OrderShipped), null, null), new("a.b", typeof(OrderShipped), null, null)]);
        catalog.Events.Should().ContainSingle("the same type under the same name, registered twice, is one event");
    }
}
