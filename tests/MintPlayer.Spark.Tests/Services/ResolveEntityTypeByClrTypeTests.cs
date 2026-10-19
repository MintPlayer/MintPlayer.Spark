using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// D28: Contributions seeds the target type into its renderer options (<c>targetType</c>, the query's
/// <c>parentType</c>; <c>compareType</c>) when the model name is not known yet. It seeded <c>typeof(T).Name</c>
/// (<c>ContributionDescriptor.cs:43,81</c>), which names nothing once the type is called otherwise; it now seeds the
/// CLR type's full name, which <see cref="IModelLoader.ResolveEntityType"/> resolves after id, alias and name.
/// </summary>
public sealed class ResolveEntityTypeByClrTypeTests : IDisposable
{
    private readonly string contentRoot = Path.Combine(Path.GetTempPath(), "spark-resolve-clr-" + Guid.NewGuid().ToString("N"));

    public ResolveEntityTypeByClrTypeTests()
    {
        var model = Path.Combine(contentRoot, "App_Data", "Model");
        Directory.CreateDirectory(model);
        File.WriteAllText(Path.Combine(model, "Hymn.json"), """
            { "persistentObject": { "id": "5e1e0000-0000-4000-8000-000000000001", "name": "Hymn", "clrType": "Acme.Music.Song", "attributes": [] } }
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(contentRoot, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void A_type_named_otherwise_than_its_class_resolves_by_its_clr_type()
    {
        var hostEnvironment = Substitute.For<IHostEnvironment>();
        hostEnvironment.ContentRootPath.Returns(contentRoot);
        var loader = new ModelLoader(ModelSource.For(hostEnvironment, []), TranslationsLoader.For(hostEnvironment, []), NullLogger<ModelLoader>.Instance);

        loader.ResolveEntityType("Acme.Music.Song")!.Name.Should().Be("Hymn");
        loader.ResolveEntityType("Song").Should().BeNull("the class's simple name is not a model name");
        loader.ResolveEntityType("Hymn")!.Name.Should().Be("Hymn");
    }
}
