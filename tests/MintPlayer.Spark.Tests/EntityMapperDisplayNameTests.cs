using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests;

/// <summary>
/// Pins the resolution chain inside <c>EntityMapper.GetEntityDisplayName</c>:
/// <list type="number">
///   <item><c>DisplayFormat</c> takes precedence (template with <c>{Property}</c> placeholders).</item>
///   <item><c>DisplayAttribute</c> next (single property name).</item>
///   <item>CLR type name as final fallback.</item>
/// </list>
/// The PR that introduced ReflectionCache also dropped the legacy
/// "Name → FullName → Title" runtime probe (ModelSynchronizer auto-populates
/// <c>DisplayAttribute</c>); these tests pin the new "purely JSON-driven" contract.
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
    public void DisplayFormat_template_resolves_placeholders_from_entity_properties()
    {
        var def = new EntityTypeDefinition
        {
            Id = PersonTypeId,
            Name = "Person",
            ClrType = typeof(DisplayNamePerson).FullName!,
            DisplayFormat = "{FirstName} {LastName}",
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
    public void DisplayFormat_takes_precedence_over_DisplayAttribute()
    {
        // If both are set, DisplayFormat wins. Pins the order in the resolution chain.
        var def = new EntityTypeDefinition
        {
            Id = PersonTypeId,
            Name = "Person",
            ClrType = typeof(DisplayNamePerson).FullName!,
            DisplayFormat = "{FirstName}!",
            DisplayAttribute = "LastName",
            Attributes = [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "FirstName", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "LastName", DataType = "string" },
            ],
        };
        var (mapper, _) = NewMapper(def);

        var po = mapper.ToPersistentObject(
            new DisplayNamePerson { FirstName = "Grace", LastName = "Hopper" },
            PersonTypeId);

        po.Name.Should().Be("Grace!");
    }

    [Fact]
    public void DisplayFormat_unresolved_placeholder_stays_in_the_string_when_property_missing()
    {
        // {Unknown} doesn't match any property, so it's left as-is. (The original behavior
        // pre-cache; just pinning that the migrated ResolveDisplayFormat preserves it.)
        var def = new EntityTypeDefinition
        {
            Id = PersonTypeId,
            Name = "Person",
            ClrType = typeof(DisplayNamePerson).FullName!,
            DisplayFormat = "{FirstName} {Unknown}",
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
    public void DisplayFormat_with_null_property_value_substitutes_empty_string()
    {
        var def = new EntityTypeDefinition
        {
            Id = PersonTypeId,
            Name = "Person",
            ClrType = typeof(DisplayNamePerson).FullName!,
            DisplayFormat = "{FirstName}-{Nickname}",
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
    public void DisplayAttribute_resolves_a_single_property_when_DisplayFormat_is_unset()
    {
        var def = new EntityTypeDefinition
        {
            Id = PersonTypeId,
            Name = "Person",
            ClrType = typeof(DisplayNamePerson).FullName!,
            DisplayAttribute = "LastName",
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
    public void DisplayAttribute_falls_through_to_CLR_type_name_when_value_is_null()
    {
        // The DisplayAttribute's property exists but evaluates to null at runtime —
        // GetEntityDisplayName must fall back to the CLR type name instead of returning null.
        var def = new EntityTypeDefinition
        {
            Id = PersonTypeId,
            Name = "Person",
            ClrType = typeof(DisplayNamePerson).FullName!,
            DisplayAttribute = "Nickname",
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
    public void DisplayAttribute_pointing_at_missing_property_falls_back_to_CLR_type_name()
    {
        // A malformed EntityTypeDefinition that names a property that doesn't exist on
        // the CLR type. Old behavior would silently fall through to "Name"/"FullName"/
        // "Title"; new behavior returns the CLR type name. Pin the new contract.
        var def = new EntityTypeDefinition
        {
            Id = PersonTypeId,
            Name = "Person",
            ClrType = typeof(DisplayNamePerson).FullName!,
            DisplayAttribute = "DoesNotExistOnCLR",
            Attributes = [],
        };
        var (mapper, _) = NewMapper(def);

        var po = mapper.ToPersistentObject(
            new DisplayNamePerson { FirstName = "X" },
            PersonTypeId);

        po.Name.Should().Be(nameof(DisplayNamePerson));
    }

    [Fact]
    public void Entity_with_no_definition_falls_back_to_CLR_type_name()
    {
        // Projection / anonymous types may not have a registered EntityTypeDefinition.
        // The old runtime fallback to "Name"/"FullName"/"Title" was removed in this PR;
        // such entities now get the CLR type name as their display.
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
    public void DisplayFormat_handles_repeated_placeholders_in_a_single_template()
    {
        var def = new EntityTypeDefinition
        {
            Id = PersonTypeId,
            Name = "Person",
            ClrType = typeof(DisplayNamePerson).FullName!,
            DisplayFormat = "{FirstName} {FirstName} {LastName}",
            Attributes = [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "FirstName", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "LastName", DataType = "string" },
            ],
        };
        var (mapper, _) = NewMapper(def);

        var po = mapper.ToPersistentObject(
            new DisplayNamePerson { FirstName = "Hi", LastName = "Lo" },
            PersonTypeId);

        // Both occurrences of {FirstName} should be replaced — verifies we use Replace
        // (which handles all matches) rather than a single-shot regex match.
        po.Name.Should().Be("Hi Hi Lo");
    }
}
