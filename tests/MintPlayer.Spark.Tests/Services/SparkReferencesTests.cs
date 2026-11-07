using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>The startup checks on Reference attributes (reference add-new PRD D13, D17, D18).</summary>
public class SparkReferencesTests
{
    public class SrOwner { }
    public class SrTarget { }
    public class SrOther { }

    public class SrKeyed
    {
        public string Code { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
    }

    static SparkReferencesTests()
        => SparkValueObjects.Register(typeof(SrKeyed), nameof(SrKeyed.Code), row => ((SrKeyed)row).Code);

    private static EntityAttributeDefinition Attr(string name, string dataType = "string", bool readOnly = false)
        => new() { Id = Guid.NewGuid(), Name = name, DataType = dataType, IsReadOnly = readOnly };

    private static EntityTypeDefinition Type(string name, Type? clr, params EntityAttributeDefinition[] attributes)
        => new() { Id = Guid.NewGuid(), Name = name, ClrType = clr?.FullName, Attributes = attributes };

    private static EntityTypeDefinition Owner(EntityAttributeDefinition reference)
        => Type("SrOwner", typeof(SrOwner), reference);

    private static EntityAttributeDefinition Ref(string query, Type? referenceType, bool canAddNew = true, string? defaultAttribute = null)
        => new()
        {
            Id = Guid.NewGuid(), Name = "Target", DataType = "Reference", Query = query,
            ReferenceType = referenceType?.FullName, CanAddNewReference = canAddNew ? true : null, DefaultAttribute = defaultAttribute,
        };

    private static readonly EntityTypeDefinition Target = Type("SrTarget", typeof(SrTarget), Attr("Name"), Attr("Size", "number"), Attr("Locked", readOnly: true));
    private static readonly EntityTypeDefinition Other = Type("SrOther", typeof(SrOther), Attr("Name"));

    private static readonly SparkQuery[] Queries =
    [
        new() { Id = Guid.NewGuid(), Name = "SrGetTargets", Source = "Database.Targets", EntityType = "SrTarget" },
        new() { Id = Guid.NewGuid(), Name = "SrGetOthers", Source = "Database.Others", EntityType = "SrOther" },
        new() { Id = Guid.NewGuid(), Name = "SrGetKeyed", Source = "Custom.Keyed", EntityType = "SrKeyed" },
    ];

    private static Type? Resolve(string? clrType)
        => new[] { typeof(SrOwner), typeof(SrTarget), typeof(SrOther), typeof(SrKeyed) }.FirstOrDefault(t => t.FullName == clrType);

    [Fact]
    public void A_consistent_reference_passes()
        => SparkReferences.Validate([Owner(Ref("SrGetTargets", typeof(SrTarget), defaultAttribute: "Name")), Target, Other], Queries, Resolve)
            .Should().BeEmpty();

    [Fact]
    public void A_lookup_query_listing_another_type_is_refused()
        => SparkReferences.Validate([Owner(Ref("SrGetOthers", typeof(SrTarget))), Target, Other], Queries)
            .Should().ContainSingle().Which.Should().Contain("lists 'SrOther'");

    [Theory]
    [InlineData("Missing", "not an attribute")]
    [InlineData("Size", "not a String")]
    [InlineData("Locked", "read-only")]
    public void A_default_attribute_that_cannot_be_prefilled_is_refused(string defaultAttribute, string expected)
        => SparkReferences.Validate([Owner(Ref("SrGetTargets", typeof(SrTarget), defaultAttribute: defaultAttribute)), Target, Other], Queries)
            .Should().ContainSingle().Which.Should().Contain(expected);

    [Fact]
    public void A_default_attribute_without_canAddNewReference_is_refused()
        => SparkReferences.Validate([Owner(Ref("SrGetTargets", typeof(SrTarget), canAddNew: false, defaultAttribute: "Name")), Target, Other], Queries)
            .Should().ContainSingle().Which.Should().Contain("without canAddNewReference");

    [Fact]
    public void Add_new_on_a_value_object_needs_its_key_as_a_writable_attribute()
    {
        var keyless = Type("SrKeyed", typeof(SrKeyed), Attr("Text"));
        var readOnlyKey = Type("SrKeyed", typeof(SrKeyed), Attr("Code", readOnly: true), Attr("Text"));
        var writableKey = Type("SrKeyed", typeof(SrKeyed), Attr("Code"), Attr("Text"));

        SparkReferences.Validate([Owner(Ref("SrGetKeyed", typeof(SrKeyed))), keyless], Queries, Resolve)
            .Should().ContainSingle().Which.Should().Contain("[ValueKey] 'Code'");
        SparkReferences.Validate([Owner(Ref("SrGetKeyed", typeof(SrKeyed))), readOnlyKey], Queries, Resolve)
            .Should().ContainSingle();
        SparkReferences.Validate([Owner(Ref("SrGetKeyed", typeof(SrKeyed))), writableKey], Queries, Resolve)
            .Should().BeEmpty();
    }
}
