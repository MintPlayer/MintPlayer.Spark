using System.Text.Json;
using Microsoft.Extensions.Hosting;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Services;

public interface IProgramUnitsLoader
{
    ProgramUnitsConfiguration GetProgramUnits();
}

[Register(typeof(IProgramUnitsLoader), ServiceLifetime.Singleton, "AddSparkServices")]
internal partial class ProgramUnitsLoader : IProgramUnitsLoader
{
    [Inject] private readonly IHostEnvironment hostEnvironment;

    private readonly Lazy<ProgramUnitsConfiguration> _programUnits;

    public ProgramUnitsLoader()
    {
        _programUnits = new Lazy<ProgramUnitsConfiguration>(LoadProgramUnits);
    }

    private ProgramUnitsConfiguration LoadProgramUnits()
    {
        var filePath = Path.Combine(hostEnvironment.ContentRootPath, "App_Data", "programUnits.json");

        if (!File.Exists(filePath))
            return new ProgramUnitsConfiguration();

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        try
        {
            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<ProgramUnitsConfiguration>(json, jsonOptions)
                ?? new ProgramUnitsConfiguration();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading program units file: {ex.Message}");
            return new ProgramUnitsConfiguration();
        }
    }

    public ProgramUnitsConfiguration GetProgramUnits()
        => _programUnits.Value;
}
