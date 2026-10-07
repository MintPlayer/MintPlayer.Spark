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
}
