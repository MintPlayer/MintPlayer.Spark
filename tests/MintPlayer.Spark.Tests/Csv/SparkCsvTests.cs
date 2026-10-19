using System.Text;
using System.Text.Json;
using MintPlayer.Spark.Abstractions.Csv;

namespace MintPlayer.Spark.Tests.Csv;

/// <summary>
/// The shared CSV codec (warehouse WX11): RFC 4180 parsing edge cases, delimiter detection, invariant formatting and
/// the CSV-injection guard. Pure; no host.
/// </summary>
public class SparkCsvTests
{
    // ---------------------------------------------------------------- parsing

    [Fact]
    public void Quoted_fields_hold_the_delimiter_and_doubled_quotes()
    {
        var records = SparkCsv.Parse("a,\"b,\"\"c\"\"\",d\r\n", ',');

        records.Should().ContainSingle();
        records[0].Should().Equal("a", "b,\"c\"", "d");
    }

    [Fact]
    public void A_quoted_field_holds_line_breaks_of_every_kind()
    {
        var records = SparkCsv.Parse("name,note\r\nx,\"line 1\r\nline 2\nline 3\"\r\ny,z", ',');

        records.Should().HaveCount(3);
        records[1].Should().Equal("x", "line 1\r\nline 2\nline 3");
        records[2].Should().Equal("y", "z");
    }

    [Fact]
    public void Crlf_lf_and_a_lone_cr_all_end_a_record()
    {
        var records = SparkCsv.Parse("a\r\nb\nc\rd", ',');

        records.Select(r => r.Single()).Should().Equal("a", "b", "c", "d");
    }

    [Fact]
    public void A_trailing_line_break_adds_no_record_but_a_trailing_delimiter_adds_an_empty_field()
    {
        SparkCsv.Parse("a,b\r\n", ',').Should().ContainSingle();
        SparkCsv.Parse("a,\r\n", ',').Single().Should().Equal("a", "");
        SparkCsv.Parse("", ',').Should().BeEmpty();
    }

    [Fact]
    public void An_empty_quoted_field_is_a_field()
    {
        SparkCsv.Parse("\"\"", ',').Single().Should().Equal("");
        SparkCsv.Parse("a,\"\",c", ',').Single().Should().Equal("a", "", "c");
    }

    [Fact]
    public void A_leading_bom_is_skipped()
    {
        var records = SparkCsv.Parse("﻿name,age\r\nAnn,3", ',');

        records[0].Should().Equal("name", "age");
    }

    [Fact]
    public void Blank_lines_in_the_middle_are_blank_records()
    {
        var records = SparkCsv.Parse("a\r\n\r\nb", ',');

        records.Should().HaveCount(3);
        SparkCsv.IsBlank(records[1]).Should().BeTrue();
        SparkCsv.IsBlank(records[0]).Should().BeFalse();
    }

    [Fact]
    public void Semicolon_files_from_belgian_excel_parse_with_commas_as_text()
    {
        var records = SparkCsv.Parse("naam;prijs\r\n\"Jansen; Piet\";12,50\r\n", ';');

        records[1].Should().Equal("Jansen; Piet", "12,50");
    }

    [Fact]
    public void A_quote_or_line_break_cannot_be_the_delimiter()
    {
        var act = () => SparkCsv.Parse("a", '"');

        act.Should().Throw<ArgumentException>();
    }

    // ---------------------------------------------------------------- delimiter detection

    [Theory]
    [InlineData("a,b,c\r\n1;2", ',')]
    [InlineData("a;b;c\r\n1,2,3,4,5", ';')]
    [InlineData("a\tb\tc", '\t')]
    [InlineData("single", ',')]
    [InlineData("﻿a;b", ';')]
    // Delimiters inside quotes do not count.
    [InlineData("\"a,b,c,d\";x;y", ';')]
    public void The_delimiter_is_detected_from_the_header(string text, char expected)
    {
        SparkCsv.DetectDelimiter(text).Should().Be(expected);
    }

    // ---------------------------------------------------------------- writing + injection

    [Theory]
    [InlineData("=SUM(A1:A9)", "'=SUM(A1:A9)")]
    [InlineData("+31 6 1234", "'+31 6 1234")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("\tx", "'\tx")]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    public void Text_starting_like_a_formula_is_guarded(string value, string expected)
    {
        SparkCsv.Guard(value).Should().Be(expected);
        SparkCsv.Unguard(expected).Should().Be(value);
    }

    [Fact]
    public void An_apostrophe_not_before_a_formula_start_is_kept_on_the_way_in()
    {
        SparkCsv.Unguard("'quoted'").Should().Be("'quoted'");
    }

    [Fact]
    public void A_guarded_cell_that_needs_quotes_is_guarded_then_quoted()
    {
        SparkCsv.Field("=HYPERLINK(\"http://x\",\"y\")").Should().Be("\"'=HYPERLINK(\"\"http://x\"\",\"\"y\"\")\"");
    }

    [Fact]
    public void Numbers_are_never_guarded_so_a_negative_number_stays_a_number()
    {
        SparkCsv.Cell(-12.5m).Should().Be("-12.5");
        SparkCsv.Cell(-3).Should().Be("-3");
        SparkCsv.Cell(JsonSerializer.SerializeToElement(-7)).Should().Be("-7");
        SparkCsv.Cell(JsonSerializer.SerializeToElement("-7")).Should().Be("'-7");
    }

    [Fact]
    public void The_semicolon_delimiter_quotes_semicolons_and_leaves_commas()
    {
        SparkCsv.Field("a;b", ';').Should().Be("\"a;b\"");
        SparkCsv.Field("a,b", ';').Should().Be("a,b");
        SparkCsv.Cell(1.5m, ';').Should().Be("1.5");
    }

    [Fact]
    public void Values_are_culture_invariant()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("nl-BE");
            SparkCsv.FormatValue(1234.5m).Should().Be("1234.5");
            SparkCsv.FormatValue(0.1d).Should().Be("0.1");
            SparkCsv.FormatValue(new DateOnly(2026, 10, 19)).Should().Be("2026-10-19");
            SparkCsv.FormatValue(new DateTimeOffset(2026, 10, 19, 10, 30, 0, TimeSpan.FromHours(2))).Should().Be("2026-10-19T08:30:00Z");
            SparkCsv.FormatValue(true).Should().Be("true");
            SparkCsv.FormatValue(null).Should().Be("");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Json_values_unwrap_strings_and_keep_structures_compact()
    {
        SparkCsv.FormatValue(JsonSerializer.SerializeToElement("text")).Should().Be("text");
        SparkCsv.FormatValue(JsonSerializer.SerializeToElement(new[] { "a", "b" })).Should().Be("[\"a\",\"b\"]");
        SparkCsv.FormatValue(JsonDocument.Parse("null").RootElement).Should().Be("");
    }

    [Fact]
    public void The_writer_uses_crlf_and_a_bom_when_asked()
    {
        var writer = new SparkCsvWriter(';', includeBom: true)
            .Header("name", "note")
            .Row("Ann", "two\nlines");

        writer.ToString().Should().Be("name;note\r\nAnn;\"two\nlines\"\r\n");
        writer.Records.Should().Be(2);
        var bytes = writer.ToBytes();
        bytes.Take(3).Should().Equal(Encoding.UTF8.GetPreamble());
        new SparkCsvWriter(includeBom: false).Header("x").ToBytes()[0].Should().Be((byte)'x');
    }

    [Fact]
    public void What_the_writer_writes_parses_back_to_the_same_text()
    {
        string[] tricky = ["plain", "a,b", "say \"hi\"", "multi\r\nline", "=1+1", "", " padded "];
        var writer = new SparkCsvWriter(',', includeBom: true).Row(tricky.Cast<object?>());

        var parsed = SparkCsv.Parse(Encoding.UTF8.GetString(writer.ToBytes()), ',').Single();

        parsed.Select(SparkCsv.Unguard).Should().Equal(tricky);
    }
}
