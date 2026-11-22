using System.Text.Json;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Files;

/// <summary>
/// #700 M1: the core half of file attributes. Core owns the data types, the model's <c>fileOptions</c>
/// and the save-path dispatch; the Files plugin implements <see cref="ISparkFileValueHandler"/>. The
/// security property tested throughout is that a posted file value is <b>never</b> deserialized into
/// the entity: a forged <c>fileId</c> must not reach the document.
/// </summary>
public class FileAttributeCoreTests
{
    private static readonly Guid DocTypeId = Guid.NewGuid();

    // --- data types ------------------------------------------------------

    [Theory]
    [InlineData(typeof(SparkFile), "BinaryFile")]
    [InlineData(typeof(SparkImage), "Image")]
    [InlineData(typeof(List<SparkFile>), "BinaryFile")]
    [InlineData(typeof(List<SparkImage>), "Image")]
    [InlineData(typeof(SparkImage[]), "Image")]
    [InlineData(typeof(DerivedImage), "Image")]
    public void A_stored_file_property_gets_its_file_data_type_not_AsDetail(Type type, string expected)
    {
        // The collection branch classifies by element type, and IsComplexType(SparkImage) would have
        // made a List<SparkImage> an AsDetail (SP1). The file test must come first.
        SparkModelShape.GetDataType(type).Should().Be(expected);
    }

    [Fact]
    public void An_image_url_string_stays_a_string_until_the_model_says_Image()
        => SparkModelShape.GetDataType(typeof(string)).Should().Be("string");

    // --- fileOptions: structural -------------------------------------------

    private static string Model(string attributeExtras) => $$"""
        { "persistentObject": { "name": "Doc", "clrType": "X.Doc", "attributes": [
          { "name": "Scan", "dataType": "BinaryFile"{{attributeExtras}} } ] } }
        """;

    [Fact]
    public void Loosening_fileOptions_moves_the_model_hash()
    {
        // fileOptions gates writes, so the model gate must see an edit to it (SP11).
        var tight = ModelFileShape.DescribeJson(Model(""", "fileOptions": { "accept": [".pdf"], "maxBytes": 1000 }"""));
        var loose = ModelFileShape.DescribeJson(Model(""", "fileOptions": { "accept": [".pdf"], "maxBytes": 99999999 }"""));
        var opened = ModelFileShape.DescribeJson(Model(""", "fileOptions": { "accept": [".pdf", ".exe"], "maxBytes": 1000 }"""));

        loose.Should().NotBe(tight);
        opened.Should().NotBe(tight);
    }

    [Fact]
    public void fileOptions_formatting_and_key_order_do_not_move_the_hash()
    {
        var a = ModelFileShape.DescribeJson(Model(""", "fileOptions": { "accept": [".pdf"], "maxBytes": 1000 }"""));
        const string reformatted = """
            ,
                "fileOptions": {
                    "maxBytes": 1000,
                    "accept": [ ".pdf" ]
                }
            """;
        var b = ModelFileShape.DescribeJson(Model(reformatted));

        b.Should().Be(a);
    }

    [Fact]
    public void A_model_without_fileOptions_hashes_as_before()
    {
        // Appended only when present: no existing model file's hash moves with this change.
        ModelFileShape.DescribeJson(Model("")).Should().NotContain("fileOptions");
    }

    // --- fileOptions: shape ------------------------------------------------

    [Theory]
    [InlineData("BinaryFile", false, """{ "accept": ["*"] }""", "wildcard")]
    [InlineData("BinaryFile", false, """{ "accept": ["application/*"] }""", "wildcard")]
    [InlineData("BinaryFile", false, """{ "accept": ["pdf"] }""", "neither a MIME type")]
    [InlineData("BinaryFile", false, """{ "accept": [" "] }""", "empty entry")]
    [InlineData("Image", false, """{ "accept": [".svg"] }""", "SVG")]
    [InlineData("Image", false, """{ "accept": ["image/svg+xml"] }""", "SVG")]
    [InlineData("BinaryFile", false, """{ "maxBytes": 0 }""", "maxBytes must be positive")]
    [InlineData("BinaryFile", false, """{ "maxCount": 3 }""", "only valid on a list")]
    [InlineData("BinaryFile", true, """{ "maxCount": 0 }""", "maxCount must be positive")]
    [InlineData("BinaryFile", false, """{ "variants": { "thumb": 160 } }""", "only valid on an Image")]
    [InlineData("Image", false, """{ "variants": { "thumb": 8 } }""", "between")]
    [InlineData("Image", false, """{ "variants": { "../x": 160 } }""", "letters, digits")]
    [InlineData("Image", false, """{ "variants": { "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa": 160 } }""", "at most 64")]
    [InlineData("BinaryFile", false, """{ "maxPixels": 1000 }""", "maxPixels is only valid on an Image")]
    [InlineData("Image", false, """{ "maxPixels": 0 }""", "maxPixels must be positive")]
    [InlineData("string", false, """{ "maxBytes": 10 }""", "only valid on a BinaryFile or Image")]
    public void A_malformed_fileOptions_is_reported(string dataType, bool isArray, string json, string expected)
    {
        var options = JsonSerializer.Deserialize<SparkFileOptions>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        SparkFileOptions.ShapeErrors(options, "Doc.Scan", dataType, isArray)
            .Should().ContainSingle().Which.Should().Contain(expected).And.StartWith("Doc.Scan");
    }

    [Fact]
    public void A_well_formed_fileOptions_has_no_errors()
    {
        var options = new SparkFileOptions
        {
            Accept = ["image/png", ".jpg"],
            MaxBytes = 5_000_000,
            MaxCount = 4,
            Variants = new() { ["thumb"] = 160, ["hero-2x"] = 1600 },
            MaxPixels = 12_000_000,
            Delivery = SparkFileDelivery.Proxy,
        };

        SparkFileOptions.ShapeErrors(options, "Car.Photos", "Image", isArray: true).Should().BeEmpty();
    }

    [Fact]
    public void Delivery_reads_as_a_name_in_the_model_file()
        => JsonSerializer.Deserialize<SparkFileOptions>("""{ "delivery": "Proxy" }""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!
            .Delivery.Should().Be(SparkFileDelivery.Proxy);

    // --- save path ----------------------------------------------------------

    private static (EntityMapper Mapper, IModelLoader Loader) CreateMapper(ISparkFileValueHandler? handler)
    {
        var loader = Substitute.For<IModelLoader>();
        var def = new EntityTypeDefinition
        {
            Id = DocTypeId,
            Name = "Doc",
            ClrType = typeof(Doc).FullName!,
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Title", DataType = "string" },
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = "Scan", DataType = "BinaryFile",
                    FileOptions = new SparkFileOptions { Accept = [".pdf"] },
                },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Photos", DataType = "Image", IsArray = true },
            ],
        };
        loader.GetEntityType(DocTypeId).Returns(def);
        loader.GetEntityTypeByClrType(typeof(Doc).FullName!).Returns(def);
        loader.GetEntityTypeByName("Doc").Returns(def);
        return (new EntityMapper(loader, fileValueHandler: handler), loader);
    }

    /// <summary>A forged stored value: someone else's file id, posted as if it were this document's.</summary>
    private static PersistentObject Posted(string attribute = "Scan") => new()
    {
        Name = "Doc",
        ObjectTypeId = DocTypeId,
        Attributes =
        [
            new PersistentObjectAttribute { Name = "Title", Value = "t" },
            new PersistentObjectAttribute
            {
                Name = attribute,
                DataType = attribute == "Scan" ? "BinaryFile" : "Image",
                IsArray = attribute == "Photos",
                Value = JsonDocument.Parse(attribute == "Scan"
                    ? """{ "fileId": "someone-elses-file", "fileName": "x.pdf", "scanStatus": 1 }"""
                    : """[{ "fileId": "someone-elses-file" }]""").RootElement,
            },
        ],
    };

    [Theory]
    [InlineData("Scan")]
    [InlineData("Photos")]
    public async Task A_posted_file_value_goes_to_the_handler_and_is_never_deserialized(string attribute)
    {
        var handler = Substitute.For<ISparkFileValueHandler>();
        var (mapper, _) = CreateMapper(handler);
        var session = Substitute.For<ISparkSession>();
        var doc = new Doc();

        await mapper.PopulateObjectValuesAsync(Posted(attribute), doc, session);

        doc.Scan.Should().BeNull("the forged value must not be copied into the entity");
        doc.Photos.Should().BeEmpty();
        doc.Title.Should().Be("t", "the other attributes still map");
        await handler.Received(1).WriteAsync(
            Arg.Is<SparkFileWriteContext>(c => c.Entity == doc && c.Attribute.Name == attribute
                && c.Property.Name == attribute && c.Definition!.Name == attribute
                && c.EntityTypeName == "Doc" && c.Session == session),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_handler_receives_the_attributes_fileOptions()
    {
        var handler = Substitute.For<ISparkFileValueHandler>();
        var (mapper, _) = CreateMapper(handler);

        await mapper.PopulateObjectValuesAsync(Posted(), new Doc(), Substitute.For<ISparkSession>());

        await handler.Received(1).WriteAsync(
            Arg.Is<SparkFileWriteContext>(c => c.Definition!.FileOptions!.Accept!.Single() == ".pdf"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_the_plugin_a_file_save_throws_instead_of_copying_the_value()
    {
        var (mapper, _) = CreateMapper(handler: null);
        var doc = new Doc();

        var act = () => mapper.PopulateObjectValuesAsync(Posted(), doc, Substitute.For<ISparkSession>());

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*AddFiles()*");
        doc.Scan.Should().BeNull();
    }

    [Fact]
    public void The_synchronous_save_refuses_a_file_attribute_rather_than_dropping_it()
    {
        var (mapper, _) = CreateMapper(Substitute.For<ISparkFileValueHandler>());

        var act = () => mapper.PopulateObjectValues(Posted(), new Doc());

        act.Should().Throw<InvalidOperationException>().WithMessage("*PopulateObjectValuesAsync*");
    }

    [Fact]
    public void ToEntity_is_a_probe_and_leaves_file_attributes_unset()
    {
        var handler = Substitute.For<ISparkFileValueHandler>();
        var (mapper, _) = CreateMapper(handler);

        var doc = mapper.ToEntity<Doc>(Posted());

        doc.Title.Should().Be("t");
        doc.Scan.Should().BeNull();
        handler.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task ToEntityAsync_creates_and_claims_through_the_handler()
    {
        var handler = Substitute.For<ISparkFileValueHandler>();
        var (mapper, _) = CreateMapper(handler);

        var doc = (Doc)await mapper.ToEntityAsync(Posted(), Substitute.For<ISparkSession>());

        doc.Title.Should().Be("t");
        doc.Scan.Should().BeNull();
        await handler.Received(1).WriteAsync(Arg.Any<SparkFileWriteContext>(), Arg.Any<CancellationToken>());
    }

    // --- the swallowed conversion -------------------------------------------

    [Fact]
    public void An_object_the_property_cannot_be_read_from_is_a_validation_error_not_a_silent_skip()
    {
        // Red before #700 M1: the JsonException was caught and the save reported success while the
        // stored value stayed.
        var loader = Substitute.For<IModelLoader>();
        var mapper = new EntityMapper(loader);
        var po = new PersistentObject
        {
            Name = "Holder",
            ObjectTypeId = Guid.NewGuid(),
            Attributes = [new PersistentObjectAttribute { Name = "Inner", Value = JsonDocument.Parse("""{ "Count": "not a number" }""").RootElement }],
        };

        var act = () => mapper.PopulateObjectValues(po, new Holder());

        act.Should().Throw<SparkValidationException>().WithMessage("*Inner*");
    }

    public class Doc
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public SparkFile? Scan { get; set; }
        public List<SparkImage> Photos { get; set; } = [];
    }

    public class DerivedImage : SparkImage;

    public class Holder
    {
        public Counter? Inner { get; set; }
    }

    public class Counter
    {
        public int Count { get; set; }
    }
}
