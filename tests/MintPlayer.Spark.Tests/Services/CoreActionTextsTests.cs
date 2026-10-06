using System.Reflection;
using System.Text.Json.Nodes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// The core layer's action texts come from the core library's own <c>translations.json</c> (#467, D7/D8).
/// <para>
/// A host gets them through its generated aggregator, which composes every referenced library's
/// <c>[assembly: SparkLayer]</c> translations layer into <see cref="SparkTranslations"/> at startup. This test
/// assembly is a library, so nothing registers them here: the test composes the core library's layer
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

    /// <summary>A library's translations as its <c>[assembly: SparkLayer]</c> of kind translations carries them, flattened.</summary>
    private static Dictionary<string, TranslatedString> LibraryTranslations(Assembly library)
    {
        var all = new Dictionary<string, TranslatedString>(StringComparer.Ordinal);
        foreach (var layer in library.GetCustomAttributes<SparkLayerAttribute>().Where(l => l.Kind == "translations"))
            Flatten(JsonNode.Parse(layer.Json)!.AsObject(), "", all);
        all.Should().NotBeEmpty($"{library.GetName().Name} ships its translations as assembly attributes");
        return all;
    }

    /// <summary>A leaf holds only strings (language → text); a namespace only objects. <c>$</c>/<c>_</c> keys are annotations.</summary>
    private static void Flatten(JsonObject node, string prefix, Dictionary<string, TranslatedString> into)
    {
        var members = node.Where(m => !m.Key.StartsWith('$') && !m.Key.StartsWith('_')).ToList();
        if (members.Count > 0 && members.All(m => m.Value is JsonValue))
        {
            into[prefix] = new TranslatedString { Translations = members.ToDictionary(m => m.Key, m => m.Value!.GetValue<string>()) };
            return;
        }

        foreach (var (key, value) in members)
            if (value is JsonObject child)
                Flatten(child, prefix.Length == 0 ? key : $"{prefix}.{key}", into);
    }
}

[CollectionDefinition(nameof(SparkTranslationsRegistryCollection), DisableParallelization = true)]
public sealed class SparkTranslationsRegistryCollection;
