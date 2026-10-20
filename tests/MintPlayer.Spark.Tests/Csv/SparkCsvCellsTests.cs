using System.Text.Json;
using MintPlayer.Spark.Csv;

namespace MintPlayer.Spark.Tests.Csv;

/// <summary>Cells read as the JSON value of an attribute (warehouse WX11), and the header plan of an import.</summary>
public class SparkCsvCellsTests
{
    private static string Json(string cell, string dataType, bool isArray = false)
    {
        SparkCsvCells.TryConvert(cell, dataType, isArray, out var value, out _).Should().BeTrue();
        return JsonSerializer.Serialize(value);
    }

    private static string? Error(string cell, string dataType, bool isArray = false)
    {
        SparkCsvCells.TryConvert(cell, dataType, isArray, out _, out var error).Should().BeFalse();
        return error;
    }

    [Fact]
    public void Numbers_are_invariant_and_a_comma_decimal_is_refused_not_guessed()
    {
        Json("42", "number").Should().Be("42");
        Json(" -7 ", "number").Should().Be("-7");
        Json("12.50", "decimal").Should().Be("12.50");
        Json("'-3.5", "decimal").Should().Be("-3.5");   // a guarded cell is unguarded first
        Error("12,50", "decimal").Should().Contain("dot");
        Error("1.5", "number").Should().Contain("whole number");
        Error("1,234", "number").Should().NotBeNull();
    }

    [Theory]
    [InlineData("true", "true")]
    [InlineData("YES", "true")]
    [InlineData("1", "true")]
    [InlineData("false", "false")]
    [InlineData("no", "false")]
    [InlineData("0", "false")]
    public void Booleans(string cell, string expected) => Json(cell, "boolean").Should().Be(expected);

    [Fact]
    public void A_boolean_that_is_neither_is_refused() => Error("maybe", "boolean").Should().NotBeNull();

    [Fact]
    public void Dates_times_and_guids_are_checked_and_normalized()
    {
        Json("2026-10-19", "date").Should().Be("\"2026-10-19\"");
        Error("19/10/2026", "date").Should().Contain("yyyy-MM-dd");

        // An offset is converted to UTC; no offset means UTC (never the server's zone).
        Json("2026-10-19T10:30:00+02:00", "datetime").Should().Be("\"2026-10-19T08:30:00Z\"");
        Json("2026-10-19T08:30:00", "datetime").Should().Be("\"2026-10-19T08:30:00Z\"");
        Error("tomorrow", "datetime").Should().NotBeNull();

        Json("6F9619FF-8B86-D011-B42D-00CF4FC964FF", "guid").Should().Be("\"6f9619ff-8b86-d011-b42d-00cf4fc964ff\"");
        Error("not-a-guid", "guid").Should().NotBeNull();
    }

    [Fact]
    public void Text_and_references_are_strings_unguarded_and_untrimmed()
    {
        Json("'=1+1", "string").Should().Be("\"=1+1\"");
        Json(" padded ", "string").Should().Be("\" padded \"");
        Json("people/12", "Reference").Should().Be("\"people/12\"");
    }

    [Fact]
    public void Arrays_are_pipe_separated_or_json()
    {
        Json("a | b|c", "string", isArray: true).Should().Be("[\"a\",\"b\",\"c\"]");
        Json("1|2", "number", isArray: true).Should().Be("[1,2]");
        Json("[\"x|y\"]", "string", isArray: true).Should().Be("[\"x|y\"]");
        Error("1|two", "number", isArray: true).Should().NotBeNull();
    }

    [Fact]
    public void Complex_values_take_json_and_refuse_broken_json()
    {
        Json("{\"amount\":12.3,\"currency\":\"EUR\"}", "Money").Should().Be("{\"amount\":12.3,\"currency\":\"EUR\"}");
        Json("[{\"Line\":1}]", "AsDetail", isArray: true).Should().Be("[{\"Line\":1}]");
        Json("plain", "TranslatedString").Should().Be("\"plain\"");
        Error("{broken", "AsDetail").Should().Contain("JSON");
    }

    // ---------------------------------------------------------------- header plan

    [Fact]
    public void The_plan_finds_reserved_columns_and_maps_headers()
    {
        var (plan, error) = CsvImportPlan.Build(
            ["@ID", "Naam", "Ignored", "Age"],
            new Dictionary<string, string?> { ["naam"] = "Name", ["Ignored"] = null });

        error.Should().BeNull();
        plan!.IdColumn.Should().Be(0);
        plan.EtagColumn.HasValue.Should().BeFalse();
        plan.Width.Should().Be(4);
        plan.Attributes.Select(a => $"{a.Column}:{a.Name}").Should().Equal("1:Name", "3:Age");
    }

    [Theory]
    [InlineData("Name,name", "Two columns")]
    [InlineData("Name,", "no name")]
    [InlineData("@secret,Name", "not a column Spark knows")]
    public void A_header_spark_cannot_read_refuses_the_file(string header, string expected)
    {
        var (plan, error) = CsvImportPlan.Build(header.Split(','), mapping: null);

        plan.Should().BeNull();
        error.Should().Contain(expected);
    }
}
