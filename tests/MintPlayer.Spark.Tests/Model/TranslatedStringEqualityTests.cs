using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Tests.Model;

/// <summary>
/// <see cref="TranslatedString"/> as a value (commerce X17): value equality, the one-line app-text API, and
/// the RFC 5646 tags it accepts. Before X17 equality was reference equality, so every comparison went through
/// JSON (history's <c>SameAt</c>, live diffs).
/// </summary>
public class TranslatedStringEqualityTests
{
    [Fact]
    public void Equal_entries_make_equal_values_with_equal_hash_codes()
    {
        var a = TranslatedString.Of(("en", "Save"), ("nl", "Opslaan"));
        var b = TranslatedString.Of(("en", "Save"), ("nl", "Opslaan"));

        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
        a.Equals((object)b).Should().BeTrue();
    }

    [Fact]
    public void Order_kind_validity_and_key_all_participate()
    {
        var plain = TranslatedString.Of(("en", "Save"), ("nl", "Opslaan"));

        plain.Should().NotBe(TranslatedString.Of(("nl", "Opslaan"), ("en", "Save")), "the author order is the preference order");
        plain.Should().NotBe(new TranslatedString
        {
            Entries = [new TranslatedStringEntry("en", "Save"), new TranslatedStringEntry("nl", "Opslaan") { Kind = TranslatedStringKind.Official }],
        });
        plain.Should().NotBe(new TranslatedString
        {
            Entries = [new TranslatedStringEntry("en", "Save"), new TranslatedStringEntry("nl", "Opslaan") { ValidTo = new DateOnly(2030, 1, 1) }],
        });
        TranslatedString.FromKey("a").Should().NotBe(TranslatedString.FromKey("b"));
        TranslatedString.FromKey("a").Should().Be(TranslatedString.FromKey("a"));
        plain.Equals(null).Should().BeFalse();
    }

    [Fact]
    public void Of_takes_any_language_and_stores_the_canonical_tag()
    {
        var ts = TranslatedString.Of(("EN", "Save"), ("zh-hant", "儲存"), ("sr-latn-rs", "Sačuvaj"));

        ts.Entries.Select(e => e.Tag).Should().Equal(["en", "zh-Hant", "sr-Latn-RS"]);
        TranslatedString.Of("pt-BR", "Salvar")["pt-br"].Should().Be("Salvar", "the indexer compares tags case-insensitively");
    }

    [Fact]
    public void Of_refuses_an_ill_formed_tag()
    {
        var act = () => TranslatedString.Of("en_US", "Save");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Set_replaces_the_plain_entry_in_place_and_leaves_names_with_a_kind()
    {
        var ts = new TranslatedString
        {
            Entries =
            [
                new TranslatedStringEntry("en", "Old"),
                new TranslatedStringEntry("nl", "Officieel") { Kind = TranslatedStringKind.Official },
                new TranslatedStringEntry("nl", "Oud"),
                new TranslatedStringEntry("fr", "Vieux"),
                new TranslatedStringEntry("nl", "Dubbel"),
            ],
        };

        ts.Set("nl", "Nieuw");

        ts.Entries.Select(e => $"{e.Tag}={e.Value}").Should().Equal(["en=Old", "nl=Officieel", "nl=Nieuw", "fr=Vieux"],
            "the first plain nl entry is replaced where it stands and the other plain nl entry is dropped");
    }

    [Fact]
    public void The_indexer_reads_and_writes_the_plain_text_of_one_tag()
    {
        var ts = TranslatedString.Of("en", "Save");

        ts["nl"] = "Opslaan";
        ts["nl"].Should().Be("Opslaan");
        ts["de"].Should().BeNull("the indexer does no fallback");

        ts["nl"] = null;
        ts.Tags.Should().Equal(["en"]);
    }

    [Fact]
    public void Map_transforms_every_text_and_keeps_tags_and_kinds()
    {
        var template = new TranslatedString
        {
            Entries = [new TranslatedStringEntry("en", "{0} saved"), new TranslatedStringEntry("nl", "{0} bewaard") { Kind = TranslatedStringKind.Common }],
        };

        var formatted = template.Map((tag, text) => string.Format(text, tag.ToUpperInvariant()));

        formatted.Should().Be(new TranslatedString
        {
            Entries = [new TranslatedStringEntry("en", "EN saved"), new TranslatedStringEntry("nl", "NL bewaard") { Kind = TranslatedStringKind.Common }],
        });
        template["en"].Should().Be("{0} saved", "Map returns a new value");
    }

    [Fact]
    public void A_clone_is_equal_but_independent()
    {
        var original = TranslatedString.Of(("en", "Save"), ("nl", "Opslaan"));
        var clone = original.Clone();

        clone.Should().Be(original);
        clone.Set("nl", "Bewaren");
        original["nl"].Should().Be("Opslaan");
    }

    [Theory]
    [InlineData("en", "en")]
    [InlineData("nl-be", "nl-BE")]
    [InlineData("ZH-HANT", "zh-Hant")]
    [InlineData("sr-latn-rs", "sr-Latn-RS")]
    [InlineData("de-ch-1901", "de-CH-1901")]
    [InlineData("es-419", "es-419")]
    [InlineData("x-private", "x-private")]
    [InlineData("en-a-bbb-x-ccc", "en-a-bbb-x-ccc")]
    [InlineData("EN-A-BB-X-CC", "en-a-bb-x-cc")]
    [InlineData("zh-yue-HK", "zh-yue-HK")]
    public void Well_formed_tags_canonicalize(string tag, string canonical)
    {
        LanguageTag.IsWellFormed(tag).Should().BeTrue();
        LanguageTag.Canonicalize(tag).Should().Be(canonical);
    }

    [Theory]
    [InlineData("")]
    [InlineData("e")]
    [InlineData("en-")]
    [InlineData("en--US")]
    [InlineData("en_US")]
    [InlineData("toolongtag1")]
    [InlineData("en-a-bb-a-cc")]   // a singleton may appear once
    [InlineData("en-a")]           // a singleton needs a subtag
    [InlineData("x")]              // private use needs a subtag
    [InlineData("1en")]
    [InlineData("en US")]
    public void Ill_formed_tags_are_refused(string tag)
    {
        LanguageTag.IsWellFormed(tag).Should().BeFalse();
        var act = () => LanguageTag.Canonicalize(tag);
        act.Should().Throw<ArgumentException>();
    }
}
