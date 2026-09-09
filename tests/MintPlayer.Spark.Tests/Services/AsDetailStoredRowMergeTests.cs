using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// A save merges onto the stored row instead of rebuilding it — the fix for a live data-loss bug.
/// </summary>
/// <remarks>
/// An AsDetail collection is rebuilt from the wire, and the schema gate refuses to write a property
/// the model marks read-only. That is correct for a client-supplied value, but on a fresh instance
/// there is no stored value left to prefer, so the field is simply gone. On the real
/// <c>EventColumnMapping</c> that meant <c>LastError</c>, <c>LastErrorAtUtc</c> and
/// <c>LastFiredAtUtc</c> — three fields the server owns — destroyed whenever a user edited an
/// unrelated field on the parent board.
/// </remarks>
public class AsDetailStoredRowMergeTests : SparkTestDriver
{
    private static readonly Guid ParentTypeId = Guid.Parse("d0da1100-0000-0000-0000-d0da11000002");

    public class Parent
    {
        public string? Id { get; set; }
        public List<Rule> Rules { get; set; } = [];
        public Settings Config { get; set; } = new();
    }

    public class Rule
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        /// <summary>Server-owned: the model marks it read-only, so a client value is refused.</summary>
        public string LastError { get; set; } = string.Empty;
    }

    public class Settings
    {
        public string Label { get; set; } = string.Empty;
        public string ComputedAtUtc { get; set; } = string.Empty;
    }

    static AsDetailStoredRowMergeTests()
        => SparkValueObjects.Register(typeof(Rule), "Id", row => ((Rule)row).Id);

    private static IModelLoader ModelLoader()
    {
        var modelLoader = Substitute.For<IModelLoader>();

        modelLoader.GetEntityTypeByClrType(typeof(Parent).FullName!).Returns(new EntityTypeDefinition
        {
            Id = ParentTypeId,
            Name = "Parent",
            ClrType = typeof(Parent).FullName!,
            Breadcrumb = "{Id}",
            Attributes =
            [
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = "Rules", DataType = "AsDetail",
                    AsDetailType = typeof(Rule).FullName, IsArray = true,
                },
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = "Config", DataType = "AsDetail",
                    AsDetailType = typeof(Settings).FullName,
                },
            ],
        });

        modelLoader.GetEntityTypeByClrType(typeof(Rule).FullName!).Returns(new EntityTypeDefinition
        {
            Id = Guid.NewGuid(), Name = "Rule", ClrType = typeof(Rule).FullName!, Breadcrumb = "{Name}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Name", DataType = "String" },
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = "LastError", DataType = "String", IsReadOnly = true,
                },
            ],
        });

        modelLoader.GetEntityTypeByClrType(typeof(Settings).FullName!).Returns(new EntityTypeDefinition
        {
            Id = Guid.NewGuid(), Name = "Settings", ClrType = typeof(Settings).FullName!, Breadcrumb = "{Label}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Label", DataType = "String" },
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = "ComputedAtUtc", DataType = "String", IsReadOnly = true,
                },
            ],
        });

        return modelLoader;
    }

    private static PersistentObject RuleRow(string? id, string name, string? lastError = null)
    {
        var attributes = new List<PersistentObjectAttribute>
        {
            new() { Name = "Name", Value = name },
        };
        if (lastError is not null)
            attributes.Add(new PersistentObjectAttribute { Name = "LastError", Value = lastError });

        return new PersistentObject
        {
            Id = id ?? string.Empty,
            Name = "Rule",
            ObjectTypeId = Guid.NewGuid(),
            Attributes = attributes,
        };
    }

    private static PersistentObject Po(params PersistentObject[] rules) => new()
    {
        Id = "parents/1",
        ObjectTypeId = ParentTypeId,
        Name = "Parent",
        Attributes =
        [
            new PersistentObjectAttributeAsDetail
            {
                Name = "Rules", DataType = "AsDetail", IsArray = true,
                AsDetailType = typeof(Rule).FullName, Objects = [.. rules],
            },
        ],
    };

    /// <summary>The bug, stated as a test.</summary>
    [Fact]
    public async Task Editing_one_row_preserves_the_server_owned_field_of_that_row()
    {
        var parent = new Parent
        {
            Rules = [new Rule { Id = "a", Name = "old", LastError = "boom" }],
        };
        using var session = Store.OpenAsyncSession();

        await new EntityMapper(ModelLoader())
            .PopulateObjectValuesAsync(Po(RuleRow("a", "renamed")), parent, session);

        parent.Rules.Should().HaveCount(1);
        parent.Rules[0].Name.Should().Be("renamed");
        parent.Rules[0].LastError.Should().Be("boom",
            "the row was populated onto its stored instance, so a field the payload may not write "
            + "was never lost to begin with");
    }

    /// <summary>
    /// And the row really is the same object, not a copy that happened to carry the value — which
    /// is what makes the guarantee hold for every read-only field rather than the ones a test names.
    /// </summary>
    [Fact]
    public async Task A_matched_row_keeps_its_stored_instance()
    {
        var stored = new Rule { Id = "a", Name = "old", LastError = "boom" };
        var parent = new Parent { Rules = [stored] };
        using var session = Store.OpenAsyncSession();

        await new EntityMapper(ModelLoader())
            .PopulateObjectValuesAsync(Po(RuleRow("a", "renamed")), parent, session);

        parent.Rules.Should().ContainSingle().Which.Should().BeSameAs(stored);
    }

    [Fact]
    public async Task A_row_the_client_sends_without_a_key_is_new()
    {
        var parent = new Parent { Rules = [new Rule { Id = "a", Name = "kept", LastError = "boom" }] };
        using var session = Store.OpenAsyncSession();

        await new EntityMapper(ModelLoader())
            .PopulateObjectValuesAsync(Po(RuleRow("a", "kept"), RuleRow(null, "added")), parent, session);

        parent.Rules.Should().HaveCount(2);
        parent.Rules[1].Name.Should().Be("added");
        parent.Rules[1].LastError.Should().BeEmpty("a genuinely new row has no server-owned history");
    }

    /// <summary>
    /// Two incoming rows claiming one stored key must not both alias that instance, or the same
    /// object lands in the collection twice and edits to one appear on the other.
    /// </summary>
    [Fact]
    public async Task A_stored_row_is_claimed_at_most_once()
    {
        var parent = new Parent { Rules = [new Rule { Id = "a", Name = "old" }] };
        using var session = Store.OpenAsyncSession();

        await new EntityMapper(ModelLoader())
            .PopulateObjectValuesAsync(Po(RuleRow("a", "first"), RuleRow("a", "second")), parent, session);

        parent.Rules.Should().HaveCount(2);
        parent.Rules[0].Should().NotBeSameAs(parent.Rules[1]);
        parent.Rules[0].Name.Should().Be("first");
        parent.Rules[1].Name.Should().Be("second");
    }

    [Fact]
    public async Task A_removed_row_is_gone()
    {
        var parent = new Parent
        {
            Rules = [new Rule { Id = "a", Name = "a" }, new Rule { Id = "b", Name = "b" }],
        };
        using var session = Store.OpenAsyncSession();

        await new EntityMapper(ModelLoader())
            .PopulateObjectValuesAsync(Po(RuleRow("a", "a")), parent, session);

        parent.Rules.Should().ContainSingle().Which.Id.Should().Be("a");
    }

    /// <summary>
    /// A single nested object is matched by its property name, so it needs no key and merges
    /// unconditionally — the same read-only wipe applied there too.
    /// </summary>
    [Fact]
    public async Task A_single_nested_object_also_merges_onto_the_stored_instance()
    {
        var parent = new Parent { Config = new Settings { Label = "old", ComputedAtUtc = "2026-01-01" } };
        using var session = Store.OpenAsyncSession();

        var po = new PersistentObject
        {
            Id = "parents/1",
            ObjectTypeId = ParentTypeId,
            Name = "Parent",
            Attributes =
            [
                new PersistentObjectAttributeAsDetail
                {
                    Name = "Config", DataType = "AsDetail", AsDetailType = typeof(Settings).FullName,
                    Object = new PersistentObject
                    {
                        Name = "Settings",
                        ObjectTypeId = Guid.NewGuid(),
                        Attributes = [new PersistentObjectAttribute { Name = "Label", Value = "renamed" }],
                    },
                },
            ],
        };

        await new EntityMapper(ModelLoader()).PopulateObjectValuesAsync(po, parent, session);

        parent.Config.Label.Should().Be("renamed");
        parent.Config.ComputedAtUtc.Should().Be("2026-01-01");
    }
}
