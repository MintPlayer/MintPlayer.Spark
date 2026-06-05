using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Integration tests for <see cref="ISyncActionHandler"/> covering the reflective
/// dispatch chain that the ReflectionCache migration touched: collection name →
/// CLR type resolution, "Id" PropertyInfo extraction, and the actions-pipeline
/// MethodInfo lookups (OnSaveAsync / OnDeleteAsync). Existing
/// <see cref="SyncActionHandlerBuildPersistentObjectTests"/> covers the
/// PersistentObject construction step in isolation; this file exercises the
/// full HandleSaveAsync / HandleDeleteAsync flows against a real document store
/// through <see cref="SparkEndpointFactory{TContext}"/>.
/// </summary>
public class SyncActionHandlerIntegrationTests : SparkTestDriver
{
    private static readonly Guid DocTypeId = Guid.Parse("9c0a33aa-33aa-33aa-33aa-9c0a33aa33aa");

    private SparkEndpointFactory<GuardedContext> _factory = null!;
    private ISyncActionHandler _handler = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _factory = new SparkEndpointFactory<GuardedContext>(Store, [GuardedDocModel.For(DocTypeId)]);
        _handler = _factory.GetService<ISyncActionHandler>();
    }

    public override async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task<string> SeedAsync(GuardedDoc doc)
    {
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(doc);
        await session.SaveChangesAsync();
        return doc.Id!;
    }

    // --- HandleSaveAsync: insert path ----------------------------------------

    [Fact]
    public async Task HandleSaveAsync_with_null_documentId_creates_a_new_document_and_returns_generated_Id()
    {
        var data = new Dictionary<string, object?>
        {
            ["Name"] = "from-sync",
            ["IsVisible"] = true,
        };

        var generatedId = await _handler.HandleSaveAsync("GuardedDocs", documentId: null, data, properties: null);

        generatedId.Should().NotBeNullOrEmpty(
            "the insert path should reflect the generated Id off the saved entity via cached PropertyInfo");

        using var session = Store.OpenAsyncSession();
        var loaded = await session.LoadAsync<GuardedDoc>(generatedId);
        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("from-sync");
        loaded.IsVisible.Should().BeTrue();
    }

    // --- HandleSaveAsync: update path ----------------------------------------

    [Fact]
    public async Task HandleSaveAsync_with_existing_documentId_updates_the_document()
    {
        var id = await SeedAsync(new GuardedDoc { Name = "before-sync", IsVisible = true });

        var data = new Dictionary<string, object?>
        {
            ["Name"] = "after-sync",
            ["IsVisible"] = true,
        };

        var resultId = await _handler.HandleSaveAsync("GuardedDocs", id, data, properties: ["Name"]);

        resultId.Should().Be(id);

        using var session = Store.OpenAsyncSession();
        var loaded = await session.LoadAsync<GuardedDoc>(id);
        loaded!.Name.Should().Be("after-sync");
    }

    // --- Collection name resolution ------------------------------------------

    [Fact]
    public async Task HandleSaveAsync_throws_when_collection_does_not_resolve_to_any_entity_type()
    {
        // ResolveEntityType walks every registered EntityTypeDefinition, looks up the CLR
        // type via cached resolution, and asks the document store's convention for the
        // collection name. An unknown collection should surface as an InvalidOperationException
        // rather than silently no-oping.
        var data = new Dictionary<string, object?> { ["Name"] = "x" };

        var act = () => _handler.HandleSaveAsync("DefinitelyNotARealCollectionName", null, data, null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("DefinitelyNotARealCollectionName"));
    }

    [Fact]
    public async Task HandleSaveAsync_collection_name_lookup_is_case_insensitive()
    {
        // The collection cache uses StringComparer.OrdinalIgnoreCase explicitly — pinned
        // because the new ReflectionCache primitive is case-sensitive, so we deliberately
        // kept the local cache for collection-name lookups.
        var data = new Dictionary<string, object?>
        {
            ["Name"] = "case-test",
            ["IsVisible"] = true,
        };

        // Lowercase variant must still resolve.
        var generatedId = await _handler.HandleSaveAsync("guardeddocs", documentId: null, data, properties: null);

        generatedId.Should().NotBeNullOrEmpty();
    }

    // --- HandleDeleteAsync ----------------------------------------------------

    [Fact]
    public async Task HandleDeleteAsync_removes_the_document_through_the_actions_pipeline()
    {
        var id = await SeedAsync(new GuardedDoc { Name = "to-delete", IsVisible = true });

        await _handler.HandleDeleteAsync("GuardedDocs", id);

        using var session = Store.OpenAsyncSession();
        var loaded = await session.LoadAsync<GuardedDoc>(id);
        loaded.Should().BeNull();
    }

    [Fact]
    public async Task HandleDeleteAsync_throws_when_collection_is_unknown()
    {
        var act = () => _handler.HandleDeleteAsync("DefinitelyNotARealCollectionName", "docs/anything");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // --- Repeat-call cache stability -----------------------------------------

    [Fact]
    public async Task Repeat_HandleSaveAsync_calls_for_the_same_collection_succeed_consistently()
    {
        // The collection-name cache and the ResolveType cache (now backed by ReflectionCache)
        // should make the second call see the cached resolution. Verify both calls succeed
        // and produce documents of the right shape.
        var data1 = new Dictionary<string, object?> { ["Name"] = "first", ["IsVisible"] = true };
        var data2 = new Dictionary<string, object?> { ["Name"] = "second", ["IsVisible"] = true };

        var id1 = await _handler.HandleSaveAsync("GuardedDocs", null, data1, null);
        var id2 = await _handler.HandleSaveAsync("GuardedDocs", null, data2, null);

        id1.Should().NotBeNullOrEmpty();
        id2.Should().NotBeNullOrEmpty();
        id1.Should().NotBe(id2);

        using var session = Store.OpenAsyncSession();
        var doc1 = await session.LoadAsync<GuardedDoc>(id1);
        var doc2 = await session.LoadAsync<GuardedDoc>(id2);
        doc1!.Name.Should().Be("first");
        doc2!.Name.Should().Be("second");
    }
}
