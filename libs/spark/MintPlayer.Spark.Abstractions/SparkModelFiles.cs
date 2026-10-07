using MintPlayer.Spark.Layering;

namespace MintPlayer.Spark.Abstractions;

/// <summary>One persistent-object type of the composed model: library layers, then the application's file.</summary>
public sealed class SparkComposedType
{
    private readonly SparkModelEntry entry;
    private IReadOnlyDictionary<string, string>? provenance;

    internal SparkComposedType(SparkModelEntry entry, string? appFile)
    {
        this.entry = entry;
        AppFile = appFile;
    }

    /// <summary><c>persistentObject.name</c>; <see langword="null"/> for an application file that names none or does not parse.</summary>
    public string? Name => entry.Name;

    /// <summary>The file as a hash or a message names it: the library's file name for a library type, else the application file's.</summary>
    public string FileName => entry.FileName;

    /// <summary>
    /// The composed <c>Model/*.json</c>, deserialized like any model file. An application file that
    /// cannot be composed is passed through as written, so its reader reports it as before.
    /// </summary>
    public string Json => entry.Json;

    /// <summary>The application's file for this type (the delta, for a library type), or <see langword="null"/>.</summary>
    public string? AppFile { get; }

    /// <summary>The assembly of the library that declares the type; <see langword="null"/> when the application owns it.</summary>
    public string? Library => entry.Library?.Layer;

    /// <summary>The type and where it came from, for messages: <c>SparkUser.json</c> or <c>SparkUser (MintPlayer.Spark.Authorization)</c>.</summary>
    public string Source => Library is { } library
        ? $"{Name} ({library}{(AppFile is null ? string.Empty : $" + {Path.GetFileName(AppFile)}")})"
        : FileName;

    /// <summary>Every layer stating the type, in order: assembly names, then the application file's path.</summary>
    public IReadOnlyList<string> Layers => entry.Inputs.Select(i => i.Layer).ToList();

    /// <summary>Each concrete path (<c>persistentObject.attributes[UserName].showedOn</c>) and the layer that set it (for <c>--spark-describe</c>, D9).</summary>
    public IReadOnlyDictionary<string, string> Provenance
        => provenance ??= entry.Composition?.Provenance ?? new Dictionary<string, string>(StringComparer.Ordinal);

    internal SparkModelEntry Entry => entry;
}

/// <summary>
/// The composed model (composition D6): every referenced library's <c>Model/*.json</c>
/// (<see cref="SparkLayerCatalog"/>, layer order), then the application's
/// <c>App_Data/Model/*.json</c> read from disk. One provider for every reader, so a library type is
/// never copied into the application and no reader can see a different model than the server loads.
/// </summary>
/// <remarks>
/// <para>
/// An application file naming a library type (<c>persistentObject.name</c>) is a delta on it: it
/// states only what differs, and an <c>id</c> it changes is refused (D5). Every other application file
/// is the application's own type.
/// </para>
/// <para>
/// Read on each call: the application layer is the developer's to edit. In the server it is read once,
/// through <c>IModelSource</c>.
/// </para>
/// </remarks>
public static class SparkModelFiles
{
    /// <summary>The composed model of the application at <paramref name="contentRootPath"/>.</summary>
    /// <exception cref="InvalidOperationException">A library layer cannot be read, the application changes a library id, or two libraries disagree.</exception>
    public static IReadOnlyList<SparkComposedType> Compose(string contentRootPath)
        => Compose(SparkLayerCatalog.Libraries, SparkAppData.Path(contentRootPath, "Model"));

    /// <summary>The composed model of <paramref name="libraries"/> and the files in <paramref name="modelDirectory"/>.</summary>
    /// <exception cref="InvalidOperationException">A library layer cannot be read, the application changes a library id, or two libraries disagree.</exception>
    public static IReadOnlyList<SparkComposedType> Compose(IEnumerable<SparkLibrary> libraries, string? modelDirectory)
    {
        var (types, problems) = ComposeLenient(libraries, modelDirectory);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "The model cannot be composed:" + Environment.NewLine
                + string.Join(Environment.NewLine, problems.Select(p => "  - " + p.Message)) + Environment.NewLine
                + $"An application file in {SparkAppData.Relative("Model")} that names a library type is a delta on it: "
                + "state only what differs, and never an id (a library's ids are fixed, composition D5).");
        }
        return types;
    }

    /// <summary>As <see cref="Compose(IEnumerable{SparkLibrary}, string?)"/>, returning what could not be honoured instead of throwing (for the hash gate, which must report rather than crash).</summary>
    internal static (IReadOnlyList<SparkComposedType> Types, IReadOnlyList<SparkLayerError> Problems) ComposeLenient(IEnumerable<SparkLibrary> libraries, string? modelDirectory)
    {
        var libraryInputs = SparkLayerCatalog.Of(libraries, SparkLayerKinds.Model)
            .Select(x => new SparkModelInput(x.Library.AssemblyName, System.IO.Path.GetFileName(x.Layer.Path), x.Layer.Json, isLibrary: true, x.Library.DependsOn))
            .ToList();

        var appInputs = new List<SparkModelInput>();
        if (modelDirectory is not null && Directory.Exists(modelDirectory))
        {
            foreach (var file in Directory.GetFiles(modelDirectory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                var fileName = System.IO.Path.GetFileName(file);
                if (string.Equals(fileName, Model.ModelHashFile.FileName, StringComparison.OrdinalIgnoreCase))
                    continue;
                string json;
                try
                {
                    json = File.ReadAllText(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.WriteLine($"Error loading model file {file}: {ex.Message}");
                    continue;
                }
                appInputs.Add(new SparkModelInput(file, fileName, json, isLibrary: false));
            }
        }

        var composition = SparkModelLayers.Compose(libraryInputs, appInputs);
        var types = composition.Types
            .Select(t => new SparkComposedType(t, t.Inputs.LastOrDefault(i => !i.IsLibrary)?.Layer))
            .ToList();
        return (types, composition.Problems);
    }

    /// <summary>The library model types alone, as a writer compares the application's against them (D6).</summary>
    internal static IReadOnlyList<SparkComposedType> LibraryTypes(IEnumerable<SparkLibrary> libraries)
        => ComposeLenient(libraries, modelDirectory: null).Types;
}
