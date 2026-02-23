using System.Text.Json;
using Microsoft.Extensions.Hosting;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Services;

public interface ITranslationsLoader
{
    Dictionary<string, TranslatedString> GetTranslations();
}

[Register(typeof(ITranslationsLoader), ServiceLifetime.Singleton)]
internal partial class TranslationsLoader : ITranslationsLoader
{
    [Inject] private readonly IHostEnvironment hostEnvironment;

    private Lazy<Dictionary<string, TranslatedString>>? _translations;

    private Dictionary<string, TranslatedString> LoadTranslations()
    {
        var filePath = Path.Combine(hostEnvironment.ContentRootPath, "App_Data", "translations.json");

        if (!File.Exists(filePath))
            return new Dictionary<string, TranslatedString>();

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        try
        {
            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<Dictionary<string, TranslatedString>>(json, jsonOptions)
                ?? new Dictionary<string, TranslatedString>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading translations file: {ex.Message}");
            return new Dictionary<string, TranslatedString>();
        }
    }

    public Dictionary<string, TranslatedString> GetTranslations()
    {
        _translations ??= new Lazy<Dictionary<string, TranslatedString>>(LoadTranslations);
        return _translations.Value;
    }
}
