using MintPlayer.Spark.ReferenceData;

namespace ReferenceData.Tests.Engine;

/// <summary>
/// RS21: a machine Latin search key for true alphabets (Cyrillic, Greek, Armenian, Georgian), next to the native key,
/// table-driven so every platform gives the same bytes (RS10). Never displayed (Q-R11).
/// </summary>
public class TransliterationTests
{
    [Theory]
    [InlineData("Москва", "москва", "moskva")]
    [InlineData("Αθήνα", "αθηνα", "athina")]
    [InlineData("Երևան", "երևան", "erevan")]
    [InlineData("თბილისი", "თბილისი", "tbilisi")]
    [InlineData("Київ", "київ", "kiyiv")]
    [InlineData("Нижний Новгород", "нижний", "nizhniy")]
    public void A_true_alphabet_name_gets_its_native_and_a_Latin_key(string name, string native, string latin)
    {
        var keys = RefSearchKeys.For([name]);
        keys.KeyVersion.Should().Be(2);
        keys.Tokens.Should().Contain(native);
        keys.Tokens.Should().Contain(latin);
    }

    [Fact]
    public void Latin_names_and_other_scripts_are_left_alone()
    {
        RefSearchKeys.Transliterate("kruishoutem").Should().Be("kruishoutem");
        RefSearchKeys.Transliterate("東京").Should().Be("東京");
        RefSearchKeys.Transliterate("القاهرة").Should().Be("القاهرة");
    }

    [Fact]
    public void A_Latin_query_finds_the_Latin_key_of_a_Cyrillic_name()
        => RefSearchKeys.For(["Санкт-Петербург"]).Compact.Should().Contain(RefSearchKeys.Query("Sankt-Peterburg").Compact);
}
