using System.Text.Json;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Services.Breadcrumb;
using NSubstitute;

namespace MintPlayer.Spark.Tests;

/// <summary>
/// Covers the list-of-references mapping: a <c>[Reference] List&lt;string&gt;</c> attribute
/// (dataType "Reference", IsArray true) round-trips its id array through <c>attr.Value</c>
/// and resolves a per-id breadcrumb on the forward path; a bare <c>List&lt;string&gt;</c>
/// (scalar array) round-trips without breadcrumbs.
/// </summary>
public class EntityMapperReferenceArrayTests
{
    private static readonly Guid TaggedTypeId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid TagTypeId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly EntityMapper _mapper;

    public EntityMapperReferenceArrayTests()
    {
        var modelLoader = Substitute.For<IModelLoader>();

        var taggedDef = new EntityTypeDefinition
        {
            Id = TaggedTypeId,
            Name = "Tagged",
            ClrType = typeof(EM_Tagged).FullName!,
            Breadcrumb = "{Title}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Title", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "TagIds", DataType = "Reference", IsArray = true, ReferenceType = typeof(EM_Tag).FullName, Query = "GetTags" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Labels", DataType = "string", IsArray = true },
            ],
        };

        var tagDef = new EntityTypeDefinition
        {
            Id = TagTypeId,
            Name = "Tag",
            ClrType = typeof(EM_Tag).FullName!,
            Breadcrumb = "{Name}",
            Attributes = [new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Name", DataType = "string" }],
        };

        modelLoader.GetEntityType(TaggedTypeId).Returns(taggedDef);
        modelLoader.GetEntityType(TagTypeId).Returns(tagDef);
        modelLoader.GetEntityTypeByClrType(typeof(EM_Tagged).FullName!).Returns(taggedDef);
        modelLoader.GetEntityTypeByClrType(typeof(EM_Tag).FullName!).Returns(tagDef);

        _mapper = new EntityMapper(modelLoader);
    }

    [Fact]
    public void Forward_reference_array_carries_id_array_and_per_id_breadcrumbs()
    {
        var tagged = new EM_Tagged { Id = "Tagged/1", Title = "Post", TagIds = ["Tags/1", "Tags/2"] };
        var breadcrumbs = new BreadcrumbResult(new Dictionary<string, string>
        {
            ["Tags/1"] = "news",
            ["Tags/2"] = "sports",
        });

        var po = _mapper.ToPersistentObject(tagged, TaggedTypeId, breadcrumbs);

        var tagIds = po.Attributes.Single(a => a.Name == "TagIds");
        tagIds.Value.Should().BeAssignableTo<IEnumerable<string>>();
        ((IEnumerable<string>)tagIds.Value!).Should().Equal("Tags/1", "Tags/2");
        tagIds.Breadcrumb.Should().BeNull("array references use Breadcrumbs, not the single Breadcrumb");
        tagIds.Breadcrumbs.Should().NotBeNull();
        tagIds.Breadcrumbs!["Tags/1"].Should().Be("news");
        tagIds.Breadcrumbs!["Tags/2"].Should().Be("sports");
    }

    [Fact]
    public void Forward_reference_array_without_included_documents_has_no_breadcrumbs()
    {
        var tagged = new EM_Tagged { Id = "Tagged/1", Title = "Post", TagIds = ["Tags/1"] };

        var po = _mapper.ToPersistentObject(tagged, TaggedTypeId);

        var tagIds = po.Attributes.Single(a => a.Name == "TagIds");
        ((IEnumerable<string>)tagIds.Value!).Should().Equal("Tags/1");
        tagIds.Breadcrumbs.Should().BeNull();
    }

    [Fact]
    public void Reverse_reference_array_writes_id_array_onto_entity()
    {
        var po = new PersistentObject
        {
            Id = "Tagged/1",
            Name = "Tagged",
            ObjectTypeId = TaggedTypeId,
            Attributes =
            [
                new PersistentObjectAttribute { Name = "TagIds", DataType = "Reference", IsArray = true, Value = JsonArray("Tags/1", "Tags/2") },
            ],
        };
        var entity = new EM_Tagged();

        _mapper.PopulateObjectValues(po, entity);

        entity.TagIds.Should().Equal("Tags/1", "Tags/2");
    }

    [Fact]
    public void Reverse_scalar_array_writes_values_onto_entity()
    {
        var po = new PersistentObject
        {
            Id = "Tagged/1",
            Name = "Tagged",
            ObjectTypeId = TaggedTypeId,
            Attributes =
            [
                new PersistentObjectAttribute { Name = "Labels", DataType = "string", IsArray = true, Value = JsonArray("a", "b", "c") },
            ],
        };
        var entity = new EM_Tagged();

        _mapper.PopulateObjectValues(po, entity);

        entity.Labels.Should().Equal("a", "b", "c");
    }

    private static JsonElement JsonArray(params string[] values)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(values));
        return doc.RootElement.Clone();
    }

    public class EM_Tag
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public class EM_Tagged
    {
        public string? Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public List<string> TagIds { get; set; } = [];
        public List<string> Labels { get; set; } = [];
    }
}
