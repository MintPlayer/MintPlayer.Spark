using System.Net;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Authorization;

/// <summary>
/// `Query` without `Read` — the mechanism #310 exists to make visible, end to end against a real
/// store. This is DemoApp's Stock grid in miniature: rows list, no row is a link.
/// </summary>
/// <remarks>
/// Spike S7 planned to check this in a browser. A driver test is the better instrument, and not
/// only because it runs in CI: a browser can show that the anchor is absent, but it cannot show
/// that <c>Read</c> is <em>enforced</em> — a client that ignored <c>canRead</c> and built the URL
/// itself would look identical. Both halves are asserted here.
/// <para>
/// The client half is one <c>@if (first &amp;&amp; canRead())</c> in each of the two grid
/// templates, covered by the ng-spark specs. What matters on this side is that
/// <c>/spark/permissions</c> reports the pair honestly and that the detail endpoint agrees with
/// it.
/// </para>
/// </remarks>
public class QueryWithoutReadTests : SparkTestDriver
{
    private static readonly Guid DocTypeId = Guid.Parse("7d7d0000-1111-2222-3333-444455556666");
    private static readonly Guid AllDocsQueryId = Guid.Parse("7d7d1111-1111-2222-3333-444455556666");

    private static EntityTypeFile Model()
    {
        var model = GuardedDocModel.For(DocTypeId);
        model.Queries =
        [
            new SparkQuery
            {
                Id = AllDocsQueryId,
                Name = "AllDocs",
                Alias = "alldocs",
                Source = "Database.Docs",
                EntityType = "GuardedDoc",
            },
        ];
        return model;
    }

    /// <summary>
    /// Grants exactly what the resource string says — no bundle, so the two rights genuinely come
    /// apart. <c>QueryRead/GuardedDoc</c> would hide the whole point.
    /// </summary>
    private async Task<(SparkEndpointFactory<GuardedContext> Factory, SparkClient Client)> HostAsync(
        params string[] rights)
    {
        var factory = new SparkEndpointFactory<GuardedContext>(
            Store, [Model()], security: SparkTestSecurity.Empty.Granting(rights));

        await SeedAsync(async session =>
        {
            await session.StoreAsync(new GuardedDoc { Id = "docs/1", Name = "SKU-1", IsVisible = true });
            await session.StoreAsync(new GuardedDoc { Id = "docs/2", Name = "SKU-2", IsVisible = true });
        });

        return (factory, new SparkClient(factory.CreateClient(), ownsClient: true));
    }

    [Fact]
    public async Task Query_without_Read_lists_the_rows_but_reports_canRead_false()
    {
        var (factory, client) = await HostAsync("Query/GuardedDoc");
        await using var _ = factory;
        using var __ = client;

        var permissions = await client.GetPermissionsAsync(DocTypeId.ToString());

        permissions!.CanQuery.Should().BeTrue("the grid must render");
        permissions.CanRead.Should().BeFalse("and no row may be a link");

        var result = await client.ExecuteQueryAsync(AllDocsQueryId);
        result.Items.Should().HaveCount(2, "withholding Read does not hide the rows");
    }

    /// <summary>
    /// The half a browser cannot show. Withholding <c>Read</c> is enforcement, not presentation:
    /// a client that ignored <c>canRead</c> and navigated to the detail URL anyway gets nothing.
    /// </summary>
    [Fact]
    public async Task Query_without_Read_refuses_the_by_id_load()
    {
        var (factory, client) = await HostAsync("Query/GuardedDoc");
        await using var _ = factory;
        using var __ = client;

        // null, not a throw: the client maps 404 to null here on purpose, because the endpoint
        // conflates "denied" with "no such row" (M-3) and a caller cannot be told which.
        (await client.GetPersistentObjectAsync(DocTypeId, "docs/1")).Should().BeNull();
    }

    /// <summary>
    /// The control. Same fixture, same rows, one more grant — so the previous two tests are
    /// pinning the RIGHT and not some unrelated reason the link or the load was missing.
    /// </summary>
    [Fact]
    public async Task Adding_Read_makes_the_row_readable_and_flips_canRead()
    {
        var (factory, client) = await HostAsync("Query/GuardedDoc", "Read/GuardedDoc");
        await using var _ = factory;
        using var __ = client;

        var permissions = await client.GetPermissionsAsync(DocTypeId.ToString());
        permissions!.CanQuery.Should().BeTrue();
        permissions.CanRead.Should().BeTrue();

        var doc = await client.GetPersistentObjectAsync(DocTypeId, "docs/1");
        doc.Should().NotBeNull();
    }

    /// <summary>
    /// The reverse pair does NOT come apart (#324): <b>Read implies Query</b>, expanded at grant
    /// time. A caller who may open every row individually may list them — a listing discloses
    /// nothing that reading the rows one by one would not — and Vidyano's rights editor encodes
    /// the same rule by only ever offering <c>Query</c>, <c>QueryRead</c>, … as a bundle, never a
    /// bare Read. So a bare <c>Read/X</c> grant is a complete grid grant, and every surface that
    /// asks for <c>Query</c> — the type catalogue included — answers yes.
    /// </summary>
    [Fact]
    public async Task Read_implies_Query_so_a_bare_Read_grant_lists_and_reads()
    {
        var (factory, client) = await HostAsync("Read/GuardedDoc");
        await using var _ = factory;
        using var __ = client;

        var permissions = await client.GetPermissionsAsync(DocTypeId.ToString());
        permissions!.CanRead.Should().BeTrue();
        permissions.CanQuery.Should().BeTrue("Read implies Query");

        (await client.GetPersistentObjectAsync(DocTypeId, "docs/1")).Should().NotBeNull();
        (await client.ExecuteQueryAsync(AllDocsQueryId)).Items.Should().HaveCount(2);

        (await client.ListEntityTypesAsync()).Should().ContainSingle(
            "the catalogue is Query-scoped, and Read reaches it through the implication — without "
            + "which a Read-only type's detail page has no metadata to render from");
    }

    /// <summary>
    /// The implication widens grants only. A denied <c>Read</c> says nothing about <c>Query</c> —
    /// otherwise "list but no click-through" could not be expressed by denial, only by omission.
    /// </summary>
    [Fact]
    public async Task Denying_Read_does_not_deny_Query()
    {
        var factory = new SparkEndpointFactory<GuardedContext>(
            Store, [Model()],
            security: SparkTestSecurity.Empty
                .Granting("Query/GuardedDoc")
                .Denying("Read/GuardedDoc"));
        await using var _ = factory;
        using var client = new SparkClient(factory.CreateClient(), ownsClient: true);

        var permissions = await client.GetPermissionsAsync(DocTypeId.ToString());
        permissions!.CanQuery.Should().BeTrue();
        permissions.CanRead.Should().BeFalse();
    }

    /// <summary>
    /// And the type must still be reachable at all. A grid that is never listed cannot demonstrate
    /// anything, so this pins the thing the other tests quietly depend on.
    /// </summary>
    [Fact]
    public async Task A_Query_only_type_is_still_listed_in_the_catalogue()
    {
        var (factory, client) = await HostAsync("Query/GuardedDoc");
        await using var _ = factory;
        using var __ = client;

        var types = await client.ListEntityTypesAsync();

        types.Should().ContainSingle().Which.Name.Should().Be("GuardedDoc");
    }

    /// <summary>
    /// The floor: a right that is neither Query nor Read leaks nothing. Both metadata surfaces
    /// stay closed, so the implication above widens exactly one edge and no more.
    /// </summary>
    [Fact]
    public async Task A_type_with_neither_right_is_hidden_from_both_metadata_surfaces()
    {
        var (factory, client) = await HostAsync("Edit/GuardedDoc");
        await using var _ = factory;
        using var __ = client;

        (await client.ListEntityTypesAsync()).Should().BeEmpty();

        using var http = factory.CreateClient();
        var response = await http.GetAsync($"/spark/types/{DocTypeId}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "existence isn't leaked");
    }
}
