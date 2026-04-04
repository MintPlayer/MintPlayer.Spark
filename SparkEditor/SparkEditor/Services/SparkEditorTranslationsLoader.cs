using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;

namespace SparkEditor.Services;

internal class SparkEditorTranslationsLoader : ITranslationsLoader
{
    private readonly ISparkEditorFileService fileService;

    public SparkEditorTranslationsLoader(ISparkEditorFileService fileService)
    {
        this.fileService = fileService;
    }

    public Dictionary<string, TranslatedString> GetTranslations()
    {
        var entries = fileService.LoadAllTranslations();
        var result = new Dictionary<string, TranslatedString>();

        foreach (var entry in entries)
        {
            if (entry.Key != null && entry.Values != null)
            {
                result[entry.Key] = entry.Values;
            }
        }

        return result;
    }
}
