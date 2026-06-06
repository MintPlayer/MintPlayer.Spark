using MintPlayer.Spark.Helpers;

namespace MintPlayer.Spark.Tests.Helpers;

/// <summary>
/// Pins the simple English pluralization rules in <see cref="CollectionHelper"/>. The
/// helper is what RavenDB's collection-name conventions for Spark documents derive
/// from, so a regression here renames every collection — silent breakage.
/// </summary>
public class CollectionHelperTests
{
    private readonly CollectionHelper _helper = new();

    [Theory]
    [InlineData("MyApp.Models.Person", "Persons")]      // base + "s"
    [InlineData("Person", "Persons")]                    // unqualified name → still pluralized
    [InlineData("Cat", "Cats")]
    [InlineData("Dog", "Dogs")]
    public void Default_singular_word_appends_s(string clr, string expected)
        => _helper.GetCollectionName(clr).Should().Be(expected);

    [Theory]
    [InlineData("Country", "Countries")]   // consonant + y → ies
    [InlineData("City", "Cities")]
    [InlineData("Property", "Properties")]
    public void Word_ending_in_consonant_then_y_replaces_with_ies(string clr, string expected)
        => _helper.GetCollectionName(clr).Should().Be(expected);

    [Theory]
    [InlineData("Day", "Days")]      // vowel + y → just s (Day is "ay" → not _ies_)
    [InlineData("Boy", "Boys")]
    [InlineData("Key", "Keys")]
    public void Word_ending_in_vowel_then_y_just_appends_s(string clr, string expected)
        => _helper.GetCollectionName(clr).Should().Be(expected);

    [Theory]
    [InlineData("Bus", "Buses")]      // s → es
    [InlineData("Box", "Boxes")]      // x → es
    [InlineData("Watch", "Watches")]  // ch → es
    [InlineData("Dish", "Dishes")]    // sh → es
    public void Word_ending_in_sibilant_appends_es(string clr, string expected)
        => _helper.GetCollectionName(clr).Should().Be(expected);

    [Theory]
    [InlineData("My.Namespace.User", "Users")]
    [InlineData("Deeply.Nested.Namespace.With.Many.Dots.City", "Cities")]
    public void Strips_namespace_dots_keeping_only_the_class_name_segment(string clr, string expected)
        => _helper.GetCollectionName(clr).Should().Be(expected);

    [Fact]
    public void Empty_class_segment_returns_empty()
        => _helper.GetCollectionName("").Should().BeEmpty();

    [Theory]
    [InlineData("y", "ys")]   // single-char "y" — Pluralize bails on length<=1, falls through to +"s"
    public void Single_letter_y_is_not_treated_as_consonant_plus_y(string clr, string expected)
        => _helper.GetCollectionName(clr).Should().Be(expected);
}
