using Xunit;

namespace CodeCoverage.Tests;

/// <summary>A confirmation's message is HTML (#554, owner 2026-11-06): our own markup stays, every value is encoded.</summary>
public class HtmlMessageTests
{
    [Fact]
    public void A_section_encodes_the_heading_and_every_item()
    {
        HtmlMessage.Section("Files & <dirs>:", [".github/workflows/a.yml", "<script>alert(1)</script>"])
            .Should().Be("Files &amp; &lt;dirs&gt;:<ul><li>.github/workflows/a.yml</li><li>&lt;script&gt;alert(1)&lt;/script&gt;</li></ul>");
    }

    [Fact]
    public void A_nested_list_keeps_its_markup_and_a_line_ends_with_a_break()
    {
        HtmlMessage.List([HtmlMessage.Encode("acme/widget") + HtmlMessage.List([HtmlMessage.Encode("Create \"x\"")])])
            .Should().Be("<ul><li>acme/widget<ul><li>Create &quot;x&quot;</li></ul></li></ul>");
        HtmlMessage.Line("a < b").Should().Be("a &lt; b<br>");
    }
}
