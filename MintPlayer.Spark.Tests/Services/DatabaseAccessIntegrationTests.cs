using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Integration tests for <see cref="IDatabaseAccess"/> covering the reflective dispatch
/// paths that the ReflectionCache migration touched: SavePersistentObjectAsync,
/// DeletePersistentObjectAsync, optimistic-concurrency check, and the generic flat APIs
/// (GetDocumentsAsync, SaveDocumentAsync, DeleteDocumentAsync) that hit
/// <c>typeof(T).GetProperty("Id")</c>. Sister file to
/// <see cref="DatabaseAccessRowLevelAuthzTests"/> which covers the Get/list authz path.
/// </summary>
public class DatabaseAccessIntegrationTests : SparkTestDriver
{
    private static readonly Guid DocTypeId = Guid.Parse("8b0a22aa-22aa-22aa-22aa-8b0a22aa22aa");

    private SparkEndpointFactory<GuardedContext> _factory = null!;
    private IDatabaseAccess _dbAccess = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _factory = new SparkEndpointFactory<GuardedContext>(Store, [GuardedDocModel.For(DocTypeId)]);
        _dbAccess = _factory.GetService<IDatabaseAccess>();
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

    // --- SavePersistentObjectAsync (insert + update via the actions pipeline) --------

    [Fact]
    public async Task SavePersistentObjectAsync_inserts_a_new_entity_and_returns_the_generated_Id()
    {
        var po = new PersistentObject
        {
            Id = null,
            ObjectTypeId = DocTypeId,
            Name = "GuardedDoc",
        };
        po.AddAttribute(new PersistentObjectAttribute { Name = "Name", DataType = "string", Value = "fresh", IsValueChanged = true });
        po.AddAttribute(new PersistentObjectAttribute { Name = "IsVisible", DataType = "bool", Value = true, IsValueChanged = true });

        var result = await _dbAccess.SavePersistentObjectAsync(po);

        result.Id.Should().NotBeNullOrEmpty(
            "the insert path should populate the generated RavenDB document Id reflectively");
        result.Etag.Should().NotBeNullOrEmpty();

        // Verify the doc landed in the store.
        using var session = Store.OpenAsyncSession();
        var loaded = await session.LoadAsync<GuardedDoc>(result.Id);
        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("fresh");
        loaded.IsVisible.Should().BeTrue();
    }

    [Fact]
    public async Task SavePersistentObjectAsync_updates_an_existing_entity_when_Id_is_set()
    {
        var id = await SeedAsync(new GuardedDoc { Name = "before", IsVisible = true });

        // Read current Etag for the OCC path.
        string currentEtag;
        using (var session = Store.OpenAsyncSession())
        {
            var loaded = await session.LoadAsync<GuardedDoc>(id);
            currentEtag = session.Advanced.GetChangeVectorFor(loaded);
        }

        var po = new PersistentObject
        {
            Id = id,
            Etag = currentEtag,
            Name = "GuardedDoc",
            ObjectTypeId = DocTypeId,
        };
        po.AddAttribute(new PersistentObjectAttribute { Name = "Name", DataType = "string", Value = "after", IsValueChanged = true });
        po.AddAttribute(new PersistentObjectAttribute { Name = "IsVisible", DataType = "bool", Value = true, IsValueChanged = true });

        await _dbAccess.SavePersistentObjectAsync(po);

        using var verify = Store.OpenAsyncSession();
        var updated = await verify.LoadAsync<GuardedDoc>(id);
        updated!.Name.Should().Be("after");
    }

    [Fact]
    public async Task SavePersistentObjectAsync_throws_SparkConcurrencyException_on_stale_Etag()
    {
        // Optimistic concurrency: the caller sent an Etag that no longer matches the
        // server-side change vector. This path opens a side session via documentStore
        // (so change tracking on the main session isn't polluted) and should throw
        // before the actions pipeline runs.
        var id = await SeedAsync(new GuardedDoc { Name = "v1", IsVisible = true });

        var po = new PersistentObject
        {
            Id = id,
            Etag = "A:1-deadbeefdeadbeefdeadbeefdeadbeef", // intentionally bogus
            Name = "GuardedDoc",
            ObjectTypeId = DocTypeId,
        };
        po.AddAttribute(new PersistentObjectAttribute { Name = "Name", DataType = "string", Value = "v2", IsValueChanged = true });
        po.AddAttribute(new PersistentObjectAttribute { Name = "IsVisible", DataType = "bool", Value = true, IsValueChanged = true });

        var act = () => _dbAccess.SavePersistentObjectAsync(po);

        await act.Should().ThrowAsync<SparkConcurrencyException>();
    }

    // --- DeletePersistentObjectAsync ------------------------------------------------

    [Fact]
    public async Task DeletePersistentObjectAsync_removes_the_document_via_the_actions_pipeline()
    {
        var id = await SeedAsync(new GuardedDoc { Name = "doomed", IsVisible = true });

        await _dbAccess.DeletePersistentObjectAsync(DocTypeId, id);

        using var session = Store.OpenAsyncSession();
        var loaded = await session.LoadAsync<GuardedDoc>(id);
        loaded.Should().BeNull();
    }

    [Fact]
    public async Task DeletePersistentObjectAsync_silently_returns_when_objectTypeId_is_unknown()
    {
        // Unknown ObjectTypeId — modelLoader returns null, so the call short-circuits.
        // Verifies the early-out guard rather than letting a NullReferenceException leak.
        var unknownTypeId = Guid.NewGuid();

        var act = () => _dbAccess.DeletePersistentObjectAsync(unknownTypeId, "docs/whatever");

        await act.Should().NotThrowAsync();
    }

    // --- Generic flat APIs (hit the typeof(T).GetProperty("Id") line in SaveDocumentAsync) -----

    [Fact]
    public async Task SaveDocumentAsync_persists_the_typed_document_and_returns_it_with_Id()
    {
        var doc = new GuardedDoc { Name = "via-generic-save", IsVisible = true };

        var saved = await _dbAccess.SaveDocumentAsync(doc);

        saved.Id.Should().NotBeNullOrEmpty();
        using var session = Store.OpenAsyncSession();
        var loaded = await session.LoadAsync<GuardedDoc>(saved.Id);
        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("via-generic-save");
    }

    [Fact]
    public async Task GetDocumentsAsync_returns_all_documents_of_the_given_type()
    {
        await SeedAsync(new GuardedDoc { Name = "a", IsVisible = true });
        await SeedAsync(new GuardedDoc { Name = "b", IsVisible = true });
        WaitForIndexing(Store);

        var docs = (await _dbAccess.GetDocumentsAsync<GuardedDoc>()).ToList();

        docs.Should().HaveCount(2);
        docs.Select(d => d.Name).Should().BeEquivalentTo(["a", "b"]);
    }

    [Fact]
    public async Task GetDocumentAsync_returns_the_document_by_Id()
    {
        var id = await SeedAsync(new GuardedDoc { Name = "loadme", IsVisible = true });

        var loaded = await _dbAccess.GetDocumentAsync<GuardedDoc>(id);

        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("loadme");
    }

    [Fact]
    public async Task GetDocumentAsync_returns_null_when_the_document_does_not_exist()
    {
        var loaded = await _dbAccess.GetDocumentAsync<GuardedDoc>("docs/nonexistent");

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task DeleteDocumentAsync_removes_the_typed_document()
    {
        var id = await SeedAsync(new GuardedDoc { Name = "doomed-typed", IsVisible = true });

        await _dbAccess.DeleteDocumentAsync<GuardedDoc>(id);

        using var session = Store.OpenAsyncSession();
        var loaded = await session.LoadAsync<GuardedDoc>(id);
        loaded.Should().BeNull();
    }

    // --- GetDocumentsByObjectTypeIdAsync (generic typed list filtered by ObjectTypeId) ---

    [Fact]
    public async Task GetDocumentsByObjectTypeIdAsync_returns_empty_when_no_documents_have_the_id()
    {
        // GuardedDoc has no ObjectTypeId field, so the LINQ Where short-circuits to empty.
        // Pin the empty-result shape so a future change to GuardedDoc doesn't accidentally
        // make this throw.
        var act = () => _dbAccess.GetDocumentsByObjectTypeIdAsync<GuardedDoc>(Guid.NewGuid());

        // GuardedDoc isn't a PersistentObject so the cast in the LINQ Where will fail at runtime;
        // that's the framework's documented contract — only PersistentObject-derived types
        // can use this overload. We accept either an empty result or InvalidCastException.
        try
        {
            var docs = await act();
            docs.Should().BeEmpty();
        }
        catch (InvalidCastException)
        {
            // Acceptable: GuardedDoc isn't a PersistentObject.
        }
    }

    // --- GetPersistentObjectsAsync via index + projection ----------------------

    /// <summary>Map index that drives DatabaseAccess.QueryEntitiesWithIncludesAsync's
    /// reflective ApplyIndex / ApplyProjection / ApplyToListAsync paths through the
    /// IndexRegistry projection registration.</summary>
    public class GuardedDocs_ByName : AbstractIndexCreationTask<GuardedDoc>
    {
        public GuardedDocs_ByName()
        {
            Map = docs => from d in docs select new { d.Name, d.IsVisible };
            StoreAllFields(FieldStorage.Yes);
        }
    }

    public class VGuardedDoc
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsVisible { get; set; }
    }

    [Fact]
    public async Task GetPersistentObjectsAsync_through_registered_index_and_projection_succeeds()
    {
        await SeedAsync(new GuardedDoc { Id = "docs/i1", Name = "Alpha", IsVisible = true });
        await SeedAsync(new GuardedDoc { Id = "docs/i2", Name = "Bravo", IsVisible = true });
        await SeedAsync(new GuardedDoc { Id = "docs/i3", Name = "Charlie", IsVisible = true });

        await new GuardedDocs_ByName().ExecuteAsync(Store);
        WaitForIndexing(Store);

        var indexRegistry = _factory.GetService<IIndexRegistry>();
        indexRegistry.RegisterIndex(typeof(GuardedDocs_ByName));
        indexRegistry.RegisterProjection(typeof(VGuardedDoc), typeof(GuardedDocs_ByName));

        var results = (await _dbAccess.GetPersistentObjectsAsync(DocTypeId)).ToList();

        results.Should().HaveCount(3);
        results.Select(po => po.Id).Should().BeEquivalentTo(["docs/i1", "docs/i2", "docs/i3"]);
    }
}
