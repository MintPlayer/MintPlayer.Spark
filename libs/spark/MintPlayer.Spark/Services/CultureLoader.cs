using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using System.Text.Json;

namespace MintPlayer.Spark.Services;

public interface ICultureLoader
{
    CultureConfiguration GetCulture();
}

/// <summary>
/// The application's <c>culture.json</c> (app-only: libraries may not ship it, composition D3), each
/// language's name resolved against the translations. Reloaded when the file changes and when the
/// translations do, through the one watcher policy (<see cref="AppLayerSnapshot{T}"/>, D8).
/// </summary>
[Register(typeof(ICultureLoader), ServiceLifetime.Singleton)]
internal partial class CultureLoader : ICultureLoader, IDisposable
{
    [Inject] private readonly IHostEnvironment hostEnvironment;
    [Inject] private readonly ITranslationsLoader translationsLoader;
    [Inject] private readonly ILogger<CultureLoader> logger;

    private AppLayerSnapshot<CultureConfiguration>? layer;

    private AppLayerSnapshot<CultureConfiguration> Layer
        => LazyInitializer.EnsureInitialized(ref layer, () => new(
            SparkAppData.Relative("culture.json"),
            SparkAppData.Directory(hostEnvironment.ContentRootPath),
            ["culture.json"],
            LoadCulture,
            logger,
            labels: translationsLoader));

    private CultureConfiguration LoadCulture()
    {
        var filePath = SparkAppData.Path(hostEnvironment.ContentRootPath, "culture.json");

        if (!File.Exists(filePath))
            return Build(["en"], "en");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(filePath));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Error loading culture file: {ex.Message}");
            return Build(["en"], "en");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Build(["en"], "en");

            var codes = new List<string>();
            string defaultLanguage = "en";
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "languages", StringComparison.OrdinalIgnoreCase))
                {
                    // #467, D1: culture.json lists codes; each language's name is the translations.json key
                    // culture.languages.{code}. The old object form embedded the names, so it is refused.
                    if (property.Value.ValueKind != JsonValueKind.Array)
                        throw new InvalidOperationException(
                            $"{SparkAppData.Relative("culture.json")}: 'languages' must be an array of language codes, e.g. " +
                            "[\"en\", \"fr\", \"nl\"]. A language's display name is the translations.json key " +
                            "'culture.languages.{code}'.");
                    codes.AddRange(property.Value.EnumerateArray()
                        .Select(e => e.GetString())
                        .Where(c => !string.IsNullOrWhiteSpace(c))
                        .Select(c => c!));
                }
                else if (string.Equals(property.Name, "defaultLanguage", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String)
                {
                    defaultLanguage = property.Value.GetString() ?? "en";
                }
            }

            return Build(codes.Count > 0 ? codes : ["en"], defaultLanguage);
        }
    }

    private CultureConfiguration Build(IEnumerable<string> codes, string defaultLanguage) => new()
    {
        Languages = codes.ToDictionary(
            code => code,
            // An untranslated language shows its code as-is ("pt"), not humanized.
            code => SparkText.Lookup(translationsLoader.GetAll(), $"culture.languages.{code}") ?? TranslatedString.Create(code)),
        DefaultLanguage = defaultLanguage,
    };

    public CultureConfiguration GetCulture() => Layer.Current;

    [NoInterfaceMember]
    public void Dispose() => layer?.Dispose();
}
