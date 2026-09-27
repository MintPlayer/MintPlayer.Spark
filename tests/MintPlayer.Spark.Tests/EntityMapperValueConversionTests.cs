using System.Drawing;
using System.Globalization;
using System.Text.Json;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests;

/// <summary>
/// The write half of <see cref="EntityMapper"/>: how a wire value (a <see cref="JsonElement"/>, a string,
/// or an in-process CLR value) is coerced onto an entity property.
/// </summary>
/// <remarks>
/// Two defects are pinned here. String parses used the <em>server's</em> culture, so the same request
/// meant a different date on a French host; and a value that could not be converted was swallowed, so
/// the save reported success while the field kept its old value.
/// </remarks>
public class EntityMapperValueConversionTests
{
    private readonly EntityMapper _mapper;

    public EntityMapperValueConversionTests()
    {
        var guard = Substitute.For<ICollectionGuard>();
        guard.BelongsToAuthorizedCollection(default!, default!, default!).ReturnsForAnyArgs(true);
        _mapper = new EntityMapper(Substitute.For<IModelLoader>(), collectionGuard: guard);
    }

    public enum Priority { Low, High }

    public sealed class Owner
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
    }

    public sealed class Sample
    {
        public string? Id { get; set; }
        public string? Text { get; set; }
        public Guid Key { get; set; }
        public DateTime When { get; set; }
        public DateOnly Day { get; set; }
        public Priority Level { get; set; }
        public int Count { get; set; }
        public long Big { get; set; }
        public decimal Amount { get; set; }
        public double Ratio { get; set; }
        public bool Flag { get; set; }
        public int? Maybe { get; set; }
        public Color Tint { get; set; }
        public TranslatedString? Title { get; set; }
        public List<string>? Tags { get; set; }
        public string[]? Codes { get; set; }
        public Owner? Holder { get; set; }
    }

    private static PersistentObject Po(string name, object? value, string dataType = "string", bool isArray = false) => new()
    {
        Name = nameof(Sample),
        ObjectTypeId = Guid.NewGuid(),
        Attributes =
        [
            new PersistentObjectAttribute { Name = name, Value = value, DataType = dataType, IsArray = isArray },
        ],
    };

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private Sample Write(string name, object? value, string dataType = "string", bool isArray = false, Sample? into = null,
        Dictionary<string, object>? included = null)
    {
        var sample = into ?? new Sample();
        _mapper.PopulateObjectValues(Po(name, value, dataType, isArray), sample, included);
        return sample;
    }

    // --- scalars off the wire ------------------------------------------------

    [Fact]
    public void Wire_scalars_are_coerced_onto_their_property_types()
    {
        Write(nameof(Sample.Key), "6f9619ff-8b86-d011-b42d-00cf4fc964ff").Key
            .Should().Be(Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff"));
        Write(nameof(Sample.Count), Json("42")).Count.Should().Be(42);
        Write(nameof(Sample.Big), Json("9000000000")).Big.Should().Be(9_000_000_000L);
        Write(nameof(Sample.Amount), Json("12.75")).Amount.Should().Be(12.75m);
        Write(nameof(Sample.Flag), Json("true")).Flag.Should().BeTrue();
        Write(nameof(Sample.Flag), Json("false"), into: new Sample { Flag = true }).Flag.Should().BeFalse();
        Write(nameof(Sample.Text), Json("7")).Text.Should().Be("7");
        Write(nameof(Sample.Text), Json("\"plain\"")).Text.Should().Be("plain");
        Write(nameof(Sample.Tint), "#FF0000").Tint.ToArgb().Should().Be(Color.Red.ToArgb());
        Write(nameof(Sample.Day), "2026-04-03").Day.Should().Be(new DateOnly(2026, 4, 3));
        Write(nameof(Sample.Level), Priority.High).Level.Should().Be(Priority.High);
    }

    [Fact]
    public void A_json_null_clears_a_nullable_property_and_leaves_a_value_type_alone()
    {
        Write(nameof(Sample.Maybe), Json("null"), into: new Sample { Maybe = 5 }).Maybe.HasValue.Should().BeFalse();
        Write(nameof(Sample.Count), Json("null"), into: new Sample { Count = 5 }).Count.Should().Be(5);
    }

    [Theory]
    [InlineData("high")]
    [InlineData("HIGH")]
    [InlineData("High")]
    public void An_enum_is_parsed_case_insensitively_as_the_query_filter_does(string wire)
    {
        Write(nameof(Sample.Level), wire).Level.Should().Be(Priority.High);
    }

    // --- culture ---------------------------------------------------------------

    [Fact]
    public void Dates_and_numbers_are_read_the_same_whatever_the_server_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            // fr-FR reads 04/03/2026 as the 4th of March, and "1.5" is not a number there at all.
            Write(nameof(Sample.When), "04/03/2026 10:30:00").When.Should().Be(new DateTime(2026, 4, 3, 10, 30, 0));
            Write(nameof(Sample.Day), "04/03/2026").Day.Should().Be(new DateOnly(2026, 4, 3));
            Write(nameof(Sample.Ratio), "1.5").Ratio.Should().Be(1.5);
            Write(nameof(Sample.Amount), "2.25").Amount.Should().Be(2.25m);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // --- values that cannot be converted --------------------------------------

    [Theory]
    [InlineData(nameof(Sample.Key), "not-a-guid")]
    [InlineData(nameof(Sample.When), "yesterday-ish")]
    [InlineData(nameof(Sample.Day), "32/13/2026")]
    [InlineData(nameof(Sample.Level), "Urgent")]
    [InlineData(nameof(Sample.Count), "many")]
    [InlineData(nameof(Sample.Count), "99999999999")]
    public void A_value_that_cannot_be_converted_is_a_validation_error_not_a_silent_skip(string property, string wire)
    {
        var act = () => Write(property, wire);

        act.Should().Throw<SparkValidationException>()
            .Which.AttributeName.Should().Be(property, "the error has to name the field the user got wrong");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_string_leaves_the_value_as_it_was(string wire)
    {
        // What a cleared input sends. Refusing it would turn "left blank" into an error.
        var key = Guid.NewGuid();

        Write(nameof(Sample.Key), wire, into: new Sample { Key = key }).Key.Should().Be(key);
    }

    [Fact]
    public void An_id_the_key_property_cannot_hold_is_ignored_rather_than_refused()
    {
        var po = Po(nameof(Sample.Text), "x");
        po.Id = "samples/1";

        var holder = new GuidKeyed();
        _mapper.PopulateObjectValues(po, holder);

        holder.Id.Should().Be(Guid.Empty);
        holder.Text.Should().Be("x");
    }

    public sealed class GuidKeyed
    {
        public Guid Id { get; set; }
        public string? Text { get; set; }
    }

    // --- TranslatedString --------------------------------------------------------

    public static TheoryData<object?, string?> TranslatedInputs() => new()
    {
        { new Dictionary<string, string> { ["en"] = "Hello", ["nl"] = "Hallo" }, "Hello|Hallo" },
        { new Dictionary<string, object?> { ["en"] = "Hello", ["nl"] = 5, ["fr"] = null }, "Hello|5" },
        { """{ "en": "Hello", "nl": "Hallo" }""", "Hello|Hallo" },
        { Json("""{ "en": "Hello", "nl": 5, "fr": null }"""), "Hello|5" },
        { TranslatedString.Create("Hello"), "Hello|" },
    };

    [Theory]
    [MemberData(nameof(TranslatedInputs))]
    public void A_translated_string_is_read_from_every_wire_shape(object? raw, string expected)
    {
        var title = Write(nameof(Sample.Title), raw, dataType: "TranslatedString").Title;

        title.Should().NotBeNull();
        var parts = expected.Split('|');
        title!.Translations["en"].Should().Be(parts[0]);
        if (parts[1].Length > 0)
            title.Translations["nl"].Should().Be(parts[1]);
        title.Translations.Should().NotContainKey("fr", "a null language is absent, not an empty string");
    }

    public static TheoryData<object?> ClearingInputs() => new()
    {
        null,
        Json("null"),
        Json("{}"),
        Json("\"a string\""),
        new Dictionary<string, string>(),
        new Dictionary<string, object?> { ["en"] = null },
        "{ not json",
        "   ",
        42,
    };

    [Theory]
    [MemberData(nameof(ClearingInputs))]
    public void A_translated_string_with_nothing_readable_clears_the_property(object? raw)
    {
        var sample = new Sample { Title = TranslatedString.Create("Old") };

        Write(nameof(Sample.Title), raw, dataType: "TranslatedString", into: sample).Title.Should().BeNull();
    }

    [Fact]
    public void Languages_absent_from_the_incoming_value_survive_on_the_entity()
    {
        var existing = new TranslatedString();
        existing.Translations["en"] = "Old";
        existing.Translations["fr"] = "Ancien";

        var title = Write(nameof(Sample.Title), """{ "en": "New" }""", dataType: "TranslatedString",
            into: new Sample { Title = existing }).Title!;

        title.Translations["en"].Should().Be("New");
        title.Translations["fr"].Should().Be("Ancien");
    }

    // --- collections -------------------------------------------------------------

    [Fact]
    public void A_json_array_is_deserialized_onto_a_collection_property()
    {
        Write(nameof(Sample.Tags), Json("""["a", "b"]"""), isArray: true).Tags.Should().Equal("a", "b");
    }

    [Fact]
    public void An_in_process_collection_is_coerced_onto_a_different_collection_type()
    {
        Write(nameof(Sample.Codes), new List<string> { "x", "y" }, isArray: true).Codes.Should().Equal("x", "y");
    }

    [Fact]
    public void An_array_that_does_not_fit_the_property_is_skipped()
    {
        var sample = new Sample { Tags = ["kept"] };

        Write(nameof(Sample.Tags), Json("""[{ "nested": true }]"""), isArray: true, into: sample).Tags.Should().Equal("kept");
        Write(nameof(Sample.Tags), new List<object> { new { nested = true } }, isArray: true, into: sample).Tags.Should().Equal("kept");
    }

    // --- references --------------------------------------------------------------

    public static TheoryData<object> ReferenceIds() => new()
    {
        "owners/1",
        Json("\"owners/1\""),
    };

    [Theory]
    [MemberData(nameof(ReferenceIds))]
    public void A_complex_reference_resolves_from_the_included_documents(object refId)
    {
        var owner = new Owner { Id = "owners/1", Name = "Included" };

        var sample = Write(nameof(Sample.Holder), refId, dataType: "Reference",
            included: new Dictionary<string, object> { ["owners/1"] = owner });

        sample.Holder.Should().BeSameAs(owner);
    }

    [Fact]
    public void A_numeric_reference_id_is_read_as_its_text()
    {
        var owner = new Owner { Id = "17" };

        Write(nameof(Sample.Holder), Json("17"), dataType: "Reference",
            included: new Dictionary<string, object> { ["17"] = owner }).Holder.Should().BeSameAs(owner);
    }

    public static TheoryData<object?> EmptyReferences() => new() { null, "", Json("null") };

    [Theory]
    [MemberData(nameof(EmptyReferences))]
    public void An_empty_reference_clears_the_property(object? refId)
    {
        var sample = new Sample { Holder = new Owner { Id = "owners/9" } };

        Write(nameof(Sample.Holder), refId, dataType: "Reference", into: sample).Holder.Should().BeNull();
    }

    [Fact]
    public void An_unresolvable_complex_reference_without_a_session_throws()
    {
        var act = () => Write(nameof(Sample.Holder), "owners/404", dataType: "Reference");

        act.Should().Throw<InvalidOperationException>().WithMessage("*could not be resolved*");
    }
}
