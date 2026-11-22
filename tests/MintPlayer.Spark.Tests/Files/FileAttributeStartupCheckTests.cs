using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Files;

/// <summary>
/// #700 D2b: file attributes are checked when <c>UseSpark()</c> runs, naming every problem, instead of
/// failing on the first save.
/// </summary>
public class FileAttributeStartupCheckTests
{
    private static IModelLoader Loader(Type clrType, params EntityAttributeDefinition[] attributes)
    {
        var loader = Substitute.For<IModelLoader>();
        loader.GetEntityTypes().Returns([new EntityTypeDefinition
        {
            Id = Guid.NewGuid(), Name = clrType.Name, ClrType = clrType.FullName!, Attributes = attributes,
        }]);
        return loader;
    }

    private static EntityAttributeDefinition Attr(string name, string dataType, bool isArray = false, SparkFileOptions? options = null)
        => new() { Id = Guid.NewGuid(), Name = name, DataType = dataType, IsArray = isArray, FileOptions = options };

    [Fact]
    public void A_stored_file_attribute_without_AddFiles_is_refused_naming_it()
    {
        var problems = SparkFileAttributeStartupCheck.Unmet(Loader(typeof(Car), Attr("Photos", "Image", isArray: true)), filesEnabled: false);

        problems.Should().ContainSingle().Which.Should().Contain("'Car.Photos'").And.Contain("spark.AddFiles()");
    }

    [Fact]
    public void With_AddFiles_the_same_model_starts()
        => SparkFileAttributeStartupCheck.Unmet(Loader(typeof(Car), Attr("Photos", "Image", isArray: true)), filesEnabled: true)
            .Should().BeEmpty();

    [Fact]
    public void A_url_string_Image_needs_no_plugin()
    {
        // A string Image is displayed, never stored: CodeCoverage's AvatarUrl must not require AddFiles().
        SparkFileAttributeStartupCheck.Unmet(Loader(typeof(Car), Attr("LogoUrl", "Image")), filesEnabled: false)
            .Should().BeEmpty();
    }

    [Fact]
    public void A_malformed_fileOptions_is_refused_at_startup()
    {
        var problems = SparkFileAttributeStartupCheck.Unmet(
            Loader(typeof(Car), Attr("Photos", "Image", isArray: true, new SparkFileOptions { Accept = ["*"] })),
            filesEnabled: true);

        problems.Should().ContainSingle().Which.Should().Contain("Car.Photos").And.Contain("wildcard");
    }

    [Fact]
    public void A_file_inside_an_AsDetail_row_starts()
    {
        // M10 (owner, 2026-11-22): one AsDetail level is bound to the document and Parent.Field.
        SparkFileAttributeStartupCheck.Unmet(Loader(typeof(Garage), Attr("Bays", "AsDetail", isArray: true)), filesEnabled: true)
            .Should().BeEmpty();
    }

    [Fact]
    public void A_file_inside_an_AsDetail_row_of_an_AsDetail_row_is_refused()
    {
        var problems = SparkFileAttributeStartupCheck.Unmet(Loader(typeof(Campus), Attr("Garages", "AsDetail", isArray: true)), filesEnabled: true);

        problems.Should().ContainSingle().Which.Should().Contain("'Campus.Garages'").And.Contain("Bay.Sign");
    }

    public class Campus
    {
        public string? Id { get; set; }
        public List<Garage> Garages { get; set; } = [];
    }

    public class Car
    {
        public string? Id { get; set; }
        public List<SparkImage> Photos { get; set; } = [];
        public string? LogoUrl { get; set; }
    }

    public class Garage
    {
        public string? Id { get; set; }
        public List<Bay> Bays { get; set; } = [];
    }

    public class Bay
    {
        public string? Number { get; set; }
        public SparkImage? Sign { get; set; }
    }
}
