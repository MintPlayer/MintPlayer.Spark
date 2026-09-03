using CodeCoverage.Badges;
using Xunit;

namespace CodeCoverage.Tests.Badges;

/// <summary>
/// The badge had no tests at all until this file: not the colour scale, not the
/// "unknown" semantics, not the escaping. The three rules the endpoint depends
/// on were enforced by a code comment and nothing else.
/// </summary>
public class BadgeRendererTests
{
    private static string ValueOf(string svg)
    {
        // The value text is rendered twice (shadow + face); both copies carry it.
        var title = svg[(svg.IndexOf("<title>", StringComparison.Ordinal) + 7)..];
        return title[..title.IndexOf("</title>", StringComparison.Ordinal)];
    }

    [Theory]
    [InlineData(100, "#4c1")]
    [InlineData(90, "#4c1")]
    [InlineData(89.9, "#97ca00")]
    [InlineData(80, "#97ca00")]
    [InlineData(79.9, "#a4a61d")]
    [InlineData(70, "#a4a61d")]
    [InlineData(69.9, "#dfb317")]
    [InlineData(60, "#dfb317")]
    [InlineData(59.9, "#fe7d37")]
    [InlineData(50, "#fe7d37")]
    [InlineData(49.9, "#e05d44")]
    [InlineData(0, "#e05d44")]
    public void Colour_stops_sit_exactly_on_their_boundaries(double percent, string expected)
        => BadgeRenderer.Coverage(percent).Should().Contain($"fill=\"{expected}\"");

    /// <summary>
    /// Null is the "no data" case the never-404 contract depends on: an unknown
    /// repository, a wrong token, a branch nobody has uploaded for. It must be
    /// visibly grey rather than a plausible 0%.
    /// </summary>
    [Fact]
    public void Null_renders_grey_unknown_not_zero()
    {
        var svg = BadgeRenderer.Coverage(null);
        svg.Should().Contain("fill=\"#9f9f9f\"");
        ValueOf(svg).Should().Be("coverage: unknown");
        // On the value text, not the whole document: the gradient's y2="100%"
        // contains "0%" and says nothing about coverage.
        ValueOf(svg).Should().NotContain("%");
    }

    [Fact]
    public void Percentages_render_with_at_most_one_decimal_invariantly()
    {
        ValueOf(BadgeRenderer.Coverage(48.7)).Should().Be("coverage: 48.7%");
        ValueOf(BadgeRenderer.Coverage(59.94)).Should().Be("coverage: 59.9%");
        // 59.9 -> "60" via "0.#": the shipped format, asserted so a future
        // change to it is a deliberate one.
        ValueOf(BadgeRenderer.Coverage(59.95)).Should().Be("coverage: 60%");
        ValueOf(BadgeRenderer.Coverage(100)).Should().Be("coverage: 100%");
    }

    /// <summary>
    /// The partial label is what stops a subset's total from reading as the
    /// whole repository's number — see LoadSelectorCoverage.
    /// </summary>
    [Fact]
    public void Partial_assemblies_say_so_in_the_label()
    {
        var svg = BadgeRenderer.Coverage(71.4, partial: true);
        ValueOf(svg).Should().Be("coverage (partial): 71.4%");
        // Still coloured by the number, not by its completeness.
        svg.Should().Contain("fill=\"#a4a61d\"");
    }

    /// <summary>
    /// "unknown" has no number to qualify, so partial must not leak a label
    /// claiming a partial measurement exists.
    /// </summary>
    [Fact]
    public void Partial_is_ignored_when_there_is_no_number()
        => ValueOf(BadgeRenderer.Coverage(null, partial: true)).Should().Be("coverage: unknown");

    /// <summary>
    /// The real control is that the label comes from a closed set of constants,
    /// so nothing caller-supplied reaches the SVG. This asserts the second line
    /// of defence: whatever does reach it is escaped, and the badge stays
    /// well-formed XML.
    /// </summary>
    [Fact]
    public void Rendered_svg_is_well_formed_and_carries_no_unescaped_markup()
    {
        foreach (var svg in new[]
        {
            BadgeRenderer.Coverage(null),
            BadgeRenderer.Coverage(0),
            BadgeRenderer.Coverage(100, partial: true),
        })
        {
            // Throws on malformed XML, which is the actual invariant.
            var doc = System.Xml.Linq.XDocument.Parse(svg);
            doc.Root!.Name.LocalName.Should().Be("svg");
            // No stray angle bracket smuggled through a text node.
            foreach (var text in doc.Descendants().Where(d => !d.HasElements))
                text.Value.Should().NotContain("<");
        }
    }

    /// <summary>
    /// Width is approximated at 6.6px/char; the longer partial label must widen
    /// the badge rather than overflow the label rectangle it is centred in.
    /// </summary>
    [Fact]
    public void Longer_partial_label_widens_the_badge()
    {
        static int WidthOf(string svg)
        {
            var at = svg.IndexOf("width=\"", StringComparison.Ordinal) + 7;
            return int.Parse(svg[at..svg.IndexOf('"', at)]);
        }

        WidthOf(BadgeRenderer.Coverage(71.4, partial: true))
            .Should().BeGreaterThan(WidthOf(BadgeRenderer.Coverage(71.4)));
    }
}
