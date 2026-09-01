using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Endpoints;

/// <summary>
/// Sub-queries the caller may not run are absent from the entity type, and — the part worth
/// testing — pruning one request's copy does not prune everybody's.
/// </summary>
/// <remarks>
/// <c>ModelLoader</c> is a Singleton handing every request references into one mutable graph, so
/// an in-place filter would be a permanent, process-wide, first-caller-wins truncation. A
/// single-request test passes either way, which is why the test below is order-dependent across
/// two requests on one host.
/// </remarks>
public class SubQueryPruningTests : SparkTestDriver
{
    private static readonly Guid DocTypeId = Guid.Parse("6c6c0000-1111-2222-3333-444455556666");
    private static readonly Guid ChildQueryId = Guid.Parse("6c6c1111-1111-2222-3333-444455556666");

    /// <summary>
    /// Denies on the first call for a given resource and allows on every call after, so the two
    /// requests below see opposite answers from one host.
    /// </summary>
    private sealed class DeniesFirstThenAllows : IAccessControl
    {
        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

        public Task<bool> IsAllowedAsync(string resource, CancellationToken cancellationToken = default)
        {
            lock (_seen)
                return Task.FromResult(!_seen.Add(resource));
        }
    }

    private static async Task<string[]> QueriesFromListAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/spark/types");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var type = document.RootElement.EnumerateArray().FirstOrDefault();

        return type.ValueKind == JsonValueKind.Undefined
            ? []
            : [.. type.GetProperty("queries").EnumerateArray().Select(q => q.GetString()!)];
    }

    [Fact]
    public async Task A_denied_sub_query_is_absent_and_a_granted_one_is_present()
    {
        var model = GuardedDocModel.For(DocTypeId);
        model.PersistentObject.Queries = ["childdocs"];
        model.Queries =
        [
            new SparkQuery { Id = ChildQueryId, Name = "ChildDocs", Alias = "childdocs", Source = "Database.Docs", EntityType = "GuardedDoc" },
        ];

        await using var granted = new SparkEndpointFactory<GuardedContext>(Store, [model]);
        using (var client = granted.CreateClient())
            (await QueriesFromListAsync(client)).Should().Equal("childdocs");

        // The sub-query resolves to a DIFFERENT type than the one hosting it, and only that type
        // is denied. Denying GuardedDoc itself would drop the whole entity type from the listing,
        // which is correct but is not what pruning is about — the case worth pinning is a type the
        // caller CAN see, carrying a sub-query it cannot run.
        var crossType = GuardedDocModel.For(DocTypeId);
        crossType.PersistentObject.Queries = ["childdocs"];
        crossType.Queries =
        [
            new SparkQuery { Id = ChildQueryId, Name = "ChildDocs", Alias = "childdocs", Source = "Database.Docs", EntityType = "SecretDoc" },
        ];

        await using var denied = new SparkEndpointFactory<GuardedContext>(
            Store, [crossType],
            security: SparkTestSecurity.Permissive.Without("SecretDoc"));

        using (var client = denied.CreateClient())
            (await QueriesFromListAsync(client)).Should().BeEmpty("the host type is visible, its sub-query is not");
    }

    /// <summary>
    /// S3, and the reason the pruner copies rather than filters: two requests on ONE host, the
    /// first denied and the second allowed. If the first request had mutated the singleton, the
    /// second would see an empty list — and the singleton's own array would be permanently short.
    /// </summary>
    [Fact]
    public async Task Pruning_one_request_does_not_prune_the_next_one()
    {
        var model = GuardedDocModel.For(DocTypeId);
        model.PersistentObject.Queries = ["childdocs"];
        model.Queries =
        [
            new SparkQuery { Id = ChildQueryId, Name = "ChildDocs", Alias = "childdocs", Source = "Database.Docs", EntityType = "GuardedDoc" },
        ];

        await using var factory = new SparkEndpointFactory<GuardedContext>(
            Store, [model],
            configureServices: services =>
            {
                services.RemoveAll<IAccessControl>();
                services.AddSingleton<IAccessControl, DeniesFirstThenAllows>();
            });

        using var client = factory.CreateClient();

        // First request: Query/GuardedDoc is denied, so the type is absent altogether.
        (await QueriesFromListAsync(client)).Should().BeEmpty();

        // Second request: the same resource now resolves true, and the sub-query must be back.
        (await QueriesFromListAsync(client)).Should().Equal("childdocs");

        // And the singleton itself must never have been touched. `Equal` takes params, so the
        // reason goes in BeEquivalentTo's overload rather than as a trailing string — which would
        // quietly become a second expected element.
        factory.GetService<IModelLoader>()
            .GetEntityTypes().Single().Queries.Should().BeEquivalentTo(
                ["childdocs"],
                because: "the pruner must copy per request, never filter the shared definition in place");
    }

    /// <summary>
    /// An alias resolving to no query is an authoring mistake, not a permission one. Pruning it
    /// would make a typo in <c>persistentObject.queries</c> invisible instead of loud, and buys
    /// nothing — an alias naming nothing discloses nothing.
    /// </summary>
    [Fact]
    public async Task An_unresolvable_alias_is_kept()
    {
        var model = GuardedDocModel.For(DocTypeId);
        model.PersistentObject.Queries = ["nosuchquery"];

        await using var factory = new SparkEndpointFactory<GuardedContext>(Store, [model]);
        using var client = factory.CreateClient();

        (await QueriesFromListAsync(client)).Should().Equal("nosuchquery");
    }
}
