using MintPlayer.Spark.Layering;

namespace MintPlayer.Spark.Tests.Layering;

/// <summary>The engine's JSON tree (composition D14): strict parsing, a deterministic writer.</summary>
public class SparkJsonTests
{
    [Theory]
    [InlineData("""{"a":1,"b":[true,false,null],"c":{"d":"e"}}""")]
    [InlineData("""{"n":-0.50e+3,"m":1.50,"z":0}""")]
    [InlineData("""["\"quoted\"","back\\slash","tab\tnew\nline","\u0001","é"]""")]
    [InlineData("""{}""")]
    [InlineData("""[]""")]
    public void Compact_json_round_trips_byte_for_byte(string json)
        => SparkJson.Write(SparkJson.Parse(json)).Should().Be(json);

    [Fact]
    public void Members_keep_their_order_and_whitespace_is_dropped()
        => SparkJson.Write(SparkJson.Parse("""
            { "z": 1,
              "a": [ 2, 3 ] }
            """)).Should().Be("""{"z":1,"a":[2,3]}""");

    [Fact]
    public void Escapes_are_read_and_slash_and_unicode_are_written_plainly()
        => SparkJson.Write(SparkJson.Parse("""["\/éA"]""")).Should().Be("""["/éA"]""");

    [Fact]
    public void The_indented_writer_is_deterministic()
        => SparkJson.Write(SparkJson.Parse("""{"a":[1,{"b":2}],"c":{}}"""), indented: true)
            .Should().Be("{\n  \"a\": [\n    1,\n    {\n      \"b\": 2\n    }\n  ],\n  \"c\": {}\n}");

    [Theory]
    [InlineData("""{ "a": 1, "a": 2 }""", "'a' is stated twice")]
    [InlineData("""{ "o": { "a": 1, "a": 1 } }""", "'o.a' is stated twice")]
    [InlineData("""{ "a": 1, }""", "expected a property name")]
    [InlineData("""[1, 2,]""", "unexpected character ']'")]
    [InlineData("""{ "a": 1 } // comment""", "unexpected content after the JSON value")]
    [InlineData("""{ /* c */ "a": 1 }""", "expected a property name")]
    [InlineData("""{ 'a': 1 }""", "expected a property name")]
    [InlineData("""{ "a": 01 }""", "a number cannot start with 0")]
    [InlineData("""{ "a": 1. }""", "invalid number")]
    [InlineData("""{ "a": tru }""", "unexpected token")]
    [InlineData("""{ "a": "x""", "unterminated string")]
    [InlineData("""{ "a": "\x" }""", "invalid escape")]
    [InlineData("""{ "a": 1 """, "unexpected end of input in an object")]
    [InlineData("", "unexpected end of input")]
    public void Invalid_json_is_refused_saying_why(string json, string problem)
    {
        var act = () => SparkJson.Parse(json);

        act.Should().Throw<SparkJsonException>().Which.Message.Should().Contain(problem).And.Contain("line 1");
    }

    [Fact]
    public void A_raw_control_character_in_a_string_is_refused()
    {
        var act = () => SparkJson.Parse("\"a\tb\"");

        act.Should().Throw<SparkJsonException>().Which.Message.Should().Contain("control character");
    }

    [Fact]
    public void The_error_names_the_line_and_column()
    {
        var act = () => SparkJson.Parse("{\n  \"a\": 1,\n  \"a\": 2\n}");

        act.Should().Throw<SparkJsonException>().Which.Message.Should().Contain("line 3, column 3");
    }

    [Fact]
    public void Deep_equality_ignores_member_order_and_number_spelling_but_not_array_order()
    {
        SparkJsonNode.DeepEquals(SparkJson.Parse("""{"a":1,"b":2}"""), SparkJson.Parse("""{"b":2.0,"a":1}""")).Should().BeTrue();
        SparkJsonNode.DeepEquals(SparkJson.Parse("[1,2]"), SparkJson.Parse("[2,1]")).Should().BeFalse();
        SparkJsonNode.DeepEquals(SparkJson.Parse("\"1\""), SparkJson.Parse("1")).Should().BeFalse();
    }

    [Fact]
    public void A_layer_that_is_not_an_object_is_refused_naming_the_layer()
    {
        var notJson = () => SparkLayer.Parse("App_Data/actions.json", "{ not valid", isLibrary: false);
        var notObject = () => SparkLayer.Parse("App_Data/actions.json", "[]", isLibrary: false);

        notJson.Should().Throw<SparkLayerException>().WithMessage("App_Data/actions.json is not valid JSON*");
        notObject.Should().Throw<SparkLayerException>().WithMessage("App_Data/actions.json must be a JSON object*");
    }
}
