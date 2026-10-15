using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Endpoints.EntityTypes;

// Fixture names start with "VoFlag": ActionsResolver matches actions classes by simple name across
// the whole assembly. Top-level, so the right's type name is the CLR name's last segment.

/// <summary>An embedded row, like CodeCoverage's <c>ProjectColumn</c>: it has no document of its own.</summary>
[ValueObject]
public partial class VoFlagColumn
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
}

/// <summary>A stored document.</summary>
public class VoFlagDocument
{
    public string? Id { get; set; }
    public string Name { get; set; } = "";
}

public class VoFlagColumnActions(IEntityMapper mapper)
    : DefaultPersistentObjectActions<VoFlagColumn>(mapper), ISparkOwnsRowSecurity
{
    public string RowSecurityRationale => "Test fixture: only the entity-type catalogue is read.";
}

public class VoFlagDocumentActions(IEntityMapper mapper)
    : DefaultPersistentObjectActions<VoFlagDocument>(mapper), ISparkOwnsRowSecurity
{
    public string RowSecurityRationale => "Test fixture: only the entity-type catalogue is read.";
}

/// <summary>
/// The catalogue says which types are <c>[ValueObject]</c>s, so the client does not render a reference
/// to one as a link to <c>/po/{type}/{id}</c>, a page that cannot load an embedded row and 404s
/// (merge-queue PRD D115).
/// </summary>
/// <remarks>
/// The caller holds <c>Read</c> on both types on purpose: that is the real shape (withholding Read hides
/// the referencing attribute), and it is why <c>canRead</c> alone could not answer this.
/// </remarks>
public class ValueObjectFlagTests(ValueObjectFlagTests.Host host)
    : SparkSharedTestDriver(host), IClassFixture<ValueObjectFlagTests.Host>
{
    public sealed class Host : SharedSparkHost<TestSparkContext>
    {
        protected override SparkEndpointFactory<TestSparkContext> CreateFactory()
            => new SparkEndpointFactory(
                Store,
                [ColumnModel(), DocumentModel()],
                configureServices: services =>
                {
                    services.AddScoped<VoFlagColumnActions>();
                    services.AddScoped<VoFlagDocumentActions>();
                },
                security: SparkTestSecurity.Empty.Granting("QueryRead/VoFlagColumn", "QueryRead/VoFlagDocument"));
    }

    private static readonly Guid ColumnTypeId = Guid.Parse("a0f1a90b-0000-4000-8000-00000000e001");
    private static readonly Guid DocumentTypeId = Guid.Parse("a0f1a90b-0000-4000-8000-00000000e002");

    private static EntityTypeFile ColumnModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = ColumnTypeId,
            Name = "VoFlagColumn",
            ClrType = typeof(VoFlagColumn).FullName,
            Attributes = [NameAttribute()],
        },
    };

    /// <summary>Claims to be a value object in its model file; the catalogue derives the flag and ignores that.</summary>
    private static EntityTypeFile DocumentModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = DocumentTypeId,
            Name = "VoFlagDocument",
            ClrType = typeof(VoFlagDocument).FullName,
            IsValueObject = true,
            Attributes = [NameAttribute()],
        },
    };

    private static EntityAttributeDefinition NameAttribute() => new()
    {
        Id = Guid.NewGuid(), Name = "Name", DataType = "string",
        ShowedOn = EShowedOn.Query | EShowedOn.PersistentObject,
    };

    [Fact]
    public async Task The_catalogue_flags_a_value_object_and_not_a_document()
    {
        using var client = new SparkClient(host.Factory.CreateClient(), ownsClient: true);

        var types = await client.ListEntityTypesAsync();

        var column = types.Single(t => t.Id == ColumnTypeId);
        column.IsValueObject.Should().BeTrue("a [ValueObject] has no document, so no page can open one");
        column.CanRead.Should().BeTrue("Read stays granted; the flag, not the right, withholds the link");
        types.Single(t => t.Id == DocumentTypeId).IsValueObject.HasValue.Should().BeFalse(
            "the flag is derived from the CLR type, so a model file cannot switch a document's links off");
    }

    [Fact]
    public async Task A_single_type_carries_the_same_flag()
    {
        using var client = new SparkClient(host.Factory.CreateClient(), ownsClient: true);

        var column = await client.GetEntityTypeAsync(ColumnTypeId.ToString());
        var document = await client.GetEntityTypeAsync(DocumentTypeId.ToString());

        column!.IsValueObject.Should().BeTrue();
        document!.IsValueObject.HasValue.Should().BeFalse();
    }
}
