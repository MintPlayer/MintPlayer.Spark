using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using System.Text.Json;

namespace MintPlayer.Spark.Services;

public interface IProgramUnitsLoader
{
    ProgramUnitsConfiguration GetProgramUnits();
}

[Register(typeof(IProgramUnitsLoader), ServiceLifetime.Singleton)]
internal partial class ProgramUnitsLoader : IProgramUnitsLoader
{
    [Inject] private readonly IHostEnvironment hostEnvironment;

    private Lazy<ProgramUnitsConfiguration>? _programUnits;

    // The canonical unit types. The loader is the single place that tolerates case — everything
    // above it (the endpoint's rights-per-type switch, the client's router-link mapping) compares
    // these exact strings, so a "Query" unit can't pass the server filter and then silently fail
    // to route on the client.
    internal const string TypeQuery = "query";
    internal const string TypePersistentObject = "persistentObject";
    internal const string TypeUrl = "url";

    private ProgramUnitsConfiguration LoadProgramUnits()
    {
        var filePath = Path.Combine(hostEnvironment.ContentRootPath, "App_Data", "programUnits.json");

        // Fail-soft on absence only: an app without a menu is a valid app. A file that exists but
        // cannot be parsed or validated throws instead — the silent alternative is an empty menu
        // that reads exactly like a rights problem.
        if (!File.Exists(filePath))
            return new ProgramUnitsConfiguration();

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        ProgramUnitsConfiguration config;
        try
        {
            var json = File.ReadAllText(filePath);
            config = JsonSerializer.Deserialize<ProgramUnitsConfiguration>(json, jsonOptions)
                ?? new ProgramUnitsConfiguration();
        }
        catch (JsonException ex)
        {
            throw new SparkProgramUnitsConfigurationException(
                $"App_Data/programUnits.json is not valid JSON: {ex.Message}", ex);
        }

        Validate(config);
        return config;
    }

    private static void Validate(ProgramUnitsConfiguration config)
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
    private static TranslatedString ResolveName(TranslatedString? name, string owner)
    {
        if (name?.Key is not { Length: > 0 } key)
            throw new SparkProgramUnitsConfigurationException(name is { Translations.Count: > 0 }
                ? $"{owner} embeds translated text in its 'name' (\"{name.GetDefaultValue()}\"). " +
                  "Move it into translations.json and set 'name' to that key, e.g. \"programUnits.cars\"."
                : $"{owner} has no 'name'. Set it to a translations.json key, e.g. \"programUnits.cars\".");
        return SparkText.Resolve(name, key, key)!;
    }

    public ProgramUnitsConfiguration GetProgramUnits()
    {
        _programUnits ??= new Lazy<ProgramUnitsConfiguration>(LoadProgramUnits);
        return _programUnits.Value;
    }
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
