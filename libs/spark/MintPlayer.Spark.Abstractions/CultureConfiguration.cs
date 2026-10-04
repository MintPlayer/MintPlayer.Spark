namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// The languages an app offers, as served to clients: code → display name. <c>App_Data/culture.json</c>
/// lists only the codes (<c>"languages": ["en", "fr", "nl"]</c>); each name is resolved from the
/// <c>translations.json</c> key <c>culture.languages.{code}</c> (#467, D1).
/// </summary>
public sealed class CultureConfiguration
{
    public Dictionary<string, TranslatedString> Languages { get; set; } = new()
    {
        ["en"] = TranslatedString.Create("English")
    };
    public string DefaultLanguage { get; set; } = "en";
}
