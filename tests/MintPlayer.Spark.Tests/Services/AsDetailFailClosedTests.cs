using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Security sweep M1 — mass assignment into an <b>undeclared</b> AsDetail child type.
/// <para>
/// The per-attribute write gate treats "no schema for this type" as allow-all (deliberate for
/// top-level internal mapping). Applied to a client-supplied AsDetail child whose CLR type was
/// never modelled, that means any writable property is settable by name — <c>Approved</c>,
/// <c>InternalSecret</c>, whatever the type has. Every legitimately-declared AsDetail child has its
/// own model file, so a missing definition means the type was never modelled and cannot be written
/// safely: the mapper now refuses it.
/// </para>
/// </summary>
public class AsDetailFailClosedTests : SparkTestDriver
{
    private static readonly Guid ParentTypeId = Guid.Parse("d0da1100-0000-0000-0000-d0da11000000");

    public class Parent
    {
        public string? Id { get; set; }
        public Child? Meta { get; set; }
    }

    /// <summary>Never registered in the model — the undeclared child type.</summary>
    public class Child
    {
        public string Label { get; set; } = string.Empty;
        public bool Approved { get; set; }
        public string InternalSecret { get; set; } = string.Empty;
    }

    [Fact]
    public async Task An_undeclared_AsDetail_child_type_is_refused_not_populated()
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
                    Id = Guid.NewGuid(), Name = "Meta", DataType = "AsDetail",
                    AsDetailType = typeof(Child).FullName,
                },
            ],
        };
        modelLoader.GetEntityTypeByClrType(typeof(Parent).FullName!).Returns(parentDef);
        // Child is deliberately NOT registered → GetEntityTypeByClrType(Child) returns null.

        var mapper = new EntityMapper(modelLoader);

        var childPo = new PersistentObject
        {
            Name = "Child",
            ObjectTypeId = Guid.NewGuid(),
            Attributes =
            [
                new PersistentObjectAttribute { Name = "Label", Value = "shown" },
                new PersistentObjectAttribute { Name = "Approved", Value = true },
                new PersistentObjectAttribute { Name = "InternalSecret", Value = "injected-by-client" },
            ],
        };
        var po = new PersistentObject
        {
            Id = "parents/1",
            ObjectTypeId = ParentTypeId,
            Name = "Parent",
            Attributes =
            [
                new PersistentObjectAttributeAsDetail
                {
                    Name = "Meta", DataType = "AsDetail", AsDetailType = typeof(Child).FullName, Object = childPo,
                },
            ],
        };

        var parent = new Parent();
        using var session = Store.OpenAsyncSession();
        var act = () => mapper.PopulateObjectValuesAsync(po, parent, session);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no registered model*",
                "an undeclared AsDetail child type cannot be gated, so it must be refused rather "
                + "than blindly populated with client-named properties");
        parent.Meta.Should().BeNull();
    }
}
