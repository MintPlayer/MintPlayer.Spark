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
            foreach (var unit in group.ProgramUnits)
            {
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
