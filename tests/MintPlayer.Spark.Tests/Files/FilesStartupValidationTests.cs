using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Files;
using MintPlayer.Spark.Files.Sniffing;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Tests.Files;

/// <summary>#700 D2b: what <c>AddFiles</c> refuses at startup, beyond the shape core already checked.</summary>
public class FilesStartupValidationTests
{
    private static readonly FileSniffer Sniffer = new([]);

    [Fact]
    public void A_valid_model_starts()
        => SparkFilesExtensions.Unmet(new SparkFilesOptions(), Sniffer, Model(Attribute(SparkFileDataTypes.BinaryFile, ".pdf", maxBytes: 4096))).Should().BeEmpty();

    [Fact]
    public void An_attribute_cannot_loosen_the_configured_ceiling()
    {
        var problems = SparkFilesExtensions.Unmet(new SparkFilesOptions { MaxFileBytes = 1000 }, Sniffer, Model(Attribute(SparkFileDataTypes.Image, null, maxBytes: 1001)));

        problems.Should().ContainSingle().Which.Should().Contain("can only tighten");
    }

    [Fact]
    public void An_accept_entry_the_sniffer_cannot_recognise_is_refused()
    {
        var problems = SparkFilesExtensions.Unmet(new SparkFilesOptions(), Sniffer, Model(Attribute(SparkFileDataTypes.BinaryFile, ".heic")));

        problems.Should().ContainSingle().Which.Should().Contain("AddSignature");
    }

    [Fact]
    public void A_registered_signature_makes_its_type_acceptable()
    {
        var heic = new MagicBytesSignature("HEIC", new Dictionary<string, string> { [".heic"] = "image/heic" }, (4, "ftypheic"u8.ToArray()));

        SparkFilesExtensions.Unmet(new SparkFilesOptions(), new FileSniffer([heic]), Model(Attribute(SparkFileDataTypes.BinaryFile, ".heic"))).Should().BeEmpty();
    }

    [Fact]
    public void Nonsensical_options_are_refused()
    {
        var problems = SparkFilesExtensions.Unmet(
            new SparkFilesOptions { MaxFileBytes = 0, MaxBatch = 0, UploadWindow = TimeSpan.FromDays(2) }, Sniffer, Model());

        problems.Should().HaveCount(3);
    }

    [Theory]
    [InlineData(0L, 2, "thumb", 160)]
    [InlineData(1000L, 0, "thumb", 160)]
    [InlineData(1000L, 2, "../x", 160)]
    [InlineData(1000L, 2, "thumb", 8)]
    [InlineData(1000L, 2, "thumb", 5000)]
    public void Nonsensical_image_options_are_refused(long maxPixels, int concurrency, string variant, int edge)
    {
        var options = new SparkFilesOptions { MaxImagePixels = maxPixels, MaxConcurrentImageProcessing = concurrency, DefaultImageVariants = new() { [variant] = edge } };

        SparkFilesExtensions.Unmet(options, Sniffer, Model()).Should().ContainSingle().Which.Should().Contain("MaxImagePixels");
    }

    private static EntityAttributeDefinition Attribute(string dataType, string? accept, long? maxBytes = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = "File",
        DataType = dataType,
        FileOptions = new SparkFileOptions { Accept = accept is null ? null : [accept], MaxBytes = maxBytes },
    };

    private static IModelLoader Model(params EntityAttributeDefinition[] attributes)
        => new StubModelLoader([new EntityTypeDefinition { Id = Guid.NewGuid(), Name = "Thing", ClrType = "Thing", Attributes = attributes }]);

    private sealed class StubModelLoader(EntityTypeDefinition[] types) : IModelLoader
    {
        public IEnumerable<EntityTypeDefinition> GetEntityTypes() => types;
        public EntityTypeDefinition? GetEntityType(Guid id) => types.FirstOrDefault(t => t.Id == id);
        public EntityTypeDefinition? GetEntityTypeByClrType(string clrType) => null;
        public EntityTypeDefinition? GetEntityTypeByName(string name) => null;
        public EntityTypeDefinition? GetEntityTypeByAlias(string alias) => null;
        public EntityTypeDefinition? ResolveEntityType(string idOrAlias) => null;
        public IEnumerable<SparkQuery> GetQueries() => [];
    }
}
