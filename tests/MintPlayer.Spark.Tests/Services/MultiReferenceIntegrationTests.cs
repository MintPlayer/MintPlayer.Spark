using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// End-to-end coverage for the list-of-references feature through the real
/// <see cref="IDatabaseAccess"/> pipeline (SparkTestDriver in-memory RavenDB +
/// SparkEndpointFactory host). Exercises the three .NET pieces together:
/// <list type="bullet">
///   <item>ReferenceResolver includes + resolves a breadcrumb per id in the array.</item>
///   <item>EntityMapper forward path carries the id array in Value and a per-id Breadcrumbs map.</item>
///   <item>EntityMapper inverse path persists an id array back onto the <c>List&lt;string&gt;</c> property.</item>
/// </list>
/// </summary>
public class MultiReferenceIntegrationTests : SparkTestDriver
{
    private static readonly Guid TaggedTypeId = Guid.Parse("aa11bb22-cc33-dd44-ee55-ff6677889900");
    private static readonly Guid TagTypeId = Guid.Parse("bb22cc33-dd44-ee55-ff66-778899001122");

    private SparkEndpointFactory<MultiRefContext> _factory = null!;
    private IDatabaseAccess _dbAccess = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _factory = new SparkEndpointFactory<MultiRefContext>(Store, [TagModel(), TaggedModel()]);
        _dbAccess = _factory.GetService<IDatabaseAccess>();
    }

    public override async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task SeedAsync()
    {
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(new MR_Tag { Id = "tags/1", Name = "news" });
        await session.StoreAsync(new MR_Tag { Id = "tags/2", Name = "sports" });
        await session.StoreAsync(new MR_Tagged { Id = "taggeds/1", Title = "Post", TagIds = ["tags/1", "tags/2"] });
        await session.SaveChangesAsync();
    }

    [Fact]
    public async Task Get_resolves_reference_array_value_and_per_id_breadcrumbs()
    {
        await SeedAsync();

        var po = await _dbAccess.GetPersistentObjectAsync(TaggedTypeId, "taggeds/1");

        po.Should().NotBeNull();
        var tagIds = po!.Attributes.Single(a => a.Name == "TagIds");
        tagIds.IsArray.Should().BeTrue();
        ((IEnumerable<string>)tagIds.Value!).Should().Equal("tags/1", "tags/2");
        tagIds.Breadcrumbs.Should().NotBeNull();
        tagIds.Breadcrumbs!["tags/1"].Should().Be("news");
        tagIds.Breadcrumbs!["tags/2"].Should().Be("sports");
    }

    [Fact]
    public async Task Save_persists_reference_id_array_onto_the_entity()
    {
        await SeedAsync();

        var po = new PersistentObject
        {
            Id = "taggeds/1",
            Name = "Tagged",
            ObjectTypeId = TaggedTypeId,
        };
        po.AddAttribute(new PersistentObjectAttribute { Name = "Title", DataType = "string", Value = "Post", IsValueChanged = true });
        po.AddAttribute(new PersistentObjectAttribute { Name = "TagIds", DataType = "Reference", IsArray = true, Value = new List<string> { "tags/2" }, IsValueChanged = true });

        await _dbAccess.SavePersistentObjectAsync(po);

        using var session = Store.OpenAsyncSession();
        var reloaded = await session.LoadAsync<MR_Tagged>("taggeds/1");
        // The update replaced the two-tag selection with a single tag.
        reloaded.TagIds.Should().Equal("tags/2");
    }

    private static EntityTypeFile TagModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = TagTypeId,
            Name = "Tag",
            ClrType = typeof(MR_Tag).FullName!,
            DisplayAttribute = "Name",
            Attributes = [new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Name", DataType = "string" }],
        },
    };

    private static EntityTypeFile TaggedModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = TaggedTypeId,
            Name = "Tagged",
            ClrType = typeof(MR_Tagged).FullName!,
            DisplayAttribute = "Title",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Title", DataType = "string" },
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = "TagIds",
                    DataType = "Reference",
                    IsArray = true,
                    ReferenceType = typeof(MR_Tag).FullName,
                    Query = "GetTags",
                },
            ],
        },
    };
}

public class MR_Tag
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class MR_Tagged
{
    public string? Id { get; set; }
    public string Title { get; set; } = string.Empty;

    [Reference(typeof(MR_Tag))]
    public List<string> TagIds { get; set; } = [];
}

public class MR_TagActions : DefaultPersistentObjectActions<MR_Tag>
{
    public MR_TagActions(IEntityMapper entityMapper) : base(entityMapper) { }
}

public class MR_TaggedActions : DefaultPersistentObjectActions<MR_Tagged>
{
    public MR_TaggedActions(IEntityMapper entityMapper) : base(entityMapper) { }
}

public class MultiRefContext : SparkContext
{
    public IRavenQueryable<MR_Tag> Tags => Session.Query<MR_Tag>();
    public IRavenQueryable<MR_Tagged> Taggeds => Session.Query<MR_Tagged>();
}
