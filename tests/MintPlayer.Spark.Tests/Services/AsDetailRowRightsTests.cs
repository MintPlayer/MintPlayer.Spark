using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// N6 — the row-level <c>New</c> / <c>Edit</c> / <c>Delete</c> rights of an AsDetail element type,
/// enforced where the stored collection and the incoming one are both in hand.
/// </summary>
/// <remarks>
/// Until this existed the three rights were fiction on the write path. They could be granted on an
/// embedded type — HR's <c>security.json</c> grants <c>QueryReadEditNewDelete/CarreerJob</c> — and no
/// code read them, while the save replaced the collection wholesale. Anyone who could edit the parent
/// could add, alter and remove rows of a type they had no rights to.
/// </remarks>
public class AsDetailRowRightsTests : SparkTestDriver
{
    private static readonly Guid ParentTypeId = Guid.Parse("d0da1100-0000-0000-0000-d0da11000001");

    public class Parent
    {
        public string? Id { get; set; }
        public List<PhoneNumber> Phones { get; set; } = [];
    }

    public class PhoneNumber
    {
        public string Id { get; set; } = string.Empty;
        public string Number { get; set; } = string.Empty;
    }

    static AsDetailRowRightsTests()
        => SparkValueObjects.Register(typeof(PhoneNumber), "Id", row => ((PhoneNumber)row).Id);

    private static IModelLoader ModelLoader(Type elementType, string elementName)
    {
        var modelLoader = Substitute.For<IModelLoader>();

        var parentDef = new EntityTypeDefinition
        {
            Id = ParentTypeId,
            Name = "Parent",
            ClrType = typeof(Parent).FullName!,
            Breadcrumb = "{Id}",
            Attributes =
            [
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = "Phones", DataType = "AsDetail",
                    AsDetailType = elementType.FullName, IsArray = true,
                },
            ],
        };
        var elementDef = new EntityTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = elementName,
            ClrType = elementType.FullName!,
            Breadcrumb = "{Number}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Number", DataType = "String" },
            ],
        };

        modelLoader.GetEntityTypeByClrType(typeof(Parent).FullName!).Returns(parentDef);
        modelLoader.GetEntityTypeByClrType(elementType.FullName!).Returns(elementDef);
        return modelLoader;
    }

    /// <summary>Grants everything except the verbs named in <paramref name="denied"/>.</summary>
    private static IPermissionService Permissions(params string[] denied)
    {
        var permissions = Substitute.For<IPermissionService>();
        permissions.IsAllowedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(!denied.Contains((string)call[0])));
        permissions.EnsureAuthorizedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => denied.Contains((string)call[0])
                ? Task.FromException(new UnauthorizedAccessException((string)call[0]))
                : Task.CompletedTask);
        return permissions;
    }

    /// <summary>
    /// The row key travels as the child PO's <c>Id</c>, the way a PersistentObject carries identity
    /// everywhere else — not as a declared attribute. <c>TryWriteId</c> is what puts it back on the
    /// entity, and it runs before the schema gate, so the key round-trips without the model having
    /// to declare it as an editable field.
    /// </summary>
    private static PersistentObject Row(string id, string number) => new()
    {
        Id = id,
        Name = "PhoneNumber",
        ObjectTypeId = Guid.NewGuid(),
        Attributes =
        [
            new PersistentObjectAttribute { Name = "Number", Value = number },
        ],
    };

    private static PersistentObject Po(params PersistentObject[] rows) => new()
    {
        Id = "parents/1",
        ObjectTypeId = ParentTypeId,
        Name = "Parent",
        Attributes =
        [
            new PersistentObjectAttributeAsDetail
            {
                Name = "Phones", DataType = "AsDetail", IsArray = true,
                AsDetailType = typeof(PhoneNumber).FullName, Objects = [.. rows],
            },
        ],
    };

    private static EntityMapper Mapper(IPermissionService permissions, Type? elementType = null)
        => new(ModelLoader(elementType ?? typeof(PhoneNumber), "PhoneNumber"),
               permissionService: permissions);

    [Fact]
    public async Task Adding_a_row_without_New_is_refused()
    {
        var parent = new Parent { Phones = [new PhoneNumber { Id = "a", Number = "111" }] };
        using var session = Store.OpenAsyncSession();

        var act = () => Mapper(Permissions("New"))
            .PopulateObjectValuesAsync(Po(Row("a", "111"), Row("b", "222")), parent, session);

        await act.Should().ThrowAsync<UnauthorizedAccessException>(
            "a key absent from the stored set is a creation of the row type, whatever right the "
            + "caller holds on the parent");
    }

    [Fact]
    public async Task Adding_a_row_with_New_is_allowed()
    {
        var parent = new Parent { Phones = [new PhoneNumber { Id = "a", Number = "111" }] };
        using var session = Store.OpenAsyncSession();

        await Mapper(Permissions())
            .PopulateObjectValuesAsync(Po(Row("a", "111"), Row("b", "222")), parent, session);

        parent.Phones.Should().HaveCount(2);
    }

    [Fact]
    public async Task Removing_a_row_without_Delete_is_refused()
    {
        var parent = new Parent
        {
            Phones = [new PhoneNumber { Id = "a", Number = "111" }, new PhoneNumber { Id = "b", Number = "222" }],
        };
        using var session = Store.OpenAsyncSession();

        var act = () => Mapper(Permissions("Delete"))
            .PopulateObjectValuesAsync(Po(Row("a", "111")), parent, session);

        await act.Should().ThrowAsync<UnauthorizedAccessException>(
            "a stored key absent from the incoming collection is a deletion of the row type");
    }

    [Fact]
    public async Task Removing_a_row_with_Delete_is_allowed()
    {
        var parent = new Parent
        {
            Phones = [new PhoneNumber { Id = "a", Number = "111" }, new PhoneNumber { Id = "b", Number = "222" }],
        };
        using var session = Store.OpenAsyncSession();

        await Mapper(Permissions()).PopulateObjectValuesAsync(Po(Row("a", "111")), parent, session);

        parent.Phones.Should().ContainSingle().Which.Id.Should().Be("a");
    }

    /// <summary>
    /// The one verb that restores rather than refuses, following
    /// <c>ShieldProtectedAttributesAsync</c>: a save that also touches something the caller may
    /// change should still succeed, with the parts they may not change left as they were.
    /// </summary>
    [Fact]
    public async Task Editing_a_row_without_Edit_restores_the_stored_content()
    {
        var parent = new Parent { Phones = [new PhoneNumber { Id = "a", Number = "111" }] };
        using var session = Store.OpenAsyncSession();

        await Mapper(Permissions("Edit"))
            .PopulateObjectValuesAsync(Po(Row("a", "666")), parent, session);

        parent.Phones.Should().ContainSingle().Which.Number.Should().Be("111",
            "the row is restored from storage rather than the save being refused");
    }

    [Fact]
    public async Task Editing_a_row_with_Edit_is_applied()
    {
        var parent = new Parent { Phones = [new PhoneNumber { Id = "a", Number = "111" }] };
        using var session = Store.OpenAsyncSession();

        await Mapper(Permissions()).PopulateObjectValuesAsync(Po(Row("a", "666")), parent, session);

        parent.Phones.Should().ContainSingle().Which.Number.Should().Be("666");
    }

    /// <summary>
    /// A create has nothing stored, so every incoming row is new and no key is needed to say so.
    /// </summary>
    [Fact]
    public async Task An_empty_stored_collection_needs_only_New()
    {
        var parent = new Parent();
        using var session = Store.OpenAsyncSession();

        var act = () => Mapper(Permissions("New")).PopulateObjectValuesAsync(Po(Row("a", "111")), parent, session);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    /// <summary>
    /// An unchanged collection asks for nothing. Enforcement that fired on every save would make
    /// <c>Edit/Parent</c> imply <c>Edit</c> on every embedded type it contains.
    /// </summary>
    [Fact]
    public async Task An_unchanged_collection_needs_no_row_right()
    {
        var parent = new Parent { Phones = [new PhoneNumber { Id = "a", Number = "111" }] };
        using var session = Store.OpenAsyncSession();

        await Mapper(Permissions("New", "Edit", "Delete"))
            .PopulateObjectValuesAsync(Po(Row("a", "111")), parent, session);

        parent.Phones.Should().ContainSingle().Which.Number.Should().Be("111");
    }

    /// <summary>
    /// A restore is announced, because a save that silently discards edits and reports success is
    /// indistinguishable from a bug.
    /// </summary>
    [Fact]
    public async Task A_restored_row_notifies_the_client()
    {
        var client = Substitute.For<MintPlayer.Spark.Abstractions.ClientOperations.IClientAccessor>();
        var parent = new Parent { Phones = [new PhoneNumber { Id = "a", Number = "111" }] };
        using var session = Store.OpenAsyncSession();

        var mapper = new EntityMapper(
            ModelLoader(typeof(PhoneNumber), "PhoneNumber"),
            permissionService: Permissions("Edit"),
            clientAccessor: client);

        await mapper.PopulateObjectValuesAsync(Po(Row("a", "666")), parent, session);

        client.Received(1).Notify(
            Arg.Is<string>(m => m.Contains("PhoneNumber")),
            MintPlayer.Spark.Abstractions.ClientOperations.NotificationKind.Warning,
            Arg.Any<TimeSpan?>());
    }

    /// <summary>
    /// ⚠️ There is deliberately <b>no</b> per-save check for a keyless stored row, and two tests
    /// asserting one were deleted rather than adapted.
    /// </summary>
    /// <remarks>
    /// The key is minted by a field initializer that runs during deserialization, so a row stored
    /// without one comes back carrying a fresh guid — measured in
    /// <see cref="NestedRowIdentityTests"/>. A stored key is therefore never empty and any
    /// <c>IsNullOrEmpty</c> guard is unreachable code. Detection has to happen where the raw JSON is
    /// still visible, which is the database: the backfill migration plus the startup gate.
    /// <para>
    /// What that leaves here is a legacy row looking like an unmatched row, i.e. a create plus a
    /// delete — which this test pins, so the behaviour is documented rather than surprising.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_legacy_row_reads_as_a_replacement_which_is_why_the_migration_gates_startup()
    {
        // A stored row whose key the client never saw: it arrives unmatched.
        var parent = new Parent { Phones = [new PhoneNumber { Id = "stored-key", Number = "111" }] };
        using var session = Store.OpenAsyncSession();

        var act = () => Mapper(Permissions("Delete"))
            .PopulateObjectValuesAsync(Po(Row("different-key", "111")), parent, session);

        await act.Should().ThrowAsync<UnauthorizedAccessException>(
            "an unmatched stored key is a deletion as far as the save can tell, which is exactly why "
            + "the migration must run before the key ships");
    }
}
