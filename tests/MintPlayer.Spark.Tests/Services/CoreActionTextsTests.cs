using System.Reflection;
using System.Text.Json;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// The core layer's action texts come from the core library's own <c>translations.json</c> (#467, D7/D8).
/// <para>
/// A host gets them through its generated aggregator, which composes every referenced library's
/// <c>[assembly: SparkTranslations]</c> chunks into <see cref="SparkTranslations"/> at startup. This test
/// assembly is a library, so nothing registers them here: the test composes the core library's chunks
/// itself, exactly as the aggregator does, and restores the registry after. The registry is process-wide,
/// so the collection runs alone.
/// </para>
/// </summary>
[Collection(nameof(SparkTranslationsRegistryCollection))]
public class CoreActionTextsTests
{
    [Fact]
    public void The_core_texts_come_from_the_core_translations()
    {
        var previous = SparkTranslations.All;
        try
        {
            SparkTranslations.Register(LibraryTranslations(typeof(ActionsCatalogueLoader).Assembly));

            var catalogue = ActionsCatalogueLoader.Build(null, SparkActionLayers.Libraries);

            catalogue.Find("Edit")!.Label.GetValue("nl").Should().Be("Bewerken");
            catalogue.Find("Delete")!.Confirmation!.GetValue("en").Should().Contain("{count}");
        }
        finally
        {
            SparkTranslations.Register(previous);
        }
    }

    /// <summary>A library's translations as its <c>[assembly: SparkTranslations]</c> chunks carry them.</summary>
    private static Dictionary<string, TranslatedString> LibraryTranslations(Assembly library)
    {
        var all = new Dictionary<string, TranslatedString>(StringComparer.Ordinal);
        foreach (var chunk in library.GetCustomAttributes<SparkTranslationsAttribute>().OrderBy(a => a.ChunkIndex))
        {
            var entries = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(chunk.Json)!;
            foreach (var (key, languages) in entries)
                all[key] = new TranslatedString { Translations = languages };
        }
        all.Should().NotBeEmpty($"{library.GetName().Name} ships its translations as assembly attributes");
        return all;
    }
}

[CollectionDefinition(nameof(SparkTranslationsRegistryCollection), DisableParallelization = true)]
public sealed class SparkTranslationsRegistryCollection;
