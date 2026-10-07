using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Services;

/// <summary>
/// The composed model (composition D6): the referenced libraries' model layers, then the application's
/// <c>App_Data/Model/*.json</c>. Every reader of the model goes through this, or through
/// <see cref="SparkModelFiles"/> where there is no service provider (the CLI verbs).
/// </summary>
public interface IModelSource
{
    /// <summary>Every type, library types first (in layer order), then the application's own.</summary>
    /// <exception cref="InvalidOperationException">On first use, when the layers cannot be composed (startup).</exception>
    IReadOnlyList<SparkComposedType> Types { get; }
}

[Register(typeof(IModelSource), ServiceLifetime.Singleton)]
internal partial class ModelSource : IModelSource
{
    [Inject] private readonly IHostEnvironment hostEnvironment;

    private Lazy<IReadOnlyList<SparkComposedType>>? types;

    /// <summary>The libraries to compose; <see langword="null"/>: the process's (<see cref="SparkLayerCatalog"/>).</summary>
    private IReadOnlyList<SparkLibrary>? libraries;

    /// <summary>
    /// A source over chosen libraries (<c>[]</c>: the application's files alone), for a test that must
    /// not depend on which layered libraries its process happens to reference.
    /// </summary>
    internal static ModelSource For(IHostEnvironment hostEnvironment, IReadOnlyList<SparkLibrary> libraries)
        => new(hostEnvironment) { libraries = libraries };

    // Read once, like the loader it feeds: a uniform reload of the application layer is M7 (D8).
    public IReadOnlyList<SparkComposedType> Types
        => (types ??= new Lazy<IReadOnlyList<SparkComposedType>>(() => SparkModelFiles.Compose(
            libraries ?? SparkLayerCatalog.Libraries,
            SparkAppData.Path(hostEnvironment.ContentRootPath, "Model")))).Value;
}
