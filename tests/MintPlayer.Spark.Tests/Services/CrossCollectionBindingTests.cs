using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Security sweep C1/H1 — a caller authorized on one entity type must not reach a document of
/// another by naming its id. The generic PO endpoints resolve the CLR type from <c>objectTypeId</c>
/// (authorized) but take the id untrusted, and RavenDB's <c>LoadAsync&lt;T&gt;</c> deserializes a
/// foreign-collection document into <c>T</c> without complaint. <see cref="CollectionGuard"/> binds
/// the id to the authorized collection at the <see cref="IDatabaseAccess"/> chokepoint.
/// <para>
/// The victim (<see cref="Secret"/>) is deliberately shaped so that when it is mis-deserialized as
/// a <see cref="GuardedDoc"/> its <c>IsVisible</c> is <c>true</c> — so it would <b>pass</b>
/// GuardedDoc's row rule. That isolates the collection guard as the only thing that can block it:
/// a green test here is the guard working, not the row rule accidentally covering.
/// </para>
/// </summary>
public class CrossCollectionBindingTests : SparkTestDriver
{
    private static readonly Guid DocTypeId = Guid.Parse("c0110c11-0000-0000-0000-c0110c110000");

    private SparkEndpointFactory<GuardedContext> _factory = null!;
    private IDatabaseAccess _dbAccess = null!;

    /// <summary>A document in its own <c>Secrets</c> collection, never registered in the model.
    /// Its <c>Name</c>/<c>IsVisible</c> line up with <see cref="GuardedDoc"/> so a mis-deserialize
    /// is field-compatible and passes the row rule; <c>ApiKey</c> is the secret an attacker wants.</summary>
    public class Secret
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsVisible { get; set; }
        public string ApiKey { get; set; } = string.Empty;
    }

    private static readonly Guid CodedTypeId = Guid.Parse("c0dedc0d-0000-0000-0000-c0dedc0d0000");

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _factory = new SparkEndpointFactory<GuardedContext>(Store,
            new[] { GuardedDocModel.For(DocTypeId), GuardedCodedModel.For(CodedTypeId) });
        _dbAccess = _factory.GetService<IDatabaseAccess>();
    }

    public override async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task<string> SeedSecretAsync()
    {
        using var session = Store.OpenAsyncSession();
        var secret = new Secret { Name = "admin", IsVisible = true, ApiKey = "sk-live-SECRET" };
        await session.StoreAsync(secret);
        await session.SaveChangesAsync();
        return secret.Id!;
    }

    [Fact]
    public async Task Get_by_a_foreign_collection_id_returns_null()
    {
        var secretId = await SeedSecretAsync();

        var result = await _dbAccess.GetPersistentObjectAsync(DocTypeId, secretId);

        result.Should().BeNull(
            "a GuardedDoc read must not return a Secrets document — even though it deserializes "
            + "cleanly and would pass GuardedDoc's row rule (IsVisible == true)");
    }

    [Fact]
    public async Task Delete_by_a_foreign_collection_id_leaves_the_document_intact()
    {
        var secretId = await SeedSecretAsync();

        await _dbAccess.DeletePersistentObjectAsync(DocTypeId, secretId);

        using var session = Store.OpenAsyncSession();
        (await session.LoadAsync<Secret>(secretId)).Should().NotBeNull(
            "a GuardedDoc delete must not erase a Secrets document");
    }

    [Fact]
    public async Task Update_by_a_foreign_collection_id_is_refused_and_the_document_is_untouched()
    {
        var secretId = await SeedSecretAsync();

        var po = new PersistentObject { Id = secretId, ObjectTypeId = DocTypeId, Name = "GuardedDoc" };
        po.AddAttribute(new PersistentObjectAttribute { Name = "Name", DataType = "string", Value = "pwned", IsValueChanged = true });
        po.AddAttribute(new PersistentObjectAttribute { Name = "IsVisible", DataType = "bool", Value = true, IsValueChanged = true });

        var act = () => _dbAccess.SavePersistentObjectAsync(po);

        await act.Should().ThrowAsync<SparkRowLevelAccessDeniedException>(
            "surgically overwriting a foreign document is the core exploit — it must be refused");

        using var session = Store.OpenAsyncSession();
        var victim = await session.LoadAsync<Secret>(secretId);
        victim.Name.Should().Be("admin");
        victim.ApiKey.Should().Be("sk-live-SECRET", "the secret must survive untouched");
    }

    // --- C2: reference attributes must not exfiltrate a foreign-collection document -----------

    public class Person
    {
        public string? Id { get; set; }
        public string FullName { get; set; } = string.Empty;
    }

    public class Container
    {
        public string? Id { get; set; }
        public Person? Author { get; set; }
    }

    [Fact]
    public async Task A_reference_naming_a_foreign_collection_id_does_not_load_that_document()
    {
        // The reference target is declared as Person; a refId naming a Secrets document must not
        // resolve — otherwise the whole Secret (ApiKey included) is copied into the caller's own
        // Container document and returned on read.
        var secretId = await SeedSecretAsync();

        var containerTypeId = Guid.NewGuid();
        var modelLoader = Substitute.For<IModelLoader>();
        var containerDef = new EntityTypeDefinition
        {
            Id = containerTypeId,
            Name = "Container",
            ClrType = typeof(Container).FullName!,
            Breadcrumb = "{Id}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Author", DataType = "Reference" },
            ],
        };
        modelLoader.GetEntityTypeByClrType(typeof(Container).FullName!).Returns(containerDef);

        var mapper = new EntityMapper(modelLoader);
        var container = new Container();
        var po = new PersistentObject { Id = "containers/1", ObjectTypeId = containerTypeId, Name = "Container" };
        po.AddAttribute(new PersistentObjectAttribute
        {
            Name = "Author", DataType = "Reference", Value = secretId, IsValueChanged = true,
        });

        using var session = Store.OpenAsyncSession();
        await mapper.PopulateObjectValuesAsync(po, container, session);

        container.Author.Should().BeNull(
            "a reference declared as Person must not resolve a Secrets document by id — that is the "
            + "exfiltration primitive");
    }

    // --- H2: natural-id create must not silently overwrite an existing document ---------------

    [Fact]
    public async Task A_natural_id_create_colliding_with_an_existing_document_runs_the_edit_gate()
    {
        // Seed a document under its natural id.
        var create = new PersistentObject { Id = null, ObjectTypeId = CodedTypeId, Name = "GuardedCoded" };
        create.AddAttribute(new PersistentObjectAttribute { Name = "Code", DataType = "string", Value = "ABC", IsValueChanged = true });
        create.AddAttribute(new PersistentObjectAttribute { Name = "Payload", DataType = "string", Value = "original", IsValueChanged = true });
        var saved = await _dbAccess.SavePersistentObjectAsync(create);
        saved.Id.Should().Be(GuardedCoded.GetId("ABC"));

        // A second "create" replaying the same Code derives the same id. Without the H2 fix this
        // takes the New path (GuardedCodedActions permits New) and silently overwrites. With it,
        // the collision is detected and re-routed through Edit — which the Actions class denies.
        var recreate = new PersistentObject { Id = null, ObjectTypeId = CodedTypeId, Name = "GuardedCoded" };
        recreate.AddAttribute(new PersistentObjectAttribute { Name = "Code", DataType = "string", Value = "ABC", IsValueChanged = true });
        recreate.AddAttribute(new PersistentObjectAttribute { Name = "Payload", DataType = "string", Value = "hijacked", IsValueChanged = true });

        var act = () => _dbAccess.SavePersistentObjectAsync(recreate);

        await act.Should().ThrowAsync<SparkRowLevelAccessDeniedException>(
            "replaying a natural key must not let New rights overwrite an existing document — the "
            + "collision is an edit, and this caller may not edit");

        using var session = Store.OpenAsyncSession();
        var doc = await session.LoadAsync<GuardedCoded>(GuardedCoded.GetId("ABC"));
        doc.Payload.Should().Be("original", "the existing document must be untouched");
    }

    [Fact]
    public async Task Same_collection_operations_still_succeed()
    {
        string docId;
        using (var session = Store.OpenAsyncSession())
        {
            var doc = new GuardedDoc { Name = "legit", IsVisible = true };
            await session.StoreAsync(doc);
            await session.SaveChangesAsync();
            docId = doc.Id!;
            await RavenIndexHelper.WaitForNonStaleAsync(Store);
        }

        var result = await _dbAccess.GetPersistentObjectAsync(DocTypeId, docId);

        result.Should().NotBeNull("the guard must not interfere with a genuine same-collection read");
        result!.Id.Should().Be(docId);
    }
}
