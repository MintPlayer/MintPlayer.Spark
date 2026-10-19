using System.Text.Json;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Converters;
using Newtonsoft.Json;

namespace MintPlayer.Spark.Tests.Converters;

/// <summary>
/// The two <see cref="TranslatedString"/> converters (commerce X17): System.Text.Json on the API and the
/// internal Newtonsoft one RavenDB stores through. They share one codec (<see cref="TranslatedStringJson"/>),
/// and these pin that they write the same bytes and read each other's output.
/// </summary>
public class TranslatedStringJsonConverterTests
{
    private static readonly JsonSerializerSettings NewtonsoftSettings = new()
    {
        Converters = { new TranslatedStringNewtonsoftJsonConverter() },
    };

    private static TranslatedString Rich() => new()
    {
        Entries =
        [
            new TranslatedStringEntry("en", "The Hague"),
            new TranslatedStringEntry("nl", "'s-Gravenhage")
            {
                Kind = TranslatedStringKind.Official,
                ValidFrom = new DateOnly(1806, 1, 1),
                ValidTo = new DateOnly(2100, 12, 31),
                Source = "BAG",
            },
            new TranslatedStringEntry("ru-Latn", "Gaaga") { Romanization = RomanizationOrigin.Generated },
            new TranslatedStringEntry("nl", "Den Haag") { Kind = TranslatedStringKind.Common },
        ],
    };

    private const string RichJson =
        """{"Entries":[{"Tag":"en","Value":"The Hague"},"""
        + """{"Tag":"nl","Value":"'s-Gravenhage","Kind":"official","ValidFrom":"1806-01-01","ValidTo":"2100-12-31","Source":"BAG"},"""
        + """{"Tag":"ru-Latn","Value":"Gaaga","Romanization":"generated"},"""
        + """{"Tag":"nl","Value":"Den Haag","Kind":"common"}]}""";

    private static readonly JsonSerializerOptions Relaxed = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // --- System.Text.Json (API) --------------------------------------------------------------

    [Fact]
    public void Writes_the_entries_shape_omitting_null_members()
        => System.Text.Json.JsonSerializer.Serialize(Rich(), Relaxed).Should().Be(RichJson);

    [Fact]
    public void Member_names_ignore_the_naming_policy_because_the_store_uses_the_same_names()
        => System.Text.Json.JsonSerializer.Serialize(Rich(), new JsonSerializerOptions(Relaxed) { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            .Should().Be(RichJson);

    [Fact]
    public void Round_trips_every_member()
        => System.Text.Json.JsonSerializer.Deserialize<TranslatedString>(RichJson).Should().Be(Rich());

    [Fact]
    public void A_key_only_value_is_written_and_read_as_a_string()
    {
        var key = TranslatedString.FromKey("model.Car.label");

        System.Text.Json.JsonSerializer.Serialize(key).Should().Be("\"model.Car.label\"");
        System.Text.Json.JsonSerializer.Deserialize<TranslatedString>("\"model.Car.label\"")!.Key.Should().Be("model.Car.label");
    }

    [Fact]
    public void The_flat_shorthand_is_read_as_plain_entries_in_canonical_case()
    {
        var read = System.Text.Json.JsonSerializer.Deserialize<TranslatedString>("""{"en":"Save","NL-be":"Bewaren","zh-hant":"儲存","fr":null}""")!;

        read.Entries.Should().Equal(
        [
            new TranslatedStringEntry("en", "Save"),
            new TranslatedStringEntry("nl-BE", "Bewaren"),
            new TranslatedStringEntry("zh-Hant", "儲存"),
        ]);
        read.Entries[1].Tag.Should().Be("nl-BE");
        read.Entries[2].Tag.Should().Be("zh-Hant");
    }

    [Fact]
    public void Entry_tags_are_canonicalized_on_read()
        => System.Text.Json.JsonSerializer.Deserialize<TranslatedString>("""{"Entries":[{"Tag":"SR-latn-rs","Value":"Beograd"}]}""")!
            .Entries.Single().Tag.Should().Be("sr-Latn-RS");

    [Theory]
    [InlineData("""{"Entries":[{"Tag":"en","Value":"a"}],"nl":"b"}""")]          // both shapes at once
    [InlineData("""{"en_US":"a"}""")]                                              // ill-formed tag, flat
    [InlineData("""{"Entries":[{"Tag":"en_US","Value":"a"}]}""")]                 // ill-formed tag, entry
    [InlineData("""{"Entries":[{"Value":"a"}]}""")]                               // no tag
    [InlineData("""{"Entries":[{"Tag":"en","Value":"a","Colour":"red"}]}""")]    // unknown member
    [InlineData("""{"Entries":[{"Tag":"en","Value":"a","Kind":"nickname"}]}""")] // unknown kind
    [InlineData("""{"Entries":[{"Tag":"en","Value":"a","Kind":"0"}]}""")]        // a kind is a name, not a number
    [InlineData("""{"Entries":[{"Tag":"en","Value":"a","ValidFrom":"01/02/2020"}]}""")]
    [InlineData("""{"en":42}""")]                                                 // a text is a string
    [InlineData("""[{"Tag":"en","Value":"a"}]""")]                                // a bare array is not the shape
    public void Malformed_input_is_refused(string json)
    {
        var act = () => System.Text.Json.JsonSerializer.Deserialize<TranslatedString>(json);

        act.Should().Throw<System.Text.Json.JsonException>();
    }

    [Fact]
    public void An_entry_without_a_value_is_dropped()
        => System.Text.Json.JsonSerializer.Deserialize<TranslatedString>("""{"Entries":[{"Tag":"en","Value":null},{"Tag":"nl","Value":"x"}]}""")!
            .Entries.Should().Equal([new TranslatedStringEntry("nl", "x")]);

    // --- Newtonsoft (RavenDB) ---------------------------------------------------------------

    [Fact]
    public void Newtonsoft_writes_exactly_what_System_Text_Json_writes()
        => JsonConvert.SerializeObject(Rich(), NewtonsoftSettings).Should().Be(RichJson);

    [Fact]
    public void Newtonsoft_reads_what_System_Text_Json_writes()
        => JsonConvert.DeserializeObject<TranslatedString>(System.Text.Json.JsonSerializer.Serialize(Rich()), NewtonsoftSettings)
            .Should().Be(Rich());

    [Fact]
    public void Newtonsoft_reads_validity_dates_a_date_parsing_reader_turned_into_DateTime()
    {
        var settings = new JsonSerializerSettings { DateParseHandling = DateParseHandling.DateTime, Converters = NewtonsoftSettings.Converters };

        JsonConvert.DeserializeObject<TranslatedString>(RichJson, settings).Should().Be(Rich());
    }

    [Fact]
    public void Newtonsoft_never_stores_a_key()
        => JsonConvert.SerializeObject(TranslatedString.FromKey("model.Car.label"), NewtonsoftSettings).Should().Be("null");

    [Fact]
    public void Newtonsoft_refuses_the_pre_X17_POCO_form()
    {
        // No backward compatibility (K3): the migrations rewrite it before anything loads it.
        var act = () => JsonConvert.DeserializeObject<TranslatedString>("""{"Translations":{"en":"a"},"Key":null}""", NewtonsoftSettings);

        act.Should().Throw<JsonSerializationException>();
    }

    [Fact]
    public void Newtonsoft_reads_the_flat_shorthand_too()
        => JsonConvert.DeserializeObject<TranslatedString>("""{"en":"Save","nl":"Opslaan"}""", NewtonsoftSettings)
            .Should().Be(TranslatedString.Of(("en", "Save"), ("nl", "Opslaan")));
}
