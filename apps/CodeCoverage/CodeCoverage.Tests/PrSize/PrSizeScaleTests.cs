using System.Drawing;
using CodeCoverage.Entities;
using CodeCoverage.PrSize;
using Xunit;

namespace CodeCoverage.Tests.PrSize;

/// <summary>Which row a size falls in, and the label the forge gets for it (docs/coverage_pr_size_labels_PRD.md D2).</summary>
public class PrSizeScaleTests
{
    private static readonly PrSizeScale Defaults = new(PrSizeDefaults.Scale());

    [Theory]
    [InlineData(0, "size/XS")]
    [InlineData(9, "size/XS")]
    [InlineData(10, "size/S")]
    [InlineData(29, "size/S")]
    [InlineData(30, "size/M")]
    [InlineData(999, "size/XL")]
    [InlineData(1000, "size/XXL")]
    [InlineData(250_000, "size/XXL")]
    public void Boundaries_on_the_pascalgn_defaults(int size, string label)
        => Defaults.LabelFor(size)!.Name.Should().Be(label);

    [Fact]
    public void A_single_row_scale_labels_everything()
    {
        var scale = new PrSizeScale([new PrSizeLabel { Id = "any", Name = "sized", MinLines = 0 }]);

        scale.LabelFor(0)!.Name.Should().Be("sized");
        scale.LabelFor(100_000)!.Name.Should().Be("sized");
        scale.LowestRowCeiling.HasValue.Should().BeFalse();
        scale.GeneratedDescription(scale.Rows[0]).Should().Be("Denotes a PR that changes 0+ lines");
    }

    [Fact]
    public void An_empty_scale_has_no_label()
        => new PrSizeScale([]).LabelFor(5).Should().BeNull();

    [Fact]
    public void Rows_are_ordered_by_threshold_whatever_the_stored_order()
    {
        var scale = new PrSizeScale(PrSizeDefaults.Scale().AsEnumerable().Reverse());

        scale.LabelFor(45)!.Name.Should().Be("size/M");
        scale.LowestRowCeiling.Should().Be(10);
    }

    [Fact]
    public void Generated_descriptions_follow_the_next_row()
    {
        var m = Defaults.Rows.Single(r => r.Name == "size/M");
        var xxl = Defaults.Rows.Single(r => r.Name == "size/XXL");

        Defaults.Desired(m).Description.Should().Be("Denotes a PR that changes 30-99 lines");
        Defaults.Desired(xxl).Description.Should().Be("Denotes a PR that changes 1000+ lines");
    }

    [Fact]
    public void A_written_description_is_kept()
    {
        var scale = new PrSizeScale([new PrSizeLabel { Id = "a", Name = "tiny", MinLines = 0, Description = "  Small  " }]);

        scale.Desired(scale.Rows[0]).Description.Should().Be("Small");
    }

    [Fact]
    public void A_colour_with_alpha_is_sent_as_six_digits()
    {
        var row = new PrSizeLabel { Id = "a", Name = "x", MinLines = 0, Color = Color.FromArgb(0x80, 0xEE, 0xBB, 0x00) };

        new PrSizeScale([row]).Desired(row).Color.Should().Be("eebb00");
    }
}
