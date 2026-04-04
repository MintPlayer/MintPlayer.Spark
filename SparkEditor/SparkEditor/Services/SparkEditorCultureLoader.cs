using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;

namespace SparkEditor.Services;

internal class SparkEditorCultureLoader : ICultureLoader
{
    private readonly ISparkEditorFileService fileService;

    public SparkEditorCultureLoader(ISparkEditorFileService fileService)
    {
        this.fileService = fileService;
    }

    public CultureConfiguration GetCulture()
    {
        var languages = fileService.LoadAllLanguages();

        var config = new CultureConfiguration();
        config.Languages.Clear();

        foreach (var lang in languages)
        {
            config.Languages[lang.Culture] = lang.Name ?? TranslatedString.Create(lang.Culture);
        }

        if (languages.Count > 0)
        {
            config.DefaultLanguage = languages[0].Culture;
        }

        return config;
    }
}
