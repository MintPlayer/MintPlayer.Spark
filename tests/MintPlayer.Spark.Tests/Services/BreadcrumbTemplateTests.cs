using MintPlayer.Spark.Services.Breadcrumb;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Pins the breadcrumb template grammar: literal text + <c>{Attribute}</c> placeholders,
/// with <c>{{</c>/<c>}}</c> escaping. The single decomposition used by model-sync validation
/// and the runtime resolver.
/// </summary>
public class BreadcrumbTemplateTests
{
    [Fact]
    public void Empty_template_yields_no_tokens()
    {
        BreadcrumbTemplate.Parse("").Should().BeEmpty();
    }

    [Fact]
    public void Pure_literal_is_one_literal_token()
    {
        BreadcrumbTemplate.Parse("hello world").Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new LiteralToken("hello world"));
    }

    [Fact]
    public void Mixed_literals_and_fields_parse_in_order()
    {
        var tokens = BreadcrumbTemplate.Parse("{ParkedCar} ({Coordinates})");

        tokens.Should().HaveCount(4);
        tokens[0].Should().BeOfType<FieldToken>().Which.AttributeName.Should().Be("ParkedCar");
        tokens[1].Should().BeEquivalentTo(new LiteralToken(" ("));
        tokens[2].Should().BeOfType<FieldToken>().Which.AttributeName.Should().Be("Coordinates");
        tokens[3].Should().BeEquivalentTo(new LiteralToken(")"));
    }

    [Fact]
    public void Escaped_braces_become_literal_braces()
    {
        var tokens = BreadcrumbTemplate.Parse("{{not a field}}");

        tokens.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new LiteralToken("{not a field}"));
    }

    [Fact]
    public void Field_names_are_trimmed_and_distinct()
    {
        BreadcrumbTemplate.FieldNames("{ A } {A} {B}").Should().Equal("A", "B");
    }

    [Theory]
    [InlineData("{Unterminated")]
    [InlineData("{}")]
    [InlineData("{  }")]
    [InlineData("dangling }")]
    public void Malformed_templates_throw_FormatException(string template)
    {
        var act = () => BreadcrumbTemplate.Parse(template);
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Escaped_closing_braces_become_a_literal_closing_brace()
    {
        // A standalone "}}" outside any placeholder is the escape for a literal '}'.
        BreadcrumbTemplate.Parse("a}}b").Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new LiteralToken("a}b"));
    }

    [Fact]
    public void Field_only_template_is_a_single_field_token()
    {
        var tokens = BreadcrumbTemplate.Parse("{Name}");

        tokens.Should().ContainSingle()
            .Which.Should().BeOfType<FieldToken>()
            .Which.AttributeName.Should().Be("Name");
    }

    [Fact]
    public void FieldNames_preserves_first_occurrence_order_and_is_distinct()
    {
        // B appears before A's second occurrence; distinct keeps the first-seen order.
        BreadcrumbTemplate.FieldNames("{B} {A} {B} {A}").Should().Equal("B", "A");
    }

    [Fact]
    public void FieldNames_on_a_literal_only_template_is_empty()
    {
        BreadcrumbTemplate.FieldNames("just text {{escaped}}").Should().BeEmpty();
    }

    [Fact]
    public void Nested_open_brace_inside_a_placeholder_throws_FormatException()
    {
        // "{a{b}" — the inner '{' is detected inside the placeholder name.
        var act = () => BreadcrumbTemplate.Parse("{a{b}");
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Escaped_braces_adjacent_to_a_field_parse_into_separate_tokens()
    {
        // "{{" -> literal "{", then {Name} field, then "}}" -> literal "}".
        var tokens = BreadcrumbTemplate.Parse("{{{Name}}}");

        tokens.Should().HaveCount(3);
        tokens[0].Should().BeEquivalentTo(new LiteralToken("{"));
        tokens[1].Should().BeOfType<FieldToken>().Which.AttributeName.Should().Be("Name");
        tokens[2].Should().BeEquivalentTo(new LiteralToken("}"));
    }
}
