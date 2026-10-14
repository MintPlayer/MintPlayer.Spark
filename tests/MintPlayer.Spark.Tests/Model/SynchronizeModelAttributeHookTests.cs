using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Configuration;
using MintPlayer.Spark.Services;
using NSubstitute;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Model;

/// <summary>
/// <c>SparkOptions.ModelAttribute</c> in the synchronizer (<c>docs/default_index_PRD.md</c>, D9; PRD §7, SP5): it decides
/// what a property becomes when its attribute is first created — <c>Exclude</c> creates none, <c>Hidden</c> creates a
/// <c>showedOn: None</c>, read-only one — and nothing else. It never changes or deletes an existing attribute (#253),
/// <c>[IgnoreProperty]</c> excludes whatever it says, and a new attribute takes an order nobody holds.
/// </summary>
public sealed class SynchronizeModelAttributeHookTests : IDisposable
{
    private readonly string contentRoot = Path.Combine(Path.GetTempPath(), "spark-hook-" + Guid.NewGuid().ToString("N"));
    private readonly IHostEnvironment hostEnvironment = Substitute.For<IHostEnvironment>();
    private readonly IIndexCatalog indexCatalog = Substitute.For<IIndexCatalog>();

    public SynchronizeModelAttributeHookTests()
    {
        Directory.CreateDirectory(contentRoot);
        hostEnvironment.ContentRootPath.Returns(contentRoot);
        indexCatalog.GetAllEntries().Returns([]);
        indexCatalog.GetDefaultFor(Arg.Any<Type>()).Returns((IndexCatalogEntry?)null);
        indexCatalog.GetByIndexName(Arg.Any<string>()).Returns((IndexCatalogEntry?)null);
    }

    public void Dispose()
    {
        try { Directory.Delete(contentRoot, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string ModelFile(string type) => Path.Combine(contentRoot, "App_Data", "Model", type + ".json");

    private void Synchronize(Type context, Func<Type, PropertyInfo, ModelAttributeSeed>? hook = null, IReadOnlyList<SparkLibrary>? libraries = null)
        => new ModelSynchronizer(hostEnvironment, indexCatalog)
        {
            Libraries = libraries ?? [],
            ModelAttribute = hook ?? ModelAttributeConventions.AuditMembersHidden,
        }.SynchronizeModels(context);

    private EntityAttributeDefinition[] Attributes(string type)
        => JsonSerializer.Deserialize<EntityTypeFile>(File.ReadAllText(ModelFile(type)), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!
            .PersistentObject.Attributes;

    private static Func<Type, PropertyInfo, ModelAttributeSeed> Seeding(string propertyName, ModelAttributeSeed seed)
        => (type, property) => property.Name == propertyName ? seed : ModelAttributeSeed.Default;

    [Fact]
    public void Default_hook_hides_the_audit_members_and_leaves_the_rest()
    {
        Synchronize(typeof(HkNoteContext));

        var attributes = Attributes(nameof(HkNote));
        foreach (var audit in new[] { nameof(HkNote.CreatedBy), nameof(HkNote.CreatedAtUtc) })
        {
            var attribute = attributes.Single(a => a.Name == audit);
            attribute.ShowedOn.Should().Be(EShowedOn.None, $"{audit} is an audit member");
            attribute.IsReadOnly.Should().BeTrue();
            attribute.IsRequired.Should().BeFalse("no form shows it, so nobody could supply it");
        }

        var title = attributes.Single(a => a.Name == nameof(HkNote.Title));
        title.ShowedOn.Should().Be(EShowedOn.Query | EShowedOn.PersistentObject);
        title.IsReadOnly.Should().BeFalse();
    }

    [Fact]
    public void Exclude_creates_no_attribute()
    {
        Synchronize(typeof(HkNoteContext), Seeding(nameof(HkNote.Body), ModelAttributeSeed.Exclude));

        Attributes(nameof(HkNote)).Select(a => a.Name).Should().NotContain(nameof(HkNote.Body))
            .And.Contain(nameof(HkNote.Title));
    }

    [Fact]
    public void Hidden_creates_an_invisible_read_only_attribute()
    {
        Synchronize(typeof(HkNoteContext), Seeding(nameof(HkNote.Body), ModelAttributeSeed.Hidden));

        var body = Attributes(nameof(HkNote)).Single(a => a.Name == nameof(HkNote.Body));
        body.ShowedOn.Should().Be(EShowedOn.None);
        body.IsReadOnly.Should().BeTrue();
    }

    [Theory]
    [InlineData(ModelAttributeSeed.Default)]
    [InlineData(ModelAttributeSeed.Hidden)]
    public void IgnoreProperty_excludes_whatever_the_hook_says(ModelAttributeSeed seed)
    {
        Synchronize(typeof(HkNoteContext), Seeding(nameof(HkNote.Scratch), seed));

        Attributes(nameof(HkNote)).Select(a => a.Name).Should().NotContain(nameof(HkNote.Scratch));
    }

    [Theory]
    [InlineData(ModelAttributeSeed.Exclude)]
    [InlineData(ModelAttributeSeed.Hidden)]
    public void An_existing_attribute_is_never_changed_or_deleted_by_the_hook(ModelAttributeSeed seed)
    {
        Synchronize(typeof(HkNoteContext), Seeding(nameof(HkNote.Body), ModelAttributeSeed.Default));
        var before = Attributes(nameof(HkNote)).Single(a => a.Name == nameof(HkNote.Body));

        Synchronize(typeof(HkNoteContext), Seeding(nameof(HkNote.Body), seed));

        var after = Attributes(nameof(HkNote)).Single(a => a.Name == nameof(HkNote.Body));
        after.Id.Should().Be(before.Id, "the hook runs at creation only");
        after.ShowedOn.Should().Be(before.ShowedOn);
        after.IsReadOnly.Should().Be(before.IsReadOnly);
        after.Order.Should().Be(before.Order);
    }

    [Fact]
    public void An_excluded_property_stays_excluded_on_the_next_run()
    {
        var hook = Seeding(nameof(HkNote.Body), ModelAttributeSeed.Exclude);
        Synchronize(typeof(HkNoteContext), hook);
        var first = File.ReadAllText(ModelFile(nameof(HkNote)));

        Synchronize(typeof(HkNoteContext), hook);

        File.ReadAllText(ModelFile(nameof(HkNote))).Should().Be(first, "a pure hook is a fixed point");
    }

    /// <summary>
    /// SP5's shape: a root over a library type. The library's model lacks <c>CreatedBy</c>, which the application's root
    /// derives from the CLR type, through the hook (hidden) and at the next free order rather than one the library's
    /// attributes already hold.
    /// </summary>
    [Fact]
    public void A_library_type_root_derives_through_the_hook_at_the_next_free_order()
    {
        var library = new SparkLibrary("hklib", "HkLib", [],
        [
            new SparkLibraryLayer("model", "Model/HkLibOrganization.json", new JsonObject
            {
                ["persistentObject"] = new JsonObject
                {
                    ["id"] = "0d090000-0000-4000-8000-000000000010",
                    ["name"] = nameof(HkLibOrganization),
                    ["clrType"] = typeof(HkLibOrganization).FullName,
                    ["attributes"] = new JsonArray(
                        new JsonObject { ["id"] = "0d090000-0000-4000-8000-000000000011", ["name"] = "Name", ["dataType"] = "string", ["order"] = 1 },
                        new JsonObject { ["id"] = "0d090000-0000-4000-8000-000000000012", ["name"] = "Slug", ["dataType"] = "string", ["order"] = 2 }),
                },
            }.ToJsonString()),
        ]);

        Synchronize(typeof(HkLibraryContext), libraries: [library]);

        var delta = JsonNode.Parse(File.ReadAllText(ModelFile(nameof(HkLibOrganization))))!;
        var createdBy = delta["persistentObject"]!["attributes"]!.AsArray()
            .Single(a => a!["name"]!.GetValue<string>() == nameof(HkLibOrganization.CreatedBy))!;
        createdBy["showedOn"]!.GetValue<string>().Should().Be("None");
        createdBy["isReadOnly"]!.GetValue<bool>().Should().BeTrue();
        createdBy["order"]!.GetValue<int>().Should().Be(3,
            "its place in the property walk, 1, is the library's Name; the next free order is after them all");
    }

    /// <summary>
    /// An excluded complex property pulls nothing into the model: no attribute, and no embedded model file for its type
    /// (or a collection's element type), which nothing would reference.
    /// </summary>
    [Fact]
    public void An_excluded_complex_property_writes_no_model_file_for_its_type()
    {
        Synchronize(typeof(HkDocketContext), (type, property) =>
            property.Name is nameof(HkDocket.Address) or nameof(HkDocket.Lines) ? ModelAttributeSeed.Exclude : ModelAttributeSeed.Default);

        Attributes(nameof(HkDocket)).Select(a => a.Name).Should().NotContain(nameof(HkDocket.Address))
            .And.NotContain(nameof(HkDocket.Lines));
        File.Exists(ModelFile(nameof(HkAddress))).Should().BeFalse("the excluded Address is the only thing that would reference it");
        File.Exists(ModelFile(nameof(HkLine))).Should().BeFalse("the excluded Lines is the only thing that would reference it");
    }

    /// <summary>
    /// The other side: an attribute that already exists outlives the hook (#253), so its type stays in the model too.
    /// </summary>
    [Fact]
    public void An_existing_complex_attribute_keeps_its_type_when_the_hook_later_excludes_it()
    {
        Synchronize(typeof(HkDocketContext));
        File.Exists(ModelFile(nameof(HkAddress))).Should().BeTrue();

        Synchronize(typeof(HkDocketContext), Seeding(nameof(HkDocket.Address), ModelAttributeSeed.Exclude));

        Attributes(nameof(HkDocket)).Select(a => a.Name).Should().Contain(nameof(HkDocket.Address));
        File.Exists(ModelFile(nameof(HkAddress))).Should().BeTrue("the existing Address attribute still references it");
    }

    [Fact]
    public void A_new_attribute_on_a_fresh_type_keeps_its_place_in_the_walk()
    {
        Synchronize(typeof(HkNoteContext));

        Attributes(nameof(HkNote)).OrderBy(a => a.Order).Select(a => a.Order).Should().Equal([1, 2, 3, 4]);
    }
}

// ---- Fixtures ------------------------------------------------------------------------------------------------

public sealed class HkNote
{
    public string? Id { get; set; }
    public string? Title { get; set; }
    public string? Body { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    [IgnoreProperty]
    public string? Scratch { get; set; }
}

public sealed class HkNoteContext : SparkContext
{
    public IRavenQueryable<HkNote> Notes => Session.Query<HkNote>();
}

/// <summary>Stands in for <c>Organization</c>: a library type whose model has no <c>CreatedBy</c>.</summary>
public sealed class HkLibOrganization
{
    public string? Id { get; set; }
    public string? CreatedBy { get; set; }
    public string? Name { get; set; }
    public string? Slug { get; set; }
}

public sealed class HkLibraryContext : SparkContext
{
    public IRavenQueryable<HkLibOrganization> Organizations => Session.Query<HkLibOrganization>();
}

public sealed class HkDocket
{
    public string? Id { get; set; }
    public string? Title { get; set; }
    public HkAddress? Address { get; set; }
    public List<HkLine> Lines { get; set; } = [];
}

public sealed class HkAddress
{
    public string? Street { get; set; }
}

public sealed class HkLine
{
    public string? Text { get; set; }
}

public sealed class HkDocketContext : SparkContext
{
    public IRavenQueryable<HkDocket> Dockets => Session.Query<HkDocket>();
}
