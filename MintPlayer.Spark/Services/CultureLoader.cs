using System.Text.Json;
using Microsoft.Extensions.Hosting;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Services;

public interface ICultureLoader
{
    CultureConfiguration GetCulture();
}

[Register(typeof(ICultureLoader), ServiceLifetime.Singleton)]
internal partial class CultureLoader : ICultureLoader
{
    [Inject] private readonly IHostEnvironment hostEnvironment;

    private Lazy<CultureConfiguration>? _culture;

    private CultureConfiguration LoadCulture()
    {
        var filePath = Path.Combine(hostEnvironment.ContentRootPath, "App_Data", "culture.json");

        if (!File.Exists(filePath))
            return new CultureConfiguration();

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        try
        {
            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<CultureConfiguration>(json, jsonOptions)
                ?? new CultureConfiguration();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading culture file: {ex.Message}");
            return new CultureConfiguration();
        }
    }

    public CultureConfiguration GetCulture()
    {
        _culture ??= new Lazy<CultureConfiguration>(LoadCulture);
        return _culture.Value;
    }
}
