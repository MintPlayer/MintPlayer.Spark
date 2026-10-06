using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using System.Text.Json;

namespace MintPlayer.Spark.Services;

public interface IProgramUnitsLoader
{
    ProgramUnitsConfiguration GetProgramUnits();
}

/// <summary>
/// The menu: the libraries' <c>programUnits.json</c> layers with the application's on top
/// (<see cref="SparkProgramUnitsFiles"/>, composition D3), names resolved against the translations.
/// Reloaded when the application's file changes and when the translations do, through the one watcher
/// policy (<see cref="AppLayerSnapshot{T}"/>, D8); a reload that does not compose keeps the previous menu.
/// </summary>
[Register(typeof(IProgramUnitsLoader), ServiceLifetime.Singleton)]
internal partial class ProgramUnitsLoader : IProgramUnitsLoader, IDisposable
{
    [Inject] private readonly IHostEnvironment hostEnvironment;
    [Inject] private readonly ITranslationsLoader translationsLoader;
    [Inject] private readonly ILogger<ProgramUnitsLoader> logger;

    private AppLayerSnapshot<ProgramUnitsConfiguration>? layer;

    // The canonical unit types. The loader is the single place that tolerates case — everything
    // above it (the endpoint's rights-per-type switch, the client's router-link mapping) compares
    // these exact strings, so a "Query" unit can't pass the server filter and then silently fail
    // to route on the client.
    internal const string TypeQuery = "query";
    internal const string TypePersistentObject = "persistentObject";
    internal const string TypeUrl = "url";

    private AppLayerSnapshot<ProgramUnitsConfiguration> Layer
        => LazyInitializer.EnsureInitialized(ref layer, () => new(
            SparkProgramUnitsFiles.AppLayerName,
            Path.GetDirectoryName(SparkProgramUnitsFiles.PathFor(hostEnvironment.ContentRootPath)),
            ["programUnits.json"],
            LoadProgramUnits,
            logger,
            labels: translationsLoader));

    private ProgramUnitsConfiguration LoadProgramUnits()
    {
        var filePath = SparkProgramUnitsFiles.PathFor(hostEnvironment.ContentRootPath);

        // Fail-soft on absence only: an app without a menu is a valid app. A file that exists but
        // cannot be parsed or validated throws instead — the silent alternative is an empty menu
        // that reads exactly like a rights problem.
        string? composed;
        try
        {
            composed = SparkProgramUnitsFiles.Compose(File.Exists(filePath) ? File.ReadAllText(filePath) : null);
        }
        catch (InvalidOperationException ex)
        {
            throw new SparkProgramUnitsConfigurationException(ex.Message, ex);
        }
        if (composed is null)
            return new ProgramUnitsConfiguration();

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        ProgramUnitsConfiguration config;
        try
        {
            config = JsonSerializer.Deserialize<ProgramUnitsConfiguration>(composed, jsonOptions)
                ?? new ProgramUnitsConfiguration();
        }
        catch (JsonException ex)
        {
            throw new SparkProgramUnitsConfigurationException(
                $"{SparkProgramUnitsFiles.AppLayerName} is not valid JSON: {ex.Message}", ex);
        }

        Validate(config);
        return config;
    }

    private void Validate(ProgramUnitsConfiguration config)
    {
        foreach (var group in config.ProgramUnitGroups)
        {
            group.Name = ResolveName(group.Name, $"Program unit group '{group.Id}'");
            foreach (var unit in group.ProgramUnits)
            {
                unit.Name = ResolveName(unit.Name, $"Program unit '{unit.Id}'");
                unit.Type = unit.Type switch
                {
                    _ when string.Equals(unit.Type, TypeQuery, StringComparison.OrdinalIgnoreCase) => TypeQuery,
                    _ when string.Equals(unit.Type, TypePersistentObject, StringComparison.OrdinalIgnoreCase) => TypePersistentObject,
                    _ when string.Equals(unit.Type, TypeUrl, StringComparison.OrdinalIgnoreCase) => TypeUrl,
                    _ => throw new SparkProgramUnitsConfigurationException(
                        $"Program unit '{unit.Id}' declares unknown type '{unit.Type}'. " +
                        $"Valid types are '{TypeQuery}', '{TypePersistentObject}' and '{TypeUrl}'."),
                };

                switch (unit.Type)
                {
                    case TypeQuery when unit.QueryId is null:
                        throw new SparkProgramUnitsConfigurationException(
                            $"Program unit '{unit.Id}' has type '{TypeQuery}' but no 'queryId'.");
                    case TypePersistentObject when unit.PersistentObjectId is null:
                        throw new SparkProgramUnitsConfigurationException(
                            $"Program unit '{unit.Id}' has type '{TypePersistentObject}' but no 'persistentObjectId'.");
                    case TypeUrl when string.IsNullOrWhiteSpace(unit.Url):
                        throw new SparkProgramUnitsConfigurationException(
                            $"Program unit '{unit.Id}' has type '{TypeUrl}' but no 'url'.");
                }
            }
        }
    }

    /// <summary>
    /// A menu entry's <c>name</c> is a <c>translations.json</c> key (#467, D1), e.g.
    /// <c>"programUnits.cars"</c>; its last segment, humanized, is shown when no layer translates it.
    /// </summary>
    private TranslatedString ResolveName(TranslatedString? name, string owner)
    {
        if (name?.Key is not { Length: > 0 } key)
            throw new SparkProgramUnitsConfigurationException(name is { Translations.Count: > 0 }
                ? $"{owner} embeds translated text in its 'name' (\"{name.GetDefaultValue()}\"). " +
                  "Move it into translations.json and set 'name' to that key, e.g. \"programUnits.cars\"."
                : $"{owner} has no 'name'. Set it to a translations.json key, e.g. \"programUnits.cars\".");
        return SparkText.Resolve(translationsLoader.GetAll(), name, key, key)!;
    }

    public ProgramUnitsConfiguration GetProgramUnits() => Layer.Current;

    [NoInterfaceMember]
    public void Dispose() => layer?.Dispose();
}

/// <summary>
/// Thrown when <c>App_Data/programUnits.json</c> exists but cannot be trusted — unparseable, an
/// unknown unit type, or a unit missing the field its type requires. Loud on purpose: the fail-soft
/// alternative is a menu that silently drops entries, which reads exactly like an authorization
/// problem and gets debugged as one. A missing file stays fail-soft (no menu is a valid choice).
/// </summary>
public sealed class SparkProgramUnitsConfigurationException : Exception
{
    public SparkProgramUnitsConfigurationException(string message) : base(message) { }
    public SparkProgramUnitsConfigurationException(string message, Exception inner) : base(message, inner) { }
}
