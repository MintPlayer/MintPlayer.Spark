using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// A row handed to <c>OnNewAsync</c> must already carry its row key.
/// </summary>
/// <remarks>
/// <c>New.cs</c> used to scaffold with <c>GetPersistentObject</c> alone, which builds the object
/// from the <em>model file</em> and never constructs the entity — so the <c>Guid</c> field
/// initializer that mints a <c>[ValueObject]</c>'s key (row identity R2) never ran and the row came
/// back with no key at all.
/// <para>
/// That is survivable by accident on the save path — an unmatched row correctly takes the create
/// branch — which is exactly why it needs a test of its own: nothing downstream complains. What it
/// is not survivable for is the feature itself. A construction hook cannot reference the row it is
/// building, and the client round-trips the key as <c>__sparkRowKey</c>, so a keyless row sends back
/// nothing and the save cannot match it.
/// </para>
/// <para>
/// These tests are on the mapper rather than the endpoint deliberately: the endpoint's fix is two
/// lines of glue over this mechanism, and it is the mechanism — "constructing the instance is what
/// mints the key" — that must not quietly stop being true.
/// </para>
/// </remarks>
public class NewRowIsKeyedTests
{
    private static readonly Guid VisitTypeId = Guid.Parse("7c4b0000-0000-4000-8000-7c4b00000001");

    /// <summary>Keyed the way <c>ValueObjectKeyGenerator</c> keys a <c>[ValueObject]</c>.</summary>
    public class Visit
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Description { get; set; } = string.Empty;
        public int Mileage { get; set; } = 42;
    }

    static NewRowIsKeyedTests()
        => SparkValueObjects.Register(typeof(Visit), nameof(Visit.Id), row => ((Visit)row).Id);

    private static EntityMapper Mapper()
    {
        var modelLoader = Substitute.For<IModelLoader>();

        var visitDef = new EntityTypeDefinition
        {
            Id = VisitTypeId,
            Name = "Visit",
            ClrType = typeof(Visit).FullName!,
            Breadcrumb = "{Description}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Description", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Mileage", DataType = "int" },
            ],
        };

        modelLoader.GetEntityType(VisitTypeId).Returns(visitDef);
        modelLoader.GetEntityTypeByClrType(typeof(Visit).FullName!).Returns(visitDef);

        return new EntityMapper(modelLoader);
    }

    [Fact]
    public void Scaffolding_from_the_model_alone_leaves_the_row_keyless()
    {
        var mapper = Mapper();

        var po = mapper.GetPersistentObject(VisitTypeId);

        // Not a bug to fix here — this is the honest behaviour of a model-only scaffold, and it is
        // the reason New.cs must go on to construct the entity. If this ever starts returning a key,
        // the endpoint's second step has become redundant and should be removed rather than left to
        // mint a second one.
        po.Id.Should().BeNullOrEmpty();
    }

    [Fact]
    public void Constructing_the_entity_is_what_mints_the_key()
    {
        var mapper = Mapper();
        var po = mapper.GetPersistentObject(VisitTypeId);

        mapper.PopulateAttributeValues(po, Activator.CreateInstance(typeof(Visit))!);

        po.Id.Should().NotBeNullOrEmpty(
            "the client round-trips this as __sparkRowKey, and a save can only match a row that has one");
    }

    [Fact]
    public void Two_new_rows_do_not_share_a_key()
    {
        var mapper = Mapper();

        var first = mapper.GetPersistentObject(VisitTypeId);
        mapper.PopulateAttributeValues(first, Activator.CreateInstance(typeof(Visit))!);

        var second = mapper.GetPersistentObject(VisitTypeId);
        mapper.PopulateAttributeValues(second, Activator.CreateInstance(typeof(Visit))!);

        // Adding two rows before saving is ordinary. If they shared a key the save would merge them
        // into one and silently drop a row the user entered.
        second.Id.Should().NotBe(first.Id);
    }

    [Fact]
    public void Property_initializers_become_the_row_s_defaults()
    {
        var mapper = Mapper();
        var po = mapper.GetPersistentObject(VisitTypeId);

        mapper.PopulateAttributeValues(po, Activator.CreateInstance(typeof(Visit))!);

        // The second thing constructing the instance buys, and the reason it is better than minting
        // a bare guid: a C# property initializer is how a .NET developer states a default, and this
        // makes that the default the user actually sees.
        po["Mileage"].Value.Should().Be(42);
    }
}
