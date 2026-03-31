namespace MintPlayer.Spark.Abstractions;

public sealed class CultureConfiguration
{
    public Dictionary<string, TranslatedString> Languages { get; set; } = new()
    {
        ["en"] = TranslatedString.Create("English")
    };
    public string DefaultLanguage { get; set; } = "en";
}
