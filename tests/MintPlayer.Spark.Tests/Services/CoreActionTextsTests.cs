using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// The core layer's action texts come from the core library's own <c>translations.json</c> (#467, D7/D8),
/// which reaches an application as an <c>[assembly: SparkLayer]</c> translations layer and is composed at
/// run time (composition D10). The test composes the core assembly's layer as the host's
/// <c>ITranslationsLoader</c> does, and hands it to the catalogue.
/// </summary>
public class CoreActionTextsTests
{
    [Fact]
    public void The_core_texts_come_from_the_core_translations()
    {
        var translations = SparkTranslations.Compose(SparkTranslations.Discover([typeof(ActionsCatalogueLoader).Assembly])).All;
        translations.Should().NotBeEmpty("MintPlayer.Spark ships its translations as assembly attributes");

        var catalogue = ActionsCatalogueLoader.Build(null, SparkActionLayers.Libraries, translations);

        catalogue.Find("Edit")!.Label.GetValue("nl").Should().Be("Bewerken");
        catalogue.Find("Delete")!.Confirmation!.GetValue("en").Should().Contain("{count}");
    }

    /// <summary>
    /// #268 M5 follow-up: the nested <c>"spark": { "query": { "unavailable" } }</c> group of the core translations
    /// reaches the composed set (what <c>GET /spark/translations</c> serves) under the dotted keys the client asks
    /// for, <c>spark.query.unavailable</c> and <c>spark.po.unavailable</c>, in all three languages.
    /// </summary>
    [Fact]
    public void The_nested_spark_group_is_served_under_dotted_keys()
    {
        var translations = SparkTranslations.Compose(SparkTranslations.Discover([typeof(ActionsCatalogueLoader).Assembly])).All;

        translations["spark.query.unavailable"].GetValue("en").Should().Be("This list is not available.");
        translations["spark.query.unavailable"].GetValue("nl").Should().Be("Deze lijst is niet beschikbaar.");
        translations["spark.po.unavailable"].GetValue("fr").Should().Be("Cet élément a été supprimé ou n'est plus disponible.");
        translations.Keys.Should().NotContain("spark");
    }
}
