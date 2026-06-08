using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests;

/// <summary>
/// Pins the display-name resolution inside <c>EntityMapper.GetEntityDisplayName</c> under the
/// single-<c>Breadcrumb</c>-template contract (DisplayFormat/DisplayAttribute were removed):
/// <list type="number">
///   <item>The <c>Breadcrumb</c> template's <c>{Attribute}</c> placeholders resolve from entity properties.</item>
///   <item>A template that resolves to whitespace falls back to the CLR type name.</item>
///   <item>An entity with no definition falls back to the CLR type name.</item>
/// </list>
/// Runtime resolution is intentionally lenient (an unknown placeholder is left verbatim); malformed
/// templates are rejected earlier, at model-sync time (see ModelSynchronizer validation).
/// NOTE (Phase 1): substitution is still flat — reference placeholders render the raw id until
/// BreadcrumbResolver lands.
/// </summary>
public class EntityMapperDisplayNameTests
{
    private static readonly Guid PersonTypeId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private sealed class DisplayNamePerson
    {
        public string? Id { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string? Nickname { get; set; }
    }

    private static (EntityMapper mapper, IModelLoader loader) NewMapper(EntityTypeDefinition def)
    {
        var loader = Substitute.For<IModelLoader>();
        loader.GetEntityType(PersonTypeId).Returns(def);
        loader.GetEntityTypeByClrType(typeof(DisplayNamePerson).FullName!).Returns(def);
        return (new EntityMapper(loader), loader);
    }

    [Fact]
    public void Breadcrumb_template_resolves_placeholders_from_entity_properties()
    {
        var def = new EntityTypeDefinition
        {
            Id = PersonTypeId,
            Name = "Person",
            ClrType = typeof(DisplayNamePerson).FullName!,
            Breadcrumb = "{FirstName} {LastName}",
            Attributes = [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "FirstName", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "LastName", DataType = "string" },
            ],
        };
        var (mapper, _) = NewMapper(def);

        var po = mapper.ToPersistentObject(
            new DisplayNamePerson { Id = "p/1", FirstName = "Ada", LastName = "Lovelace" },
            PersonTypeId);

        po.Name.Should().Be("Ada Lovelace");
        po.Breadcrumb.Should().Be("Ada Lovelace");
    }

    [Fact]
    public void Breadcrumb_unresolved_placeholder_stays_in_the_string_when_property_missing()
    {
        // {Unknown} matches no property, so runtime leaves it verbatim. (Model-sync validation
        // is what rejects unknown placeholders; runtime stays lenient.)
        var def = new EntityTypeDefinition
        {
            Id = PersonTypeId,
            Name = "Person",
            ClrType = typeof(DisplayNamePerson).FullName!,
            Breadcrumb = "{FirstName} {Unknown}",
            Attributes = [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "FirstName", DataType = "string" },
            ],
        };
        var (mapper, _) = NewMapper(def);

        var po = mapper.ToPersistentObject(
            new DisplayNamePerson { FirstName = "X" },
            PersonTypeId);

        po.Name.Should().Be("X {Unknown}");
    }

    [Fact]
    public void Breadcrumb_with_null_property_value_substitutes_empty_string()
    {
        var def = new EntityTypeDefinition
        {
            Id = PersonTypeId,
            Name = "Person",
            ClrType = typeof(DisplayNamePerson).FullName!,
            Breadcrumb = "{FirstName}-{Nickname}",
            Attributes = [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "FirstName", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Nickname", DataType = "string" },
            ],
        };
        var (mapper, _) = NewMapper(def);

        var po = mapper.ToPersistentObject(
            new DisplayNamePerson { FirstName = "Linus", Nickname = null },
            PersonTypeId);

        po.Name.Should().Be("Linus-");
    }

    [Fact]
    public void Breadcrumb_single_placeholder_resolves_to_the_property_value()
    {
        var def = new EntityTypeDefinition
        {
            Id = PersonTypeId,
            Name = "Person",
            ClrType = typeof(DisplayNamePerson).FullName!,
            Breadcrumb = "{LastName}",
            Attributes = [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "LastName", DataType = "string" },
            ],
        };
        var (mapper, _) = NewMapper(def);

        var po = mapper.ToPersistentObject(
            new DisplayNamePerson { LastName = "Knuth" },
            PersonTypeId);

        po.Name.Should().Be("Knuth");
    }

    [Fact]
    public void Breadcrumb_resolving_to_whitespace_falls_back_to_CLR_type_name()
    {
        // A single placeholder whose value is null resolves to empty → fall back to the CLR type name
        // rather than returning an empty display.
        var def = new EntityTypeDefinition
        {
            Id = PersonTypeId,
            Name = "Person",
            ClrType = typeof(DisplayNamePerson).FullName!,
            Breadcrumb = "{Nickname}",
            Attributes = [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Nickname", DataType = "string" },
            ],
        };
        var (mapper, _) = NewMapper(def);

        var po = mapper.ToPersistentObject(
            new DisplayNamePerson { Nickname = null },
            PersonTypeId);

        po.Name.Should().Be(nameof(DisplayNamePerson));
    }

    [Fact]
    public void Entity_with_no_definition_falls_back_to_CLR_type_name()
    {
        // Projection / anonymous types may not have a registered EntityTypeDefinition.
        var loader = Substitute.For<IModelLoader>();
        loader.GetEntityType(PersonTypeId).Returns((EntityTypeDefinition?)null);
        loader.GetEntityTypeByClrType(typeof(DisplayNamePerson).FullName!).Returns((EntityTypeDefinition?)null);
        var mapper = new EntityMapper(loader);

        var po = mapper.ToPersistentObject(
            new DisplayNamePerson { FirstName = "Anon" },
            PersonTypeId);

        po.Name.Should().Be(nameof(DisplayNamePerson));
    }

    [Fact]
    public void Breadcrumb_handles_repeated_placeholders_in_a_single_template()
    {
        var def = new EntityTypeDefinition
        {
            Id = PersonTypeId,
            Name = "Person",
            ClrType = typeof(DisplayNamePerson).FullName!,
            Breadcrumb = "{FirstName} {FirstName} {LastName}",
            Attributes = [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "FirstName", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "LastName", DataType = "string" },
            ],
        };
        var (mapper, _) = NewMapper(def);

        var po = mapper.ToPersistentObject(
            new DisplayNamePerson { FirstName = "Hi", LastName = "Lo" },
            PersonTypeId);

        po.Name.Should().Be("Hi Hi Lo");
    }
}
