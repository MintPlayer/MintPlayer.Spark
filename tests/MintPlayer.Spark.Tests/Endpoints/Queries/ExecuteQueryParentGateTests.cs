using System.Net;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Endpoints.Queries;

/// <summary>
/// H-3 — the parent-fetch gate in <c>ExecuteQuery</c>. When a caller passes
/// <c>parentId</c>/<c>parentType</c>, the endpoint loads the parent via
/// <c>IDatabaseAccess.GetPersistentObjectAsync</c> (which applies row-level authz) and bails
/// out with 404 if the parent isn't visible — otherwise the query would run unscoped and leak
/// data the caller can't see. Uses <see cref="SparkClient.ExecuteQueryAsync(Guid,int,int,string,string,string,System.Threading.CancellationToken)"/>
/// so the test expresses "run query X with parent Y" directly.
/// </summary>
public class ExecuteQueryParentGateTests : SparkTestDriver
{
    private static readonly Guid DocTypeId = Guid.Parse("42420000-1111-2222-3333-444455556666");
    private static readonly Guid ChildrenQueryId = Guid.Parse("42421111-1111-2222-3333-444455556666");

    private SparkEndpointFactory<GuardedContext> _factory = null!;
    private SparkClient _client = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        var docType = GuardedDocModel.For(DocTypeId);
        docType.Queries =
        [
            new SparkQuery
            {
                Id = ChildrenQueryId,
                Name = "AllDocs",
                Source = "Database.Docs",
            },
        ];

        _factory = new SparkEndpointFactory<GuardedContext>(Store, new[] { docType });
        _client = new SparkClient(_factory.CreateClient(), ownsClient: true);
    }

    public override async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task SeedAsync(params GuardedDoc[] docs)
    {
        using (var session = Store.OpenAsyncSession())
        {
            foreach (var d in docs) await session.StoreAsync(d);
            await session.SaveChangesAsync();
        }
        await RavenIndexHelper.WaitForNonStaleAsync(Store);
    }

    [Fact]
    public async Task Execute_returns_404_when_parent_is_row_level_denied()
    {
        await SeedAsync(new GuardedDoc { Id = "docs/forbidden-parent", Name = "hidden", IsVisible = false });

        var ex = await Assert.ThrowsAsync<SparkClientException>(
            () => _client.ExecuteQueryAsync(ChildrenQueryId, parentId: "docs/forbidden-parent", parentType: "GuardedDoc"));
        ex.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Execute_returns_404_when_parent_does_not_exist()
    {
        var ex = await Assert.ThrowsAsync<SparkClientException>(
            () => _client.ExecuteQueryAsync(ChildrenQueryId, parentId: "docs/ghost", parentType: "GuardedDoc"));
        ex.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Execute_runs_query_when_parent_is_visible()
    {
        await SeedAsync(new GuardedDoc { Id = "docs/parent", Name = "parent", IsVisible = true });

        var result = await _client.ExecuteQueryAsync(ChildrenQueryId, parentId: "docs/parent", parentType: "GuardedDoc");

        result.Should().NotBeNull();
    }
}
