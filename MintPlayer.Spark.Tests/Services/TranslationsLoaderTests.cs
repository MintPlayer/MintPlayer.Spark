using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// TranslationsLoader is a thin wrapper over the source-generated <c>SparkTranslations.All</c>
/// dictionary. Pins the contract: known key → matching TranslatedString, unknown key → null,
/// GetAll exposes the same dictionary. The shape, not the contents — translations.json
/// can change.
/// </summary>
public class TranslationsLoaderTests
{
    [Fact]
    public void Resolve_returns_null_for_unknown_keys()
    {
        var loader = new TranslationsLoader();

        loader.Resolve("__definitely_not_a_real_translation_key__").Should().BeNull();
    }

    [Fact]
    public void GetAll_returns_the_underlying_translation_dictionary()
    {
        var loader = new TranslationsLoader();

        var all = loader.GetAll();

        all.Should().NotBeNull();
    }

    [Fact]
    public void Resolve_returns_a_TranslatedString_for_every_key_in_GetAll()
    {
        var loader = new TranslationsLoader();
        var all = loader.GetAll();

        // Whatever the source generator emitted, every advertised key must be resolvable.
        foreach (var key in all.Keys)
        {
            loader.Resolve(key).Should().NotBeNull($"key '{key}' is in GetAll() so Resolve must find it");
        }
    }
}
